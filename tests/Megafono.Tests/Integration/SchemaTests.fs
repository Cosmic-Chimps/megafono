module Megafono.Tests.Integration.SchemaTests

open System
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport

let private expectedTables =
    [ "activities"; "app_events"; "deliveries"; "follows"; "instance_actor"; "local_actors"; "objects"
      "processed_activities"; "remote_actors"; "remote_hosts" ]

let private withDatabase (test: NpgsqlDataSource -> string -> Task) : Task =
    task {
        let container = PostgreSqlBuilder("postgres:17-alpine").Build()
        do! container.StartAsync()

        try
            let connectionString = container.GetConnectionString()
            use dataSource = NpgsqlDataSource.Create connectionString
            do! test dataSource connectionString
        finally
            container.DisposeAsync().AsTask().GetAwaiter().GetResult()
    }

let private scalar (dataSource: NpgsqlDataSource) (sql: string) =
    task {
        use command = dataSource.CreateCommand sql
        let! value = command.ExecuteScalarAsync()
        return value
    }

let private execute (dataSource: NpgsqlDataSource) (sql: string) =
    task {
        use command = dataSource.CreateCommand sql
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

let private tables (dataSource: NpgsqlDataSource) =
    task {
        use command = dataSource.CreateCommand "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' ORDER BY 1"
        use! reader = command.ExecuteReaderAsync()
        let names = ResizeArray<string>()

        while reader.Read() do
            names.Add(reader.GetString 0)

        return List.ofSeq names
    }

/// The statement must fail with this Postgres error code
let private rejected (dataSource: NpgsqlDataSource) (code: string) (sql: string) =
    task {
        let! ex = Assert.ThrowsAnyAsync<PostgresException>(fun () -> execute dataSource sql)
        Assert.Equal(code, ex.SqlState)
    }

let private checkViolation = "23514"
let private uniqueViolation = "23505"

[<Fact>]
let ``T010 initializeSchema creates every table and is safe to run twice`` () : Task =
    withDatabase (fun dataSource _ ->
        task {
            do! Database.initializeSchema dataSource
            let! first = tables dataSource
            Assert.Equal<string list>(expectedTables, first)

            do! Database.initializeSchema dataSource
            let! second = tables dataSource
            Assert.Equal<string list>(expectedTables, second)
        })

[<Fact>]
let ``T010 concurrent starts do not race`` () : Task =
    withDatabase (fun dataSource _ ->
        task {
            do! Task.WhenAll [| for _ in 1..6 -> Database.initializeSchema dataSource |]
            let! names = tables dataSource
            Assert.Equal<string list>(expectedTables, names)
        })

[<Fact>]
let ``T010 data survives a second bootstrap`` () : Task =
    withDatabase (fun dataSource _ ->
        task {
            do! Database.initializeSchema dataSource
            do! execute dataSource "INSERT INTO remote_hosts (host) VALUES ('a.example')"
            do! Database.initializeSchema dataSource
            let! count = scalar dataSource "SELECT count(*) FROM remote_hosts"
            Assert.Equal(1L, count :?> int64)
        })

[<Fact>]
let ``T010 the checks and uniques of the data model hold`` () : Task =
    withDatabase (fun dataSource _ ->
        task {
            do! Database.initializeSchema dataSource

            let actor id handle =
                $"INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('{id}', '{handle}', 'N', 'https://app/u', 'pem', '\\x00')"

            do! execute dataSource (actor "u1" "alex")

            // FR-004: id and handle patterns
            do! rejected dataSource checkViolation (actor "bad id" "other")
            do! rejected dataSource checkViolation (actor "u2" "Alex")
            // handle unique among live actors only
            do! rejected dataSource uniqueViolation (actor "u3" "alex")
            do! execute dataSource "UPDATE local_actors SET deleted_at = now() WHERE app_user_id = 'u1'"
            do! execute dataSource (actor "u4" "alex")
            // the id itself stays taken (rows are never deleted)
            do! rejected dataSource uniqueViolation (actor "u1" "zed")

            // instance actor: exactly one row
            do! rejected dataSource checkViolation "INSERT INTO instance_actor (id, public_key_pem, enc_private_key) VALUES (2, 'p', '\\x00')"
            do! execute dataSource "INSERT INTO instance_actor (id, public_key_pem, enc_private_key) VALUES (1, 'p', '\\x00')"
            do! rejected dataSource uniqueViolation "INSERT INTO instance_actor (id, public_key_pem, enc_private_key) VALUES (1, 'p', '\\x00')"

            // remote actor key id is unique
            let remote id keyId = $"INSERT INTO remote_actors (id, host, actor_type, inbox, key_id, public_key_pem) VALUES ('{id}', 'h', 'Person', 'https://h/i', '{keyId}', 'p')"
            do! execute dataSource (remote "https://h/a" "https://h/a#k")
            do! rejected dataSource uniqueViolation (remote "https://h/b" "https://h/a#k")

            // follows: status and direction checks, one row per pair and direction
            let follow status direction = $"INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status) VALUES (gen_random_uuid(), '{direction}', 'u4', 'https://h/a', 'f', '{status}')"
            do! rejected dataSource checkViolation (follow "maybe" "inbound")
            do! rejected dataSource checkViolation (follow "pending" "sideways")
            do! execute dataSource (follow "pending" "inbound")
            do! rejected dataSource uniqueViolation (follow "accepted" "inbound")
            do! execute dataSource (follow "pending" "outbound")

            // objects: audience and idempotency key
            let obj audience key = $"INSERT INTO objects (id, local_actor, audience, object_type, document, idempotency_key) VALUES (gen_random_uuid(), 'u4', '{audience}', 'Note', '{{}}', {key})"
            do! rejected dataSource checkViolation (obj "direct" "NULL")
            do! execute dataSource (obj "public" "'k1'")
            do! rejected dataSource uniqueViolation (obj "public" "'k1'")
            do! execute dataSource (obj "public" "NULL")
            do! execute dataSource (obj "public" "NULL")

            // deliveries: unique (activity, inbox_url), 48 h default deadline
            do! execute dataSource "INSERT INTO activities (id, local_actor, activity_type, audience, body) VALUES ('00000000-0000-4000-8000-000000000001', 'u4', 'Follow', 'direct', '\\x7b7d')"
            let delivery = "INSERT INTO deliveries (id, activity, inbox_url, host) VALUES (gen_random_uuid(), '00000000-0000-4000-8000-000000000001', 'https://h/i', 'h')"
            do! execute dataSource delivery
            do! rejected dataSource uniqueViolation delivery
            let! deadline = scalar dataSource "SELECT deadline_at - created_at FROM deliveries"
            Assert.Equal(TimeSpan.FromHours 48.0, deadline :?> TimeSpan)

            // events: unique (local_actor, seq)
            let evt id = $"INSERT INTO app_events (id, local_actor, seq, event_type, payload) VALUES ('{id}', 'u4', 1, 'follow.requested', '{{}}')"
            do! execute dataSource (evt "evt_1")
            do! rejected dataSource uniqueViolation (evt "evt_2")
        })

[<Fact>]
let ``T010 starting the app with a connection string creates the schema`` () : Task =
    withDatabase (fun dataSource connectionString ->
        task {
            let app =
                Megafono.Program.buildApp [||] (validOverrides @ [ "urls", "http://127.0.0.1:0"; "ConnectionStrings:Database", connectionString ])

            do! app.StartAsync()

            try
                let! names = tables dataSource
                Assert.Equal<string list>(expectedTables, names)
            finally
                app.StopAsync().GetAwaiter().GetResult()
        })
