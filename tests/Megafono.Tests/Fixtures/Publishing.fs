/// Shared set-up for the publishing tests: a host with a fake app and fake clock, stub remote servers, seeded
/// followers with real keys, and the HTTP calls the app and a remote server would make
module Megafono.Tests.Fixtures.Publishing

open System
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let json (text: string) : JsonNode = JsonNode.Parse text

/// The node at a dotted path (`"activities.0.deliveries.0.status"`): object keys and array positions
let at (node: JsonNode) (path: string) : JsonNode =
    path.Split('.')
    |> Array.fold
        (fun (n: JsonNode) key ->
            match n with
            | null -> null
            | :? JsonArray as a -> a[int key]
            | o -> o[key])
        node

/// Typed reads of a JSON node (`n["a"].GetValue<string>()` does not parse well inside argument lists)
let jstr (node: JsonNode) : string = node.GetValue<string>()
let intOf (node: JsonNode) : int = node.GetValue<int>()
let boolOf (node: JsonNode) : bool = node.GetValue<bool>()

type Rig =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      Time: FakeTime
      Stub: StubRemote.StubRemote }

/// Megáfono with two app users (`u-1` alex, `u-2` bea) and one stub remote server with the given users
let startWith (pg: PostgresFixture) (extra: (string * string) list) (stubUsers: string list) : Task<Rig> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser(FakeApp.user "u-1" "alex")
        app.AddUser(FakeApp.user "u-2" "bea")
        let! stub = StubRemote.start stubUsers
        let time = FakeTime()
        let! host = MegafonoHost.start { MegafonoHost.settings cs app.Url time with Extra = extra }
        // The users get their rows (and keys) the first time Megáfono looks them up
        use client = host.FederationClient()

        for id in [ "u-1"; "u-2" ] do
            use! response = client.GetAsync $"/ap/actors/{id}"
            response.EnsureSuccessStatusCode() |> ignore

        return { App = app; Host = host; Db = NpgsqlDataSource.Create cs; Time = time; Stub = stub }
    }

let start (pg: PostgresFixture) (stubUsers: string list) : Task<Rig> = startWith pg [] stubUsers

/// An inbound follow of `localUser` by `name` on `stub`, with that stub's real public key, so signed requests
/// from it verify. `status` is pending, accepted, rejected or removed.
let addFollower
    (db: NpgsqlDataSource)
    (localUser: string)
    (stub: StubRemote.StubRemote)
    (name: string)
    (status: string)
    (sharedInbox: string option)
    : Task =
    task {
        use command =
            db.CreateCommand
                """INSERT INTO remote_actors (id, host, handle, actor_type, inbox, shared_inbox, key_id, public_key_pem)
                   VALUES (@id, @host, @handle, 'Person', @inbox, @shared, @keyid, @pem) ON CONFLICT (id) DO NOTHING;
                   INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
                   VALUES (gen_random_uuid(), 'inbound', @local, @id, @follow, @status)
                   ON CONFLICT (direction, local_actor, remote_actor) DO UPDATE SET status = @status"""

        command.Parameters.AddWithValue("id", stub.ActorId name) |> ignore
        command.Parameters.AddWithValue("host", stub.Authority.ToLowerInvariant()) |> ignore
        command.Parameters.AddWithValue("handle", $"{name}@{stub.Authority}") |> ignore
        command.Parameters.AddWithValue("inbox", stub.InboxUrl name) |> ignore
        command.Parameters.AddWithValue("shared", (match sharedInbox with Some s -> box s | None -> box DBNull.Value)) |> ignore
        command.Parameters.AddWithValue("keyid", stub.KeyId name) |> ignore
        command.Parameters.AddWithValue("pem", stub.PublicKeyPem) |> ignore
        command.Parameters.AddWithValue("local", localUser) |> ignore
        command.Parameters.AddWithValue("follow", stub.ActorId name + "/follows/1") |> ignore
        command.Parameters.AddWithValue("status", status) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

/// An outbound follow of `name` on `stub` by `localUser`, with that stub's real public key, so posts signed by it
/// verify. `status` is pending, accepted, rejected or removed.
let addFollowing (db: NpgsqlDataSource) (localUser: string) (stub: StubRemote.StubRemote) (name: string) (status: string) : Task =
    task {
        use command =
            db.CreateCommand
                """INSERT INTO remote_actors (id, host, handle, actor_type, inbox, followers_url, name, profile_url, key_id, public_key_pem)
                   VALUES (@id, @host, @handle, 'Person', @inbox, @followers, @name, @profile, @keyid, @pem) ON CONFLICT (id) DO NOTHING;
                   INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
                   VALUES (gen_random_uuid(), 'outbound', @local, @id, @follow, @status)
                   ON CONFLICT (direction, local_actor, remote_actor) DO UPDATE SET status = @status"""

        command.Parameters.AddWithValue("id", stub.ActorId name) |> ignore
        command.Parameters.AddWithValue("host", stub.Authority.ToLowerInvariant()) |> ignore
        command.Parameters.AddWithValue("handle", $"{name}@{stub.Authority}") |> ignore
        command.Parameters.AddWithValue("inbox", stub.InboxUrl name) |> ignore
        command.Parameters.AddWithValue("followers", stub.ActorId name + "/followers") |> ignore
        command.Parameters.AddWithValue("name", $"{name} (stub)") |> ignore
        command.Parameters.AddWithValue("profile", $"{stub.BaseUrl}/@{name}") |> ignore
        command.Parameters.AddWithValue("keyid", stub.KeyId name) |> ignore
        command.Parameters.AddWithValue("pem", stub.PublicKeyPem) |> ignore
        command.Parameters.AddWithValue("local", localUser) |> ignore
        command.Parameters.AddWithValue("follow", $"https://megafono.test/ap/activities/{Guid.NewGuid()}") |> ignore
        command.Parameters.AddWithValue("status", status) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

let setFollowStatus (db: NpgsqlDataSource) (localUser: string) (stub: StubRemote.StubRemote) (name: string) (status: string) : Task =
    Db.execute db $"UPDATE follows SET status = '{status}' WHERE local_actor = '{localUser}' AND remote_actor = '{stub.ActorId name}'"

/// A publish body; `extra` is merged into the `object`
let publishBody (audience: string) (content: string) : JsonObject =
    let o = JsonObject()
    o["audience"] <- JsonValue.Create audience
    let inner = JsonObject()
    inner["type"] <- JsonValue.Create "Note"
    inner["content"] <- JsonValue.Create content
    o["object"] <- inner
    o

let private jsonContent (body: string) = new StringContent(body, Encoding.UTF8, "application/json")

/// POST /internal/actors/{user}/objects; returns the status and the body text
let publishRaw (rig: Rig) (user: string) (body: string) (key: string option) : Task<int * string> =
    task {
        use client = rig.Host.InternalClient(Some MegafonoHost.token)
        use request = new HttpRequestMessage(HttpMethod.Post, $"/internal/actors/{user}/objects")
        request.Content <- jsonContent body
        key |> Option.iter (fun k -> request.Headers.Add("Idempotency-Key", k))
        use! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, text
    }

/// Publishes a Note and returns its object id (a GUID string); fails the test unless the answer is 201
let publish (rig: Rig) (user: string) (audience: string) (content: string) : Task<string> =
    task {
        let! status, text = publishRaw rig user ((publishBody audience content).ToJsonString()) None

        if status <> 201 then
            failwith $"publish answered {status}: {text}"

        return jstr ((JsonNode.Parse text)["id"])
    }

let sendInternal (rig: Rig) (httpMethod: HttpMethod) (path: string) (body: string option) : Task<int * string> =
    task {
        use client = rig.Host.InternalClient(Some MegafonoHost.token)
        use request = new HttpRequestMessage(httpMethod, path)
        body |> Option.iter (fun b -> request.Content <- jsonContent b)
        use! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, text
    }

/// GET on the federation port; `headers` are added as given (a signed request from a stub brings its own)
let federationGet (rig: Rig) (path: string) (headers: (string * string) list) : Task<HttpResponseMessage> =
    task {
        let client = rig.Host.FederationClient()
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        headers |> List.iter (fun (n, v) -> request.Headers.TryAddWithoutValidation(n, v) |> ignore)
        return! client.SendAsync request
    }

/// GET signed as `name` on `stub`, the way a remote server fetches a private object
let signedGet (rig: Rig) (stub: StubRemote.StubRemote) (name: string) (path: string) : Task<HttpResponseMessage> =
    federationGet rig path (stub.Sign(name, "GET", rig.Host.FederationUrl + path, None))

let objectPath (objectId: string) = $"/ap/objects/{objectId}"

/// The Create activity's id (a GUID string) of an object
let createActivityId (rig: Rig) (objectId: string) : Task<string> =
    task {
        let! v = Db.scalar rig.Db $"SELECT id::text FROM activities WHERE object = '{objectId}' AND activity_type = 'Create'"
        return string v
    }



let disposeRig (rig: Rig) : IDisposable = MegafonoHost.disposer rig.Host

/// The first column of every row, as text
let strings (db: NpgsqlDataSource) (sql: string) : Task<string list> =
    task {
        use command = db.CreateCommand sql
        use! reader = command.ExecuteReaderAsync()
        let rows = ResizeArray<string>()

        while reader.Read() do
            rows.Add(reader.GetString 0)

        return List.ofSeq rows
    }
