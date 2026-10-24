module Megafono.Tests.Integration.SchemaTests

open System
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures.PostgresFixture

let private expectedTables =
    [ "activities"; "app_events"; "blocks"; "deliveries"; "extension_namespaces"; "feed_posts"; "follows"; "instance_actor"
      "local_actors"; "objects"; "processed_activities"; "remote_actors"; "remote_hosts"; "replies" ]

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

type SchemaTests(pg: PostgresFixture) =
    let withDatabase (test: NpgsqlDataSource -> string -> Task) : Task =
        task {
            let! connectionString = pg.CreateDatabase()
            use dataSource = NpgsqlDataSource.Create connectionString
            do! test dataSource connectionString
        }

    [<Fact>]
    member _.``002 FR-003 the replies table holds references only, never a reply's content``() : Task =
        withDatabase (fun dataSource _ ->
            task {
                do! Database.initializeSchema dataSource
                use command =
                    dataSource.CreateCommand
                        "SELECT column_name FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'replies'"
                use! reader = command.ExecuteReaderAsync()
                let columns = ResizeArray<string>()

                while reader.Read() do
                    columns.Add(reader.GetString 0)

                Assert.NotEmpty columns
                // The app owns content (research R-27): no body, warning, media or document column may appear here
                for forbidden in [ "content"; "summary"; "body"; "document"; "attachment"; "payload"; "name" ] do
                    Assert.DoesNotContain(forbidden, columns)
            })

    [<Fact>]
    member _.``T010 initializeSchema creates every table and is safe to run twice``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource
                let! first = tables dataSource
                Assert.Equal<string list>(expectedTables, first)

                do! Database.initializeSchema dataSource
                let! second = tables dataSource
                Assert.Equal<string list>(expectedTables, second)
            })

    [<Fact>]
    member _.``003 R-32 the key columns of local_actors are nullable and the change is repeatable``() : Task =
        withDatabase (fun dataSource _ ->
            task {
                do! Database.initializeSchema dataSource
                do! Database.initializeSchema dataSource

                let! nullable =
                    scalar
                        dataSource
                        "SELECT count(*) FROM information_schema.columns WHERE table_name = 'local_actors' AND column_name IN ('public_key_pem', 'enc_private_key') AND is_nullable = 'YES'"

                Assert.Equal(2L, nullable :?> int64)

                // A tombstone row without keys is accepted
                do!
                    execute
                        dataSource
                        "INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key, deleted_at) VALUES ('gone', 'gone', '', '', NULL, NULL, now())"

                // A database that still has the NOT NULL constraints gets them dropped on the next start
                do! execute dataSource "DELETE FROM local_actors"
                do! execute dataSource "ALTER TABLE local_actors ALTER COLUMN public_key_pem SET NOT NULL"
                do! execute dataSource "ALTER TABLE local_actors ALTER COLUMN enc_private_key SET NOT NULL"
                do! Database.initializeSchema dataSource

                let! again =
                    scalar
                        dataSource
                        "SELECT count(*) FROM information_schema.columns WHERE table_name = 'local_actors' AND column_name IN ('public_key_pem', 'enc_private_key') AND is_nullable = 'YES'"

                Assert.Equal(2L, again :?> int64)
            })

    [<Fact>]
    member _.``T010 concurrent starts do not race``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Task.WhenAll [| for _ in 1..6 -> Database.initializeSchema dataSource |]
                let! names = tables dataSource
                Assert.Equal<string list>(expectedTables, names)
            })

    [<Fact>]
    member _.``T010 data survives a second bootstrap``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource
                do! execute dataSource "INSERT INTO remote_hosts (host) VALUES ('a.example')"
                do! Database.initializeSchema dataSource
                let! count = scalar dataSource "SELECT count(*) FROM remote_hosts"
                Assert.Equal(1L, count :?> int64)
            })

    [<Fact>]
    member _.``a database made before dashed handles gets the new check on the next start``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource
                // Put back the check the first schema created
                do! execute dataSource "ALTER TABLE local_actors DROP CONSTRAINT local_actors_handle_check"
                do! execute dataSource "ALTER TABLE local_actors ADD CONSTRAINT local_actors_handle_check CHECK (handle ~ '^[a-z0-9_]{1,30}$')"

                let actor id handle =
                    $"INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('{id}', '{handle}', 'N', 'https://app/u', 'pem', '\\x00')"

                do! rejected dataSource checkViolation (actor "u1" "anime-fan")
                do! Database.initializeSchema dataSource
                do! execute dataSource (actor "u1" "anime-fan")
                do! rejected dataSource checkViolation (actor "u2" "fan-")
                // Replaced once: a further start leaves the check alone
                do! Database.initializeSchema dataSource
                let! checks = scalar dataSource "SELECT count(*) FROM pg_constraint WHERE conname = 'local_actors_handle_check'"
                Assert.Equal(1L, checks :?> int64)
            })

    [<Fact>]
    member _.``a database made before discoverable and indexable gets both columns with the old hardcoded values``() : Task =
        withDatabase (fun dataSource _ ->
            task {
                do! Database.initializeSchema dataSource
                do! execute dataSource "ALTER TABLE local_actors DROP COLUMN discoverable"
                do! execute dataSource "ALTER TABLE local_actors DROP COLUMN indexable"

                do!
                    execute
                        dataSource
                        "INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('old', 'old', 'N', 'https://app/u', 'pem', '\\x00')"

                do! Database.initializeSchema dataSource
                do! Database.initializeSchema dataSource

                let! discoverable = scalar dataSource "SELECT discoverable FROM local_actors WHERE app_user_id = 'old'"
                let! indexable = scalar dataSource "SELECT indexable FROM local_actors WHERE app_user_id = 'old'"
                Assert.True(discoverable :?> bool)
                Assert.False(indexable :?> bool)
            })

    [<Fact>]
    member _.``T010 the checks and uniques of the data model hold``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource

                let actor id handle =
                    $"INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('{id}', '{handle}', 'N', 'https://app/u', 'pem', '\\x00')"

                do! execute dataSource (actor "u1" "alex")

                // FR-004: id and handle patterns
                do! rejected dataSource checkViolation (actor "bad id" "other")
                do! rejected dataSource checkViolation (actor "u2" "Alex")
                do! rejected dataSource checkViolation (actor "u2" "-alex")
                do! rejected dataSource checkViolation (actor "u2" "alex-")
                do! rejected dataSource checkViolation (actor "u2" "al.ex")
                do! execute dataSource (actor "u5" "anime-fan")
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
    member _.``002 T001 replies: exactly one of local_object and remote_actor, state and audience checks, one row per local object``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource

                do!
                    execute
                        dataSource
                        "INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('g1', 'frieren', 'F', 'https://app/f', 'pem', '\\x00')"

                let rootSql id = $"INSERT INTO objects (id, local_actor, audience, object_type, document) VALUES ('{id}', 'g1', 'public', 'Note', '{{}}')"
                do! execute dataSource (rootSql "00000000-0000-4000-8000-0000000000a1")
                do! execute dataSource (rootSql "00000000-0000-4000-8000-0000000000a2")

                do!
                    execute
                        dataSource
                        "INSERT INTO remote_actors (id, host, actor_type, inbox, key_id, public_key_pem) VALUES ('https://h/a', 'h', 'Person', 'https://h/i', 'https://h/a#k', 'p')"

                let reply id local remote audience state =
                    $"""INSERT INTO replies (id, local_object, remote_actor, parent, thread_root, audience, state, published_at)
                        VALUES ('{id}', {local}, {remote}, 'https://megafono.test/ap/objects/00000000-0000-4000-8000-0000000000a1',
                                '00000000-0000-4000-8000-0000000000a1', '{audience}', '{state}', now())"""

                let remote = "'https://h/a'"
                let local = "'00000000-0000-4000-8000-0000000000a2'"
                do! execute dataSource (reply "https://h/r1" "NULL" remote "public" "live")
                // both set, neither set
                do! rejected dataSource checkViolation (reply "https://h/r2" local remote "public" "live")
                do! rejected dataSource checkViolation (reply "https://h/r3" "NULL" "NULL" "public" "live")
                // audience and state
                do! rejected dataSource checkViolation (reply "https://h/r4" "NULL" remote "mutuals" "live")
                do! rejected dataSource checkViolation (reply "https://h/r5" "NULL" remote "public" "gone")
                do! execute dataSource (reply "https://h/r6" "NULL" remote "direct" "deleted")
                // a local object has one reply row
                do! execute dataSource (reply "https://megafono.test/ap/objects/a2" local "NULL" "public" "live")
                do! rejected dataSource uniqueViolation (reply "https://megafono.test/ap/objects/a2b" local "NULL" "public" "live")
                // the parent object must exist
                do!
                    rejected
                        dataSource
                        "23503"
                        "INSERT INTO replies (id, remote_actor, parent, parent_object, thread_root, audience, published_at) VALUES ('https://h/r7', 'https://h/a', 'p', gen_random_uuid(), '00000000-0000-4000-8000-0000000000a1', 'public', now())"
            })

    [<Fact>]
    member _.``002 T001 a 001 database gets the reply columns and table, keeping its data``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource

                do!
                    execute
                        dataSource
                        "INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('g1', 'frieren', 'F', 'https://app/f', 'pem', '\\x00')"

                do!
                    execute
                        dataSource
                        "INSERT INTO objects (id, local_actor, audience, object_type, document) VALUES (gen_random_uuid(), 'g1', 'public', 'Note', '{}')"

                // Back to what feature 001 created, then start again
                do! execute dataSource "DROP TABLE replies; ALTER TABLE objects DROP COLUMN thread_root; ALTER TABLE objects DROP COLUMN in_reply_to"
                do! Database.initializeSchema dataSource
                do! Database.initializeSchema dataSource

                let! columns =
                    scalar
                        dataSource
                        "SELECT count(*) FROM information_schema.columns WHERE table_name = 'objects' AND column_name IN ('in_reply_to', 'thread_root')"

                Assert.Equal(2L, columns :?> int64)
                let! objects = scalar dataSource "SELECT count(*) FROM objects WHERE in_reply_to IS NULL AND thread_root IS NULL"
                Assert.Equal(1L, objects :?> int64)
                let! names = tables dataSource
                Assert.Contains("replies", names)
            })

    [<Fact>]
    member _.``004 data-model blocks: one target per row, one row per owner and target, Block is an activity type, the replaced check stays single``() : Task =
        withDatabase (fun dataSource connectionString ->
            task {
                do! Database.initializeSchema dataSource

                let actor id handle =
                    $"INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('{id}', '{handle}', 'N', 'https://app/u', 'pem', '\\x00')"

                do! execute dataSource (actor "u1" "alex")
                do! execute dataSource (actor "u2" "bea")

                let block (owner: string) (target: string) (domain: string) =
                    $"INSERT INTO blocks (id, local_actor, actor, domain) VALUES (gen_random_uuid(), {owner}, {target}, {domain})"

                // exactly one of actor and domain
                do! rejected dataSource checkViolation (block "'u1'" "'https://h/a'" "'h'")
                do! rejected dataSource checkViolation (block "'u1'" "NULL" "NULL")

                // one block per owner and target, also for the instance (NULL owner) where a plain unique index would not hold
                do! execute dataSource (block "'u1'" "'https://h/a'" "NULL")
                do! rejected dataSource uniqueViolation (block "'u1'" "'https://h/a'" "NULL")
                do! execute dataSource (block "NULL" "'https://h/a'" "NULL")
                do! rejected dataSource uniqueViolation (block "NULL" "'https://h/a'" "NULL")
                do! execute dataSource (block "'u1'" "NULL" "'h.example'")
                do! rejected dataSource uniqueViolation (block "'u1'" "NULL" "'h.example'")
                do! execute dataSource (block "NULL" "NULL" "'h.example'")
                do! rejected dataSource uniqueViolation (block "NULL" "NULL" "'h.example'")

                // the same target for two owners
                do! execute dataSource (block "'u2'" "'https://h/a'" "NULL")
                do! execute dataSource (block "'u2'" "NULL" "'h.example'")

                // the owner must be a local actor
                do! rejected dataSource "23503" (block "'nobody'" "'https://h/z'" "NULL")

                // 'Block' is an activity type
                do! execute dataSource "INSERT INTO activities (id, local_actor, activity_type, audience, body) VALUES (gen_random_uuid(), 'u1', 'Block', 'direct', '\\x7b7d')"

                // A database made before blocks has the old check: it is replaced once, then left alone
                do! execute dataSource "DELETE FROM activities"
                do! execute dataSource "ALTER TABLE activities DROP CONSTRAINT activities_activity_type_check"
                do!
                    execute
                        dataSource
                        "ALTER TABLE activities ADD CONSTRAINT activities_activity_type_check CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo'))"

                do! rejected dataSource checkViolation "INSERT INTO activities (id, local_actor, activity_type, audience, body) VALUES (gen_random_uuid(), 'u1', 'Block', 'direct', '\\x7b7d')"
                do! Database.initializeSchema dataSource
                do! Database.initializeSchema dataSource
                do! execute dataSource "INSERT INTO activities (id, local_actor, activity_type, audience, body) VALUES (gen_random_uuid(), 'u1', 'Block', 'direct', '\\x7b7d')"
                let! checks = scalar dataSource "SELECT count(*) FROM pg_constraint WHERE conname = 'activities_activity_type_check'"
                Assert.Equal(1L, checks :?> int64)
            })

    [<Fact>]
    member _.``005 data-model feed_posts: audience and state are checked, one row per post and user, the same post for two users``() : Task =
        withDatabase (fun dataSource _ ->
            task {
                do! Database.initializeSchema dataSource

                let actor id handle =
                    $"INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('{id}', '{handle}', 'N', 'https://app/u', 'pem', '\\x00')"

                do! execute dataSource (actor "u1" "alex")
                do! execute dataSource (actor "u2" "bea")

                do!
                    execute
                        dataSource
                        "INSERT INTO remote_actors (id, host, handle, actor_type, inbox, key_id, public_key_pem) VALUES ('https://h/bob', 'h', 'bob@h', 'Person', 'https://h/bob/inbox', 'https://h/bob#main-key', 'pem')"

                let post (user: string) (audience: string) (state: string) =
                    $"INSERT INTO feed_posts (id, local_actor, remote_actor, audience, state, published_at) VALUES ('https://h/bob/1', '{user}', 'https://h/bob', '{audience}', '{state}', now())"

                // the audience and the state are checked ('mutuals' is not a received audience)
                do! rejected dataSource checkViolation (post "u1" "mutuals" "live")
                do! rejected dataSource checkViolation (post "u1" "public" "gone")

                for audience in [ "public"; "unlisted"; "followers"; "direct" ] do
                    do! execute dataSource "DELETE FROM feed_posts"
                    do! execute dataSource (post "u1" audience "live")

                // one row per post and user, and the same post may be recorded for two users
                do! rejected dataSource uniqueViolation (post "u1" "public" "live")
                do! execute dataSource (post "u2" "public" "live")

                // the user and the author must exist
                do! rejected dataSource "23503" (post "nobody" "public" "live")
            })

    [<Fact>]
    member _.``005 R-58 extension_namespaces is filled once from the stored objects' context, then left alone``() : Task =
        withDatabase (fun dataSource _ ->
            task {
                do! Database.initializeSchema dataSource

                do!
                    execute
                        dataSource
                        "INSERT INTO local_actors (app_user_id, handle, name, profile_url, public_key_pem, enc_private_key) VALUES ('u1', 'alex', 'N', 'https://app/u', 'pem', '\\x00')"

                // an object as Megáfono stores it: its own terms, and the app's namespace under a prefix
                let context =
                    """["https://www.w3.org/ns/activitystreams",{"ostatus":"http://ostatus.org#","conversation":"ostatus:conversation","gts":"https://gotosocial.org/ns#","interactionPolicy":{"@id":"gts:interactionPolicy","@type":"@id"},"app":"https://app.example/ns#"}]"""

                let insert (id: string) (document: string) =
                    $"INSERT INTO objects (id, local_actor, audience, object_type, document) VALUES ('{id}', 'u1', 'public', 'Note', '{document}')"

                do! execute dataSource (insert "6f1c0000-0000-4000-8000-000000000001" $"""{{"@context":{context}}}""")
                // a context that is a plain string, and an object without one, add nothing
                do! execute dataSource (insert "6f1c0000-0000-4000-8000-000000000002" """{"@context":"https://www.w3.org/ns/activitystreams"}""")
                do! execute dataSource (insert "6f1c0000-0000-4000-8000-000000000003" "{}")

                // Back to a database made before this feature, then start again
                do! execute dataSource "DROP TABLE extension_namespaces"
                do! Database.initializeSchema dataSource

                let! namespaces = scalar dataSource "SELECT string_agg(namespace, ',' ORDER BY namespace) FROM extension_namespaces"
                Assert.Equal("https://app.example/ns#", namespaces :?> string)

                // starting again changes nothing, and does not scan the objects a second time
                do! Database.initializeSchema dataSource
                do! execute dataSource "DELETE FROM extension_namespaces"
                do! Database.initializeSchema dataSource
                let! rows = scalar dataSource "SELECT count(*) FROM extension_namespaces"
                Assert.Equal(0L, rows :?> int64)
            })
