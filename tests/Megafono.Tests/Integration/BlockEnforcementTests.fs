/// What a block refuses afterwards (specs/004-blocks): inbound activities, signed fetches and the reply collections. The
/// user part is User Story 1 (T008); the instance part is added with User Story 2.
module Megafono.Tests.Integration.BlockEnforcementTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private domain = "megafono.test"
let private objectIri (id: string) = $"https://{domain}/ap/objects/{id}"

let private blockActor (rig: Rig) (user: string) (actor: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Post $"/internal/actors/{user}/blocks" (Some(JsonSerializer.Serialize {| actor = actor |}))
        Assert.True((status = 201 || status = 200), $"block answered {status}: {text}")
    }

let private liftActor (rig: Rig) (user: string) (actor: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Delete $"/internal/actors/{user}/blocks?actor={Uri.EscapeDataString actor}" None
        Assert.True((status = 204), $"lift answered {status}: {text}")
    }

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private count (rig: Rig) (sql: string) : Task<int64> = Db.count rig.Db sql

/// POST to a Megáfono inbox, signed as `name` on the stub
let private send (rig: Rig) (name: string) (url: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name url body
        return response.StatusCode
    }

let private sharedInbox (rig: Rig) = rig.Host.FederationUrl + "/inbox"
let private personalInbox (rig: Rig) (user: string) = $"{rig.Host.FederationUrl}/ap/actors/{user}/inbox"

let private reply (rig: Rig) (name: string) (suffix: string) (inReplyTo: string) : Task<HttpStatusCode> =
    send rig name (sharedInbox rig) (createNote rig.Stub name suffix (Signed.note rig.Stub name suffix inReplyTo))

let private getJson (rig: Rig) (path: string) : Task<int * JsonNode> =
    task {
        use! response = federationGet rig path []
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, (if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text)
    }

let private total (node: JsonNode) : int = node["totalItems"].GetValue<int>()
let private items (node: JsonNode) : string list = [ for i in (node["items"] :?> JsonArray) -> jstr i ]

/// A rig whose stub server has bob and carol
let private rigWithStub (pg: PostgresFixture) : Task<Rig> = start pg [ "bob"; "carol" ]

type BlockEnforcementTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-006 FR-010 a Follow from the blocked actor to the user is 202 and dropped; to another user, or from another actor, it still works``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)
            Assert.Empty(events rig "follow.requested")
            Assert.Empty(events rig "follow.accepted")
            // Nothing is answered: the only thing bob's server got is the Block itself, no Accept
            do! waitUntil "the Block" (TimeSpan.FromSeconds 20.0) (fun () -> not rig.Stub.Posts.IsEmpty)
            do! settle ()
            let types = rig.Stub.Posts |> List.map (fun p -> jstr (at (json (Encoding.UTF8.GetString p.Body)) "type"))
            Assert.Equal<string list>([ "Block" ], types)

            // bob to bea: bea has not blocked him
            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "2" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! beas = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2' AND status = 'accepted'"
            Assert.Equal(1L, beas)
            do! waitForEvents rig "follow.accepted" 1
            Assert.Equal("u-2", jstr (at (events rig "follow.accepted").Head.Json "user.id"))

            // carol to alex: the block names bob alone, though both live on one server
            let! status = send rig "carol" (sharedInbox rig) (followBody rig.Stub "carol" "3" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let carol = rig.Stub.ActorId "carol"
            let! alexs = count rig $"SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{carol}'"
            Assert.Equal(1L, alexs)
        }

    [<Fact>]
    member _.``FR-006 FR-010 a reply and an edit from the blocked actor are 202 and not recorded, a reply from another actor is``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"

            // bob replied before the block
            let! status = reply rig "bob" "1" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! status = reply rig "bob" "2" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            // an edit of the earlier reply
            let edited = Signed.note rig.Stub "bob" "1" (objectIri root)
            edited["content"] <- JsonValue.Create "<p>edited</p>"
            edited["updated"] <- JsonValue.Create "2026-10-12T18:30:00Z"
            let! status = send rig "bob" (sharedInbox rig) (updateNote rig.Stub "bob" "1" "e1" edited)
            Assert.Equal(HttpStatusCode.Accepted, status)
            // a reply to bob's own reply, still in alex's thread
            let! status = reply rig "bob" "3" (noteId rig.Stub "bob" "1")
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()

            Assert.Equal(1, (events rig "reply.received").Length)
            Assert.Empty(events rig "reply.updated")
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(1L, recorded)
            let bob2, bob3 = noteId rig.Stub "bob" "2", noteId rig.Stub "bob" "3"
            let! second = count rig $"SELECT count(*) FROM replies WHERE id = '{bob2}' OR id = '{bob3}'"
            Assert.Equal(0L, second)

            // carol, on the same server, is not blocked
            let! status = reply rig "carol" "1" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 2
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(2L, recorded)
        }

    [<Fact>]
    member _.``FR-006 a reply to the blocking user's own reply is dropped when that user blocks the signer, even in someone else's thread``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            // bea (u-2) replies in alex's thread
            let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-2/objects" (Some(let o = publishBody "public" "<p>from bea</p>" in o["object"]["inReplyTo"] <- JsonValue.Create(objectIri root); o.ToJsonString()))
            Assert.Equal(201, status)
            let beaReply = objectIri (jstr (at (json text) "id"))

            do! blockActor rig "u-2" (rig.Stub.ActorId "bob")

            let! status = reply rig "bob" "1" beaReply
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = reply rig "carol" "1" beaReply
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1
            do! settle ()
            let bobsId, carolsId = noteId rig.Stub "bob" "1", noteId rig.Stub "carol" "1"
            let! bobs = count rig $"SELECT count(*) FROM replies WHERE id = '{bobsId}'"
            let! carols = count rig $"SELECT count(*) FROM replies WHERE id = '{carolsId}'"
            Assert.Equal(0L, bobs)
            Assert.Equal(1L, carols)
        }

    [<Fact>]
    member _.``FR-006 anything the blocked actor sends to the user's own inbox is 202 and dropped before the body is parsed``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            // Garbage is a 400 for anyone else, so a 202 shows it was dropped before it was read
            let! status = send rig "carol" (personalInbox rig "u-1") "not json at all"
            Assert.Equal(HttpStatusCode.BadRequest, status)
            let! status = send rig "bob" (personalInbox rig "u-1") "not json at all"
            Assert.Equal(HttpStatusCode.Accepted, status)

            let! status = send rig "bob" (personalInbox rig "u-1") (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)

            // The block is alex's: bea's own inbox still reads bob's requests
            let! status = send rig "bob" (personalInbox rig "u-2") "not json at all"
            Assert.Equal(HttpStatusCode.BadRequest, status)
            let! status = send rig "bob" (personalInbox rig "u-2") (followBody rig.Stub "bob" "2" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2'"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-008 SC-004 fetches signed by the blocked actor's key are 404 on every path of the user, and only the blocked key``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            let! pub = publish rig "u-1" "public" "<p>hello</p>"
            let! friends = publish rig "u-1" "followers" "<p>for friends</p>"
            let! activity = createActivityId rig pub
            let! friendsActivity = createActivityId rig friends
            let! beas = publish rig "u-2" "public" "<p>from bea</p>"

            let paths =
                [ "/ap/actors/u-1"
                  "/ap/actors/u-1/followers"
                  "/ap/actors/u-1/following"
                  "/ap/actors/u-1/outbox"
                  objectPath pub
                  $"{objectPath pub}/replies"
                  $"/ap/contexts/{pub}"
                  $"/ap/activities/{activity}" ]

            // Before the block bob reads all of it, and the object only his follow let him see
            for path in paths @ [ objectPath friends; $"/ap/activities/{friendsActivity}" ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"before the block {path}: {response.StatusCode}")

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            for path in paths @ [ objectPath friends; $"/ap/activities/{friendsActivity}" ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.NotFound), $"after the block {path}: {response.StatusCode}")
                Assert.False(response.Headers.Contains "WWW-Authenticate", $"{path}: no hint")
                let! text = response.Content.ReadAsStringAsync()
                Assert.Equal("", text)

            // Unsigned, and signed by someone else, nothing changed
            for path in paths do
                use! response = federationGet rig path []
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"unsigned {path}: {response.StatusCode}")
                use! response = signedGet rig rig.Stub "carol" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"carol {path}: {response.StatusCode}")

            // The block is alex's: bob still reads bea's actor and posts
            for path in [ "/ap/actors/u-2"; objectPath beas ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"bea {path}: {response.StatusCode}")

            // A block lifted, bob reads again
            do! liftActor rig "u-1" (rig.Stub.ActorId "bob")
            use! response = signedGet rig rig.Stub "bob" (objectPath pub)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        }

    [<Fact>]
    member _.``FR-011 the blocked actor's replies leave the replies and context collections, counts included, and return when the block is lifted``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! _ = reply rig "bob" "1" (objectIri root)
            let! _ = reply rig "bob" "2" (objectIri root)
            let! _ = reply rig "carol" "1" (objectIri root)
            let! _ = reply rig "bob" "3" (noteId rig.Stub "carol" "1")
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(4L, recorded)

            let bobs = [ noteId rig.Stub "bob" "1"; noteId rig.Stub "bob" "2" ]
            let carols = noteId rig.Stub "carol" "1"

            let read () =
                task {
                    let! _, whole = getJson rig $"{objectPath root}/replies"
                    let! _, doc = getJson rig (objectPath root)
                    let! _, page = getJson rig $"{objectPath root}/replies?page=1"
                    let! _, context = getJson rig $"/ap/contexts/{root}"
                    return whole, doc, page, context
                }

            let! whole, doc, page, context = read ()
            Assert.Equal(3, total whole)
            Assert.Equal(3, total doc["replies"])
            Assert.Equal(3, (items page).Length)
            Assert.Equal(5, total context)

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! whole, doc, page, context = read ()
            Assert.Equal(1, total whole)
            Assert.Equal<string list>([ carols ], items whole["first"])
            Assert.Equal(1, total doc["replies"])
            Assert.Equal<string list>([ carols ], items (doc["replies"]["first"]))
            Assert.Equal<string list>([ carols ], items page)
            // The thread: the root, carol's reply; bob's three are out (his reply to carol's is bob's too)
            Assert.Equal(2, total context)
            let! _, contextPage = getJson rig $"/ap/contexts/{root}?page=1"
            Assert.Equal<string list>([ objectIri root; carols ], items contextPage)
            // The rows stay: lifting shows them again
            let! recorded = count rig "SELECT count(*) FROM replies WHERE state = 'live'"
            Assert.Equal(4L, recorded)

            do! liftActor rig "u-1" (rig.Stub.ActorId "bob")
            let! whole, _, _, context = read ()
            Assert.Equal(3, total whole)
            Assert.Equal(5, total context)
            Assert.Contains(bobs.Head, items (whole["first"]))
        }
