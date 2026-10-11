module Megafono.Tests.Integration.RemoteActorUpdateTests

open System
open System.Net
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      Stub: StubRemote.StubRemote }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-open" "open" with Locked = false }
        app.AddUser { FakeApp.user "u-other" "other" with Locked = false }
        let! stub = StubRemote.start [ "alice"; "mallory" ]
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        return { App = app; Host = host; Db = NpgsqlDataSource.Create cs; Stub = stub }
    }

let private events (w: World) (eventType: string) = w.App.Received |> List.filter (fun e -> e.Type = eventType)

let private updateBody (stub: StubRemote.StubRemote) (signer: string) (subject: string) (suffix: string) =
    $"""{{"@context":"https://www.w3.org/ns/activitystreams","id":"{stub.ActorId signer}#updates/{suffix}","type":"Update","actor":"{stub.ActorId signer}","object":{{"id":"{stub.ActorId subject}","type":"Person","preferredUsername":"{subject}","name":"Embedded Name"}}}}"""

let private nameInDb (w: World) (name: string) =
    task {
        let! v = Db.scalar w.Db $"SELECT name FROM remote_actors WHERE id = '{w.Stub.ActorId name}'"
        return string v
    }

/// A string at a dotted path of a JSON document
let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

let private rename (w: World) (name: string) =
    w.Stub.Mutate <- Some(fun actor doc -> if actor = "alice" then doc["name"] <- JsonValue.Create name)

type RemoteActorUpdateTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-013 an Update{Person} from a followed actor refreshes remote_actors and emits actor.updated``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            let url = w.Host.FederationUrl + "/inbox"

            // alice follows u-open (unlocked: accepted at once) and u-other
            let! _ = postText client w.Stub "alice" url (followBody w.Stub "alice" "1" (ourActor "u-open"))
            let! _ = postText client w.Stub "alice" url (followBody w.Stub "alice" "2" (ourActor "u-other"))
            do! waitUntil "both follows accepted" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "follow.accepted").Length = 2)
            let! before = nameInDb w "alice"
            Assert.Equal("alice (stub)", before)

            rename w "Alice <b>Renamed</b>"
            let! response = postText client w.Stub "alice" url (updateBody w.Stub "alice" "alice" "1")
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode)

            do! waitUntil "actor.updated for both users" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "actor.updated").Length = 2)
            let! after = nameInDb w "alice"
            // The origin's copy is stored (and sanitized), not what the activity embedded
            Assert.Equal("Alice Renamed", after)

            let updated = events w "actor.updated"
            Assert.Equal<string list>([ "u-open"; "u-other" ], updated |> List.map (fun e -> get e.Json "user.id") |> List.sort)
            let e = updated.Head
            Assert.True e.SignatureValid
            Assert.Equal(w.Stub.ActorId "alice", get e.Json "actor.id")
            Assert.Equal("Alice Renamed", get e.Json "actor.name")
            Assert.False((e.Json.AsObject()).ContainsKey "follow")
        }

    [<Fact>]
    member _.``FR-013 an Update whose object is not the signer is ignored``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            let url = w.Host.FederationUrl + "/inbox"
            let! _ = postText client w.Stub "alice" url (followBody w.Stub "alice" "1" (ourActor "u-open"))
            do! waitUntil "follow accepted" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "follow.accepted").Length = 1)
            let! _ = postText client w.Stub "mallory" url (followBody w.Stub "mallory" "1" (ourActor "u-open"))
            do! waitUntil "second follow accepted" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "follow.accepted").Length = 2)

            rename w "Changed By Update"
            // mallory signs an Update about alice
            let! response = postText client w.Stub "mallory" url (updateBody w.Stub "mallory" "alice" "1")
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode)
            do! Task.Delay 800

            Assert.Empty(events w "actor.updated")
            let! name = nameInDb w "alice"
            Assert.Equal("alice (stub)", name)
        }

    [<Fact>]
    member _.``FR-013 an Update from an actor nobody follows is applied quietly``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            rename w "Stranger Renamed"
            let! response = postText client w.Stub "alice" (w.Host.FederationUrl + "/inbox") (updateBody w.Stub "alice" "alice" "1")
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode)
            do! Task.Delay 500
            Assert.Empty w.App.Received
            let! name = nameInDb w "alice"
            Assert.Equal("Stranger Renamed", name)
        }

    [<Fact>]
    member _.``FR-014 a repeated Update is handled once``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            let url = w.Host.FederationUrl + "/inbox"
            let! _ = postText client w.Stub "alice" url (followBody w.Stub "alice" "1" (ourActor "u-open"))
            do! waitUntil "follow accepted" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "follow.accepted").Length = 1)
            rename w "Once"

            for _ in 1..3 do
                let! _ = postText client w.Stub "alice" url (updateBody w.Stub "alice" "alice" "same")
                ()

            do! waitUntil "the event" (TimeSpan.FromSeconds 20.0) (fun () -> (events w "actor.updated").Length >= 1)
            do! Task.Delay 600
            Assert.Equal(1, (events w "actor.updated").Length)
        }
