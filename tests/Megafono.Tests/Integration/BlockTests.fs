/// Blocks through the internal API (specs/004-blocks, contracts/internal-api.md): what creating a block does. The user
/// part is User Story 1 (T007); what is refused afterwards is `BlockEnforcementTests`.
module Megafono.Tests.Integration.BlockTests

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private domain = "megafono.test"

let private body (fields: (string * string) list) : string =
    JsonSerializer.Serialize(dict fields)

let private blockUser (rig: Rig) (user: string) (fields: (string * string) list) =
    sendInternal rig HttpMethod.Post $"/internal/actors/{user}/blocks" (Some(body fields))

let private blockActor (rig: Rig) (user: string) (actor: string) = blockUser rig user [ "actor", actor ]

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// What the stub's inboxes received of this type
let private posted (stub: StubRemote.StubRemote) (activityType: string) : (StubRemote.ReceivedPost * JsonNode) list =
    stub.Posts
    |> List.map (fun p -> p, json (Encoding.UTF8.GetString p.Body))
    |> List.filter (fun (_, a) -> jstr (at a "type") = activityType)

let private waitForPosts (stub: StubRemote.StubRemote) (activityType: string) (n: int) =
    waitUntil $"{n} {activityType} at the stub" (TimeSpan.FromSeconds 20.0) (fun () -> (posted stub activityType).Length >= n)

/// Lets anything wrongly queued reach the stub before a test asserts that nothing did
let private settle () = Task.Delay 700

let private count (rig: Rig) (sql: string) : Task<int64> = Db.count rig.Db sql

let private outboundFollow (rig: Rig) (user: string) (stub: StubRemote.StubRemote) (name: string) (status: string) : Task =
    Db.execute
        rig.Db
        $"""INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
            VALUES (gen_random_uuid(), 'outbound', '{user}', '{stub.ActorId name}', 'https://megafono.test/ap/activities/{Guid.NewGuid()}', '{status}')"""

let private followStatus (rig: Rig) (direction: string) (user: string) (stub: StubRemote.StubRemote) (name: string) : Task<string> =
    task {
        let! v =
            Db.scalar
                rig.Db
                $"SELECT status FROM follows WHERE direction = '{direction}' AND local_actor = '{user}' AND remote_actor = '{stub.ActorId name}'"

        return string v
    }

/// A delivery of one of `user`'s activities that is not due for a day, so the worker leaves it `pending`
let private pendingDelivery (rig: Rig) (user: string) (inbox: string) : Task<Guid> =
    task {
        let! activity, _ = Seed.activity rig.Db user

        do!
            Db.execute
                rig.Db
                $"""INSERT INTO deliveries (id, activity, inbox_url, host, next_attempt_at)
                    VALUES (gen_random_uuid(), '{activity}', '{inbox}', '{Uri(inbox).Authority}', now() + interval '1 day')"""

        return activity
    }

let private deliveryState (rig: Rig) (activity: Guid) : Task<string * string> =
    task {
        use command = rig.Db.CreateCommand $"SELECT status, COALESCE(last_error, '') FROM deliveries WHERE activity = '{activity}'"
        use! reader = command.ExecuteReaderAsync()
        reader.Read() |> ignore
        return reader.GetString 0, reader.GetString 1
    }

let private replyBody (inReplyTo: string) : string =
    let o = publishBody "public" "<p>a comment</p>"
    o["object"]["inReplyTo"] <- JsonValue.Create inReplyTo
    o.ToJsonString()

type BlockTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-001 blocking an actor by URL answers 201 with the block object and stores it for the user``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! status, text = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)
            let view = json text
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at view "actor"))
            Assert.Null(at view "domain")
            Assert.True(fst (DateTimeOffset.TryParse(jstr (at view "createdAt"))))

            let bob = rig.Stub.ActorId "bob"
            let! rows = count rig $"SELECT count(*) FROM blocks WHERE local_actor = 'u-1' AND actor = '{bob}' AND domain IS NULL"
            Assert.Equal(1L, rows)
            let! all = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, all)
        }

    [<Fact>]
    member _.``FR-001 R-48 blocking by handle stores the actor id: by handle then by URL is the same block, answered 200``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! status, text = blockActor rig "u-1" $"@bob@{rig.Stub.Authority}"
            Assert.Equal(201, status)
            let first = json text
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at first "actor"))

            // The same block by URL, and by the handle without its @
            let! status, text = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(200, status)
            Assert.Equal(jstr (at first "createdAt"), jstr (at (json text) "createdAt"))
            let! status, _ = blockActor rig "u-1" $"bob@{rig.Stub.Authority}"
            Assert.Equal(200, status)

            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, rows)
            do! waitForPosts rig.Stub "Block" 1
            do! settle ()
            Assert.Equal(1, (posted rig.Stub "Block").Length)
        }

    [<Fact>]
    member _.``FR-003 FR-012 pending and accepted follows in both directions end, one follow.removed each; other users keep theirs``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            do! outboundFollow rig "u-1" rig.Stub "bob" "pending"
            // bea follows bob too: alex's block is alex's alone
            do! addFollower rig.Db "u-2" rig.Stub "bob" "accepted" None
            do! outboundFollow rig "u-2" rig.Stub "bob" "accepted"

            let! status, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)

            let! inbound = followStatus rig "inbound" "u-1" rig.Stub "bob"
            let! outbound = followStatus rig "outbound" "u-1" rig.Stub "bob"
            Assert.Equal("removed", inbound)
            Assert.Equal("removed", outbound)
            let! beaInbound = followStatus rig "inbound" "u-2" rig.Stub "bob"
            let! beaOutbound = followStatus rig "outbound" "u-2" rig.Stub "bob"
            Assert.Equal("accepted", beaInbound)
            Assert.Equal("accepted", beaOutbound)

            do! waitForEvents rig "follow.removed" 2
            do! settle ()
            let removed = events rig "follow.removed"
            Assert.Equal(2, removed.Length)

            for e in removed do
                Assert.Equal("u-1", jstr (at e.Json "user.id"))
                Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
                Assert.Equal("removed", jstr (at e.Json "follow.status"))

            let directions = removed |> List.map (fun e -> jstr (at e.Json "follow.direction")) |> List.sort
            Assert.Equal<string list>([ "inbound"; "outbound" ], directions)
            // The follow ids are the rows' ids
            let! ids = Publishing.strings rig.Db "SELECT id::text FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal<string list>(ids |> List.sort, removed |> List.map (fun e -> jstr (at e.Json "follow.id")) |> List.sort)
        }

    [<Fact>]
    member _.``FR-004 SC-006 exactly one signed Block reaches the actor's own inbox, not the shared one, and nothing else is sent``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let shared = $"{rig.Stub.BaseUrl}/inbox"
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" (Some shared)

            let! status, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)
            do! waitForPosts rig.Stub "Block" 1
            do! settle ()

            // One request in all: no Undo{Follow} or Reject next to the Block (R-40)
            let post, activity = rig.Stub.Posts |> List.map (fun p -> p, json (Encoding.UTF8.GetString p.Body)) |> List.exactlyOne
            Assert.Equal("/users/bob/inbox", post.Path)
            Assert.Equal("Block", jstr (at activity "type"))
            Assert.Equal("https://megafono.test/ap/actors/u-1", jstr (at activity "actor"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at activity "object"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at activity "to.0"))
            Assert.StartsWith("https://megafono.test/ap/activities/", jstr (at activity "id"))

            let! pem = Db.scalar rig.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use pub = KeyVault.importPublicPem (string pem)
            let request: HttpSignatures.SignedRequest = { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }
            Assert.Equal(Ok "https://megafono.test/ap/actors/u-1#main-key", HttpSignatures.draftCavage.ExtractKeyId request)
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify rig.Time pub request)

            // The block remembers the activity it sent, so lifting it can undo exactly that
            let! stored = Db.scalar rig.Db "SELECT block_activity::text FROM blocks WHERE local_actor = 'u-1'"
            Assert.Equal(jstr (at activity "id"), Ids.activityId domain (Guid.Parse(string stored)))
            let! kind = Db.scalar rig.Db $"SELECT activity_type FROM activities WHERE id = '{stored}'"
            Assert.Equal("Block", string kind)
        }

    [<Fact>]
    member _.``FR-004 a repeated block answers 200, sends nothing and adds no rows``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            let! first, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, first)
            do! waitForPosts rig.Stub "Block" 1
            do! waitForEvents rig "follow.removed" 1

            let! activities = count rig "SELECT count(*) FROM activities"
            let! again, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(200, again)
            do! settle ()

            Assert.Equal(1, (posted rig.Stub "Block").Length)
            Assert.Equal(1, rig.Stub.Posts.Length)
            Assert.Equal(1, (events rig "follow.removed").Length)
            let! after = count rig "SELECT count(*) FROM activities"
            Assert.Equal(activities, after)
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-007 a waiting delivery of the user's to the actor's own inbox is skipped as blocked; the shared inbox and other users' stay``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let own = rig.Stub.InboxUrl "bob"
            let shared = $"{rig.Stub.BaseUrl}/inbox"
            let! toOwn = pendingDelivery rig "u-1" own
            let! toShared = pendingDelivery rig "u-1" shared
            let! beas = pendingDelivery rig "u-2" own

            let! status, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)

            let! state, error = deliveryState rig toOwn
            Assert.Equal("skipped", state)
            Assert.Equal("blocked", error)
            // A shared inbox serves other people too: an actor block leaves it alone
            let! state, _ = deliveryState rig toShared
            Assert.Equal("pending", state)
            // The block is alex's: bea's delivery to bob is untouched
            let! state, _ = deliveryState rig beas
            Assert.Equal("pending", state)
        }

    [<Fact>]
    member _.``SC-005 a reply the user publishes to the blocked actor's reply is not queued for the actor, bea's still is``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let rootIri = $"https://{domain}/ap/objects/{root}"

            // bob replies; the reply is recorded in alex's thread
            let note = Signed.note rig.Stub "bob" "1" rootIri
            use client = new HttpClient()
            use! response = Signed.postText client rig.Stub "bob" (rig.Host.FederationUrl + "/inbox") (createNote rig.Stub "bob" "1" note)
            Assert.Equal(Net.HttpStatusCode.Accepted, response.StatusCode)
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(1L, recorded)

            let! status, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)
            do! settle ()
            let blocksSent = (posted rig.Stub "Block").Length

            let bobsReply = noteId rig.Stub "bob" "1"

            let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-1/objects" (Some(replyBody bobsReply))
            Assert.Equal(201, status)
            let alexReply = jstr (at (json text) "id")
            let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-2/objects" (Some(replyBody bobsReply))
            Assert.Equal(201, status)
            let beaReply = jstr (at (json text) "id")

            let inboxesOf (objectId: string) =
                Publishing.strings rig.Db $"SELECT d.inbox_url FROM deliveries d JOIN activities a ON a.id = d.activity WHERE a.object = '{objectId}' AND a.activity_type = 'Create'"

            let! alexInboxes = inboxesOf alexReply
            let! beaInboxes = inboxesOf beaReply
            Assert.Empty alexInboxes
            Assert.Equal<string list>([ rig.Stub.InboxUrl "bob" ], beaInboxes)

            do! waitForPosts rig.Stub "Create" 1
            do! settle ()
            Assert.Equal(1, (posted rig.Stub "Create").Length)
            Assert.Equal(blocksSent, (posted rig.Stub "Block").Length)
        }

    [<Fact>]
    member _.``FR-004 an actor whose document cannot be had is still blocked, but no Block is sent``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let ghost = "http://127.0.0.1:1/users/ghost"
            let! status, text = blockActor rig "u-1" ghost
            Assert.Equal(201, status)
            Assert.Equal(ghost, jstr (at (json text) "actor"))
            let! rows = count rig $"SELECT count(*) FROM blocks WHERE local_actor = 'u-1' AND actor = '{ghost}' AND block_activity IS NULL"
            Assert.Equal(1L, rows)
            let! activities = count rig "SELECT count(*) FROM activities WHERE activity_type = 'Block'"
            Assert.Equal(0L, activities)
        }

    [<Fact>]
    member _.``R-48 a handle on a server that does not answer is 502 remote_unreachable, an unknown handle 404, and nothing is stored``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! status, text = blockActor rig "u-1" "@ghost@127.0.0.1:1"
            Assert.Equal(502, status)
            Assert.Equal("remote_unreachable", jstr (at (json text) "error"))
            let! status, text = blockActor rig "u-1" $"@nobody@{rig.Stub.Authority}"
            Assert.Equal(404, status)
            Assert.Equal("not_found", jstr (at (json text) "error"))
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``contract 400 for both or neither of actor and domain, a bad URL or handle, and an actor of this instance``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let bob = rig.Stub.ActorId "bob"

            for fields in
                [ [ "actor", bob; "domain", "spam.example" ]
                  []
                  [ "actor", "not a url" ]
                  [ "actor", "ftp://spam.example/users/bob" ]
                  [ "actor", "@@" ]
                  [ "actor", $"https://{domain}/ap/actors/u-2" ]
                  [ "actor", "@alex@megafono.test" ]
                  [ "actor", "https://megafono.test/users/x" ] ] do
                let! status, text = blockUser rig "u-1" fields
                Assert.Equal(400, status)
                Assert.Equal("invalid_request", jstr (at (json text) "error"))

            let! status, _ = sendInternal rig HttpMethod.Post "/internal/actors/u-1/blocks" (Some "not json")
            Assert.Equal(400, status)
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``contract 404 for a user the app does not know, 410 for a deleted one``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! status, text = blockActor rig "nobody" (rig.Stub.ActorId "bob")
            Assert.Equal(404, status)
            Assert.Equal("not_found", jstr (at (json text) "error"))

            let! status, _ = sendInternal rig HttpMethod.Delete "/internal/actors/u-2" None
            Assert.Equal(202, status)
            let! status, text = blockActor rig "u-2" (rig.Stub.ActorId "bob")
            Assert.Equal(410, status)
            Assert.Equal("gone", jstr (at (json text) "error"))
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-009 following a blocked actor is 409 blocked, by handle or URL; other users may still follow``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! status, _ = blockActor rig "u-1" (rig.Stub.ActorId "bob")
            Assert.Equal(201, status)

            for target in [ $"@bob@{rig.Stub.Authority}"; rig.Stub.ActorId "bob" ] do
                let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-1/following" (Some(body [ "target", target ]))
                Assert.Equal(409, status)
                Assert.Equal("blocked", jstr (at (json text) "error"))

            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND direction = 'outbound'"
            Assert.Equal(0L, rows)
            // bea has not blocked bob
            let! status, _ = sendInternal rig HttpMethod.Post "/internal/actors/u-2/following" (Some(body [ "target", rig.Stub.ActorId "bob" ]))
            Assert.Equal(202, status)
        }
