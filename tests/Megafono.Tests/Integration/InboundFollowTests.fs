module Megafono.Tests.Integration.InboundFollowTests

open System
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

module private Task =
    let map (f: 'a -> 'b) (t: Task<'a>) : Task<'b> =
        task {
            let! v = t
            return f v
        }

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      Stub: StubRemote.StubRemote }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-lock" "lock" with Locked = true }
        app.AddUser { FakeApp.user "u-open" "open" with Locked = false }
        let! stub = StubRemote.start [ "alice"; "bea"; "carl"; "mallory" ]
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        return { App = app; Host = host; Db = NpgsqlDataSource.Create cs; Stub = stub }
    }

let private inboxUrl (w: World) = w.Host.FederationUrl + "/inbox"

let private send (w: World) (name: string) (body: string) =
    task {
        use client = new Net.Http.HttpClient()
        let! response = postText client w.Stub name (inboxUrl w) body
        return response.StatusCode
    }

let private events (w: World) (eventType: string) = w.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (w: World) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events w eventType).Length >= count)

let private followRow (w: World) (user: string) (name: string) =
    task {
        use command =
            w.Db.CreateCommand
                "SELECT id::text, status, follow_activity_id FROM follows WHERE direction = 'inbound' AND local_actor = @u AND remote_actor = @r"

        command.Parameters.AddWithValue("u", user) |> ignore
        command.Parameters.AddWithValue("r", w.Stub.ActorId name) |> ignore
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(reader.GetString 0, reader.GetString 1, reader.GetString 2) else None
    }

let private answer (w: World) (user: string) (followId: string) (verb: string) =
    task {
        let! response = w.Host.InternalClient(Some MegafonoHost.token).PostAsync($"/internal/actors/{user}/follow-requests/{followId}/{verb}", null)
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

/// What the stub's inbox received at `path`, parsed
let private received (w: World) (path: string) =
    w.Stub.Posts |> List.filter (fun p -> p.Path = path) |> List.map (fun p -> p, JsonNode.Parse(Text.Encoding.UTF8.GetString p.Body))

let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

type InboundFollowTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-030 a follow of a locked user stays pending and reaches the app as follow.requested``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! status = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            Assert.Equal(HttpStatusCode.Accepted, status)

            do! waitForEvents w "follow.requested" 1
            let e = (events w "follow.requested").Head
            Assert.True e.SignatureValid
            Assert.Equal("u-lock", get e.Json "user.id")
            Assert.Equal($"@alice@{w.Stub.Authority}", get e.Json "actor.handle")
            Assert.Equal(w.Stub.ActorId "alice", get e.Json "actor.id")
            Assert.Equal("inbound", get e.Json "follow.direction")
            Assert.Equal("pending", get e.Json "follow.status")

            let! row = followRow w "u-lock" "alice"
            let rowId, rowStatus, rowFollow = row.Value
            Assert.Equal("pending", rowStatus)
            Assert.Equal(followId w.Stub "alice" "1", rowFollow)
            Assert.Equal(rowId, get e.Json "follow.id")

            // Nothing is sent to the follower until the app answers
            do! Task.Delay 500
            Assert.Empty w.Stub.Posts
        }

    [<Fact>]
    member _.``FR-030 approving sends an Accept that embeds the full Follow, and reports follow.accepted``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 1
            let! rowId, _, _ = followRow w "u-lock" "alice" |> Task.map Option.get

            let! status, body = answer w "u-lock" rowId "approve"
            Assert.Equal(HttpStatusCode.OK, status)
            use json = JsonDocument.Parse body
            Assert.Equal("accepted", json.RootElement.GetProperty("status").GetString())
            Assert.Equal(rowId, json.RootElement.GetProperty("followId").GetString())

            do! waitUntil "the Accept" (TimeSpan.FromSeconds 20.0) (fun () -> not w.Stub.Posts.IsEmpty)
            let post, accept = (received w "/users/alice/inbox").Head
            Assert.Equal("Accept", get accept "type")
            Assert.StartsWith("https://megafono.test/ap/activities/", get accept "id")
            Assert.Equal(ourActor "u-lock", get accept "actor")
            // The full Follow, not just its id (GoToSocial refuses the bare form)
            Assert.Equal(followId w.Stub "alice" "1", get accept "object.id")
            Assert.Equal("Follow", get accept "object.type")
            Assert.Equal(w.Stub.ActorId "alice", get accept "object.actor")
            Assert.Equal(ourActor "u-lock", get accept "object.object")

            // Signed with the user's key
            let! pem = Db.scalar w.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-lock'"
            use pub = KeyVault.importPublicPem (string pem)
            let request: HttpSignatures.SignedRequest = { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify w.Host.Time pub request)

            do! waitForEvents w "follow.accepted" 1
            Assert.Equal("accepted", get (events w "follow.accepted").Head.Json "follow.status")
            let! row = followRow w "u-lock" "alice"
            Assert.Equal("accepted", (let _, s, _ = row.Value in s))
        }

    [<Fact>]
    member _.``FR-030 rejecting sends a Reject and reports follow.rejected``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "bea" (followBody w.Stub "bea" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 1
            let! rowId, _, _ = followRow w "u-lock" "bea" |> Task.map Option.get

            let! status, body = answer w "u-lock" rowId "reject"
            Assert.Equal(HttpStatusCode.OK, status)
            Assert.Contains("rejected", body)

            do! waitUntil "the Reject" (TimeSpan.FromSeconds 20.0) (fun () -> not w.Stub.Posts.IsEmpty)
            let _, reject = (received w "/users/bea/inbox").Head
            Assert.Equal("Reject", get reject "type")
            Assert.Equal(followId w.Stub "bea" "1", get reject "object.id")
            Assert.Equal("Follow", get reject "object.type")
            do! waitForEvents w "follow.rejected" 1
            Assert.Equal("rejected", get (events w "follow.rejected").Head.Json "follow.status")
        }

    [<Fact>]
    member _.``FR-031 a follow of an unlocked user is accepted at once``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! status = send w "carl" (followBody w.Stub "carl" "1" (ourActor "u-open"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitUntil "the Accept" (TimeSpan.FromSeconds 20.0) (fun () -> not w.Stub.Posts.IsEmpty)
            let _, accept = (received w "/users/carl/inbox").Head
            Assert.Equal("Accept", get accept "type")
            Assert.Equal(followId w.Stub "carl" "1" , get accept "object.id")
            do! waitForEvents w "follow.accepted" 1
            Assert.Empty(events w "follow.requested")
            let! row = followRow w "u-open" "carl"
            Assert.Equal("accepted", (let _, s, _ = row.Value in s))
        }

    [<Fact>]
    member _.``FR-032 Undo of a Follow, embedded or by id, removes the follower and reports follow.removed``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-open"))
            let! _ = send w "bea" (followBody w.Stub "bea" "1" (ourActor "u-open"))
            do! waitForEvents w "follow.accepted" 2

            let! s1 = send w "alice" (undoEmbedded w.Stub "alice" "1" (ourActor "u-open"))
            let! s2 = send w "bea" (undoBare w.Stub "bea" "1")
            Assert.Equal(HttpStatusCode.Accepted, s1)
            Assert.Equal(HttpStatusCode.Accepted, s2)

            do! waitForEvents w "follow.removed" 2
            for name in [ "alice"; "bea" ] do
                let! row = followRow w "u-open" name
                Assert.Equal("removed", (let _, s, _ = row.Value in s))

            let removed = events w "follow.removed"
            Assert.All(removed, fun e -> Assert.Equal("removed", get e.Json "follow.status"))
            Assert.Equal<string list>([ w.Stub.ActorId "alice"; w.Stub.ActorId "bea" ], removed |> List.map (fun e -> get e.Json "actor.id") |> List.sort)
        }

    [<Fact>]
    member _.``FR-032 Undo of a pending request removes it too``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 1
            let! _ = send w "alice" (undoEmbedded w.Stub "alice" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.removed" 1
            let! row = followRow w "u-lock" "alice"
            Assert.Equal("removed", (let _, s, _ = row.Value in s))
        }

    [<Fact>]
    member _.``data-model a repeated Follow keeps one row and answers with the latest id``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-open"))
            do! waitUntil "the first Accept" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length = 1)
            let! again = send w "alice" (followBody w.Stub "alice" "2" (ourActor "u-open"))
            Assert.Equal(HttpStatusCode.Accepted, again)
            do! waitUntil "the second Accept" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length = 2)

            let accepts = received w "/users/alice/inbox" |> List.map snd
            Assert.Equal(followId w.Stub "alice" "1", get accepts[0] "object.id")
            Assert.Equal(followId w.Stub "alice" "2", get accepts[1] "object.id")
            Assert.NotEqual<string>(get accepts[0] "id", get accepts[1] "id")

            let! rows = Db.count w.Db "SELECT count(*) FROM follows WHERE local_actor = 'u-open'"
            Assert.Equal(1L, rows)
            let! row = followRow w "u-open" "alice"
            Assert.Equal(followId w.Stub "alice" "2", (let _, _, f = row.Value in f))
            // The relationship did not change, so the app is not told twice
            Assert.Equal(1, (events w "follow.accepted").Length)
        }

    [<Fact>]
    member _.``data-model a pending request that is repeated stays pending with the latest id``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 1
            let! _ = send w "alice" (followBody w.Stub "alice" "2" (ourActor "u-lock"))
            let! row = followRow w "u-lock" "alice"
            let _, status, follow = row.Value
            Assert.Equal("pending", status)
            Assert.Equal(followId w.Stub "alice" "2", follow)
            Assert.Equal(1, (events w "follow.requested").Length)
        }

    [<Fact>]
    member _.``data-model a follow after an unfollow or a rejection is a new request``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 1
            let! rowId, _, _ = followRow w "u-lock" "alice" |> Task.map Option.get
            let! _ = answer w "u-lock" rowId "reject"
            do! waitForEvents w "follow.rejected" 1

            let! _ = send w "alice" (followBody w.Stub "alice" "2" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 2
            let! row = followRow w "u-lock" "alice"
            Assert.Equal("pending", (let _, s, _ = row.Value in s))
        }

    [<Fact>]
    member _.``FR-015 a Follow whose actor is not the signer is ignored``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            // mallory signs, but the Follow says bea follows
            let body = followBody w.Stub "bea" "1" (ourActor "u-open")
            let! status = send w "mallory" body
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! Task.Delay 700
            let! rows = Db.count w.Db "SELECT count(*) FROM follows"
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(0L, rows)
            Assert.Equal(0L, processed)
            Assert.Empty w.App.Received
            Assert.Empty w.Stub.Posts
        }

    [<Fact>]
    member _.``FR-015 a Follow whose id is on another host than the signer is ignored``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let body = (followBody w.Stub "alice" "1" (ourActor "u-open")).Replace(followId w.Stub "alice" "1", "https://elsewhere.example/follows/1")
            let! status = send w "alice" body
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! Task.Delay 500
            let! rows = Db.count w.Db "SELECT count(*) FROM follows"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-030 a Follow for a user the app does not know is refused, one for another domain is ignored``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! unknown = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-gone"))
            Assert.Equal(HttpStatusCode.NotFound, unknown)

            let! foreign = send w "alice" (followBody w.Stub "alice" "2" "https://other.example/ap/actors/u-open")
            Assert.Equal(HttpStatusCode.Accepted, foreign)

            do! Task.Delay 500
            let! rows = Db.count w.Db "SELECT count(*) FROM follows"
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(0L, rows)
            Assert.Equal(0L, processed) // a refused request leaves no trace, so a later retry is handled
            Assert.Empty w.App.Received
        }

    [<Fact>]
    member _.``R-05 the target comes from the object, not from the inbox the Follow was sent to``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            // Sent to u-lock's personal inbox, but it follows u-open
            let url = w.Host.FederationUrl + "/ap/actors/u-lock/inbox"
            let! _ = postText client w.Stub "alice" url (followBody w.Stub "alice" "1" (ourActor "u-open"))
            let! response = postText client w.Stub "alice" url (followBody w.Stub "alice" "2" (ourActor "u-open"))
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode)
            do! waitForEvents w "follow.accepted" 1
            let! open' = followRow w "u-open" "alice"
            let! lock' = followRow w "u-lock" "alice"
            Assert.True open'.IsSome
            Assert.True lock'.IsNone
        }

    [<Fact>]
    member _.``contract a personal inbox of an unknown user is 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            use client = new Net.Http.HttpClient()
            let url = w.Host.FederationUrl + "/ap/actors/u-gone/inbox"
            let! response = postText client w.Stub "alice" url (followBody w.Stub "alice" "1" (ourActor "u-open"))
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        }

    [<Fact>]
    member _.``contract approving twice is 200, the other answer afterwards is 409, unknown ids are 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = send w "alice" (followBody w.Stub "alice" "1" (ourActor "u-lock"))
            let! _ = send w "bea" (followBody w.Stub "bea" "1" (ourActor "u-lock"))
            do! waitForEvents w "follow.requested" 2
            let! aliceId, _, _ = followRow w "u-lock" "alice" |> Task.map Option.get
            let! beaId, _, _ = followRow w "u-lock" "bea" |> Task.map Option.get

            let! first, _ = answer w "u-lock" aliceId "approve"
            let! second, _ = answer w "u-lock" aliceId "approve"
            let! afterApprove, conflictText = answer w "u-lock" aliceId "reject"
            Assert.Equal(HttpStatusCode.OK, first)
            Assert.Equal(HttpStatusCode.OK, second)
            Assert.Equal(HttpStatusCode.Conflict, afterApprove)
            Assert.Contains("conflict", conflictText)

            let! rejected, _ = answer w "u-lock" beaId "reject"
            let! rejectedAgain, _ = answer w "u-lock" beaId "reject"
            let! approveAfterReject, _ = answer w "u-lock" beaId "approve"
            Assert.Equal(HttpStatusCode.OK, rejected)
            Assert.Equal(HttpStatusCode.OK, rejectedAgain)
            Assert.Equal(HttpStatusCode.Conflict, approveAfterReject)

            let! unknown, _ = answer w "u-lock" (string (Guid.NewGuid())) "approve"
            let! malformed, _ = answer w "u-lock" "not-a-guid" "approve"
            let! otherUser, _ = answer w "u-open" aliceId "approve" // someone else's request
            Assert.Equal(HttpStatusCode.NotFound, unknown)
            Assert.Equal(HttpStatusCode.NotFound, malformed)
            Assert.Equal(HttpStatusCode.NotFound, otherUser)

            // One Accept and one Reject went out, once each, and the app heard once each
            do! waitUntil "both answers" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length = 2)
            do! Task.Delay 500
            Assert.Equal(2, w.Stub.Posts.Length)
            Assert.Equal(1, (events w "follow.accepted").Length)
            Assert.Equal(1, (events w "follow.rejected").Length)
        }
