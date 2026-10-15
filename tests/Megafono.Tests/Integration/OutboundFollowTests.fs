module Megafono.Tests.Integration.OutboundFollowTests

open System
open System.Net
open System.Net.Http
open System.Text
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

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      Stub: StubRemote.StubRemote }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-1" "alex" with Locked = false }
        app.AddUser { FakeApp.user "u-2" "sam" with Locked = false }
        let! stub = StubRemote.start [ "alice"; "bea"; "carl"; "mallory" ]
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        return { App = app; Host = host; Db = NpgsqlDataSource.Create cs; Stub = stub }
    }

let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

let private events (w: World) (eventType: string) = w.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (w: World) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType}" (TimeSpan.FromSeconds 20.0) (fun () -> (events w eventType).Length >= count)

let private internalCall (w: World) (httpMethod: HttpMethod) (path: string) (body: string option) =
    task {
        use request = new HttpRequestMessage(httpMethod, path)
        body |> Option.iter (fun b -> request.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
        let! response = w.Host.InternalClient(Some MegafonoHost.token).SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

let private follow (w: World) (user: string) (target: string) =
    internalCall w HttpMethod.Post $"/internal/actors/{user}/following" (Some(JsonSerializer.Serialize {| target = target |}))

let private unfollow (w: World) (user: string) (actorId: string) =
    internalCall w HttpMethod.Delete $"/internal/actors/{user}/following?actor={Uri.EscapeDataString actorId}" None

let private inboxOf (name: string) = $"/users/{name}/inbox"

let private received (w: World) (name: string) =
    w.Stub.Posts |> List.filter (fun p -> p.Path = inboxOf name) |> List.map (fun p -> p, JsonNode.Parse(Encoding.UTF8.GetString p.Body))

let private row (w: World) (user: string) (name: string) =
    task {
        use command =
            w.Db.CreateCommand
                "SELECT id::text, status, follow_activity_id FROM follows WHERE direction = 'outbound' AND local_actor = @u AND remote_actor = @r"

        command.Parameters.AddWithValue("u", user) |> ignore
        command.Parameters.AddWithValue("r", w.Stub.ActorId name) |> ignore
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(reader.GetString 0, reader.GetString 1, reader.GetString 2) else None
    }

let private status' (r: (string * string * string) option) = let _, s, _ = r.Value in s

/// The remote server answers our Follow: an Accept or a Reject that embeds the Follow it saw
let private answer (w: World) (signer: string) (kind: string) (suffix: string) (followNode: string) =
    task {
        use client = new HttpClient()
        let body =
            $"""{{"@context":"https://www.w3.org/ns/activitystreams","id":"{w.Stub.ActorId signer}/{kind.ToLowerInvariant()}s/{suffix}","type":"{kind}","actor":"{w.Stub.ActorId signer}","object":{followNode}}}"""

        let! response = postText client w.Stub signer (w.Host.FederationUrl + "/inbox") body
        return response.StatusCode
    }

/// The Follow we sent, as JSON text, taken from what the stub's inbox received
let private sentFollow (w: World) (name: string) =
    let _, node = (received w name) |> List.find (fun (_, n) -> get n "type" = "Follow")
    node.ToJsonString(), get node "id"

type OutboundFollowTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-034 following by handle delivers a signed Follow and the follow is pending``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! status, body = follow w "u-1" $"@alice@{w.Stub.Authority}"
            Assert.Equal(HttpStatusCode.Accepted, status)
            use json = JsonDocument.Parse body
            Assert.Equal("pending", json.RootElement.GetProperty("status").GetString())
            Assert.Equal(w.Stub.ActorId "alice", json.RootElement.GetProperty("actor").GetProperty("id").GetString())
            Assert.Equal($"@alice@{w.Stub.Authority}", json.RootElement.GetProperty("actor").GetProperty("handle").GetString())

            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not w.Stub.Posts.IsEmpty)
            let post, activity = (received w "alice").Head
            Assert.Equal("Follow", get activity "type")
            Assert.Equal(ourActor "u-1", get activity "actor")
            Assert.Equal(w.Stub.ActorId "alice", get activity "object")
            Assert.StartsWith("https://megafono.test/ap/activities/", get activity "id")

            // Signed with the follower's own key, digest included
            let! pem = Db.scalar w.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use pub = KeyVault.importPublicPem (string pem)
            let request: HttpSignatures.SignedRequest = { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }
            Assert.Equal(Ok "https://megafono.test/ap/actors/u-1#main-key", HttpSignatures.draftCavage.ExtractKeyId request)
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify w.Host.Time pub request)

            // The row carries the Follow's id and the response's followId
            let! r = row w "u-1" "alice"
            let rowId, rowStatus, rowFollow = r.Value
            Assert.Equal("pending", rowStatus)
            Assert.Equal(get activity "id", rowFollow)
            Assert.Equal(rowId, json.RootElement.GetProperty("followId").GetString())
            Assert.Empty(events w "follow.accepted")
        }

    [<Fact>]
    member _.``FR-034 following by actor URL works the same, and an unresolvable target is 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! status, _ = follow w "u-1" (w.Stub.ActorId "bea")
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not (received w "bea").IsEmpty)

            let! unknown, text = follow w "u-1" $"@nobody@{w.Stub.Authority}"
            Assert.Equal(HttpStatusCode.NotFound, unknown)
            Assert.Contains("not_found", text)
            let! noTarget, _ = internalCall w HttpMethod.Post "/internal/actors/u-1/following" (Some "{}")
            let! badJson, _ = internalCall w HttpMethod.Post "/internal/actors/u-1/following" (Some "nope")
            Assert.Equal(HttpStatusCode.BadRequest, noTarget)
            Assert.Equal(HttpStatusCode.BadRequest, badJson)
            let! unknownUser, _ = follow w "u-gone" $"@alice@{w.Stub.Authority}"
            Assert.Equal(HttpStatusCode.NotFound, unknownUser)
        }

    [<Fact>]
    member _.``FR-034 a remote Accept makes the follow accepted and reports follow.accepted as outbound``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not (received w "alice").IsEmpty)
            let followJson, _ = sentFollow w "alice"

            let! status = answer w "alice" "Accept" "1" followJson
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents w "follow.accepted" 1

            let e = (events w "follow.accepted").Head
            Assert.True e.SignatureValid
            Assert.Equal("u-1", get e.Json "user.id")
            Assert.Equal("outbound", get e.Json "follow.direction")
            Assert.Equal("accepted", get e.Json "follow.status")
            Assert.Equal(w.Stub.ActorId "alice", get e.Json "actor.id")
            let! r = row w "u-1" "alice"
            Assert.Equal("accepted", status' r)
            Assert.Equal((let id, _, _ = r.Value in id), get e.Json "follow.id")
        }

    [<Fact>]
    member _.``FR-034 a remote Reject makes the follow rejected, and a Reject after an Accept ends it``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            let! _ = follow w "u-1" (w.Stub.ActorId "bea")
            do! waitUntil "both Follows" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length = 2)
            let aliceFollow, _ = sentFollow w "alice"
            let beaFollow, _ = sentFollow w "bea"

            // alice rejects the request
            let! _ = answer w "alice" "Reject" "1" aliceFollow
            do! waitForEvents w "follow.rejected" 1
            let! aliceRow = row w "u-1" "alice"
            Assert.Equal("rejected", status' aliceRow)

            // bea accepts, then revokes
            let! _ = answer w "bea" "Accept" "1" beaFollow
            do! waitForEvents w "follow.accepted" 1
            let! _ = answer w "bea" "Reject" "2" beaFollow
            do! waitForEvents w "follow.rejected" 2
            let! beaRow = row w "u-1" "bea"
            Assert.Equal("rejected", status' beaRow)
            let rejected = events w "follow.rejected"
            Assert.All(rejected, fun e -> Assert.Equal("outbound", get e.Json "follow.direction"))
        }

    [<Fact>]
    member _.``R-05 the answer is matched by the Follow's id, a bare id, or the actor and target, and only the target may answer``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            let! _ = follow w "u-1" (w.Stub.ActorId "bea")
            let! _ = follow w "u-1" (w.Stub.ActorId "carl")
            do! waitUntil "all Follows" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length = 3)
            let _, aliceId = sentFollow w "alice"

            // A bare id
            let! _ = answer w "alice" "Accept" "1" $"\"{aliceId}\""
            // An embedded Follow with another id: found by actor and target
            let beaNode = $"""{{"id":"{w.Stub.ActorId "bea"}/other-id","type":"Follow","actor":"{ourActor "u-1"}","object":"{w.Stub.ActorId "bea"}"}}"""
            let! _ = answer w "bea" "Accept" "1" beaNode
            do! waitForEvents w "follow.accepted" 2

            // mallory answers a Follow of carl: not the target, ignored
            let carlFollow, _ = sentFollow w "carl"
            let! ignored = answer w "mallory" "Accept" "1" carlFollow
            Assert.Equal(HttpStatusCode.Accepted, ignored)
            do! Task.Delay 600
            let! carlRow = row w "u-1" "carl"
            Assert.Equal("pending", status' carlRow)

            // an Accept for a Follow of somebody we never followed does nothing
            let! stray = answer w "mallory" "Accept" "2" $"""{{"id":"{w.Stub.ActorId "mallory"}/f/1","type":"Follow","actor":"{ourActor "u-2"}","object":"{w.Stub.ActorId "mallory"}"}}"""
            Assert.Equal(HttpStatusCode.Accepted, stray)
            Assert.Equal(2, (events w "follow.accepted").Length)

            // A repeated Accept (same id) is handled once; a second Accept under a new id changes nothing
            let! _ = answer w "alice" "Accept" "1" $"\"{aliceId}\""
            let! _ = answer w "alice" "Accept" "2" $"\"{aliceId}\""
            do! Task.Delay 600
            Assert.Equal(2, (events w "follow.accepted").Length)
        }

    [<Fact>]
    member _.``FR-034 following again returns the current status and sends nothing``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _, firstBody = follow w "u-1" (w.Stub.ActorId "alice")
            use first = JsonDocument.Parse firstBody
            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not (received w "alice").IsEmpty)

            let! again, againBody = follow w "u-1" $"@alice@{w.Stub.Authority}"
            Assert.Equal(HttpStatusCode.Accepted, again)
            use second = JsonDocument.Parse againBody
            Assert.Equal(first.RootElement.GetProperty("followId").GetString(), second.RootElement.GetProperty("followId").GetString())
            Assert.Equal("pending", second.RootElement.GetProperty("status").GetString())

            let followJson, _ = sentFollow w "alice"
            let! _ = answer w "alice" "Accept" "1" followJson
            do! waitForEvents w "follow.accepted" 1
            let! third, thirdBody = follow w "u-1" (w.Stub.ActorId "alice")
            Assert.Equal(HttpStatusCode.Accepted, third)
            Assert.Contains("accepted", thirdBody)

            do! Task.Delay 700
            Assert.Equal(1, (received w "alice").Length)
            let! rows = Db.count w.Db "SELECT count(*) FROM follows WHERE direction = 'outbound'"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-034 unfollowing delivers Undo{Follow} embedding the original Follow and removes the follow``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not (received w "alice").IsEmpty)
            let followJson, followId = sentFollow w "alice"
            let! _ = answer w "alice" "Accept" "1" followJson
            do! waitForEvents w "follow.accepted" 1

            let! status, body = unfollow w "u-1" (w.Stub.ActorId "alice")
            Assert.Equal(HttpStatusCode.Accepted, status)
            Assert.Contains("removed", body)

            do! waitUntil "the Undo" (TimeSpan.FromSeconds 20.0) (fun () -> (received w "alice").Length = 2)
            let post, undo = (received w "alice") |> List.find (fun (_, n) -> get n "type" = "Undo")
            Assert.Equal(ourActor "u-1", get undo "actor")
            Assert.StartsWith("https://megafono.test/ap/activities/", get undo "id")
            Assert.Equal(followId, get undo "object.id")
            Assert.Equal("Follow", get undo "object.type")
            Assert.Equal(ourActor "u-1", get undo "object.actor")
            Assert.Equal(w.Stub.ActorId "alice", get undo "object.object")
            let! pem = Db.scalar w.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use pub = KeyVault.importPublicPem (string pem)
            let request: HttpSignatures.SignedRequest = { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify w.Host.Time pub request)

            let! r = row w "u-1" "alice"
            Assert.Equal("removed", status' r)

            // Not followed any more: 404, and a stale Accept does not bring it back
            let! again, _ = unfollow w "u-1" (w.Stub.ActorId "alice")
            Assert.Equal(HttpStatusCode.NotFound, again)
            let! _ = answer w "alice" "Accept" "2" followJson
            do! Task.Delay 500
            let! after = row w "u-1" "alice"
            Assert.Equal("removed", status' after)
        }

    [<Fact>]
    member _.``FR-034 unfollowing someone who is not followed is 404, and a rejected follow can be asked again``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! never, _ = unfollow w "u-1" (w.Stub.ActorId "carl")
            Assert.Equal(HttpStatusCode.NotFound, never)
            let! noParam, _ = internalCall w HttpMethod.Delete "/internal/actors/u-1/following" None
            Assert.Equal(HttpStatusCode.BadRequest, noParam)

            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            do! waitUntil "the Follow" (TimeSpan.FromSeconds 20.0) (fun () -> not (received w "alice").IsEmpty)
            let followJson, firstId = sentFollow w "alice"
            let! _ = answer w "alice" "Reject" "1" followJson
            do! waitForEvents w "follow.rejected" 1

            // After a rejection the same call makes a new request, with a new Follow id
            let! asked, body = follow w "u-1" (w.Stub.ActorId "alice")
            Assert.Equal(HttpStatusCode.Accepted, asked)
            Assert.Contains("pending", body)
            do! waitUntil "the second Follow" (TimeSpan.FromSeconds 20.0) (fun () -> (received w "alice").Length = 2)
            let ids = received w "alice" |> List.map (fun (_, n) -> get n "id")
            Assert.Equal(2, (List.distinct ids).Length)
            Assert.Contains(firstId, ids)
        }

    [<Fact>]
    member _.``FR-034 two users can follow the same actor independently``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! _ = follow w "u-1" (w.Stub.ActorId "alice")
            let! _ = follow w "u-2" (w.Stub.ActorId "alice")
            do! waitUntil "two Follows" (TimeSpan.FromSeconds 20.0) (fun () -> (received w "alice").Length = 2)
            let actors = received w "alice" |> List.map (fun (_, n) -> get n "actor") |> List.sort
            Assert.Equal<string list>([ ourActor "u-1"; ourActor "u-2" ], actors)

            // alice accepts only sam's: the answer names the follower, so only that follow changes
            let samFollow = (received w "alice") |> List.find (fun (_, n) -> get n "actor" = ourActor "u-2") |> snd
            let! _ = answer w "alice" "Accept" "1" (samFollow.ToJsonString())
            do! waitForEvents w "follow.accepted" 1
            Assert.Equal("u-2", get (events w "follow.accepted").Head.Json "user.id")
            let! one = row w "u-1" "alice"
            Assert.Equal("pending", status' one)
        }
