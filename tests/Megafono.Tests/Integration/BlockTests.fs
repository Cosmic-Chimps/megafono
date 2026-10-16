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

    // ---- instance blocks (User Story 2, T015) -------------------------------------------------------------

    [<Fact>]
    member _.``FR-002 FR-003 FR-004 a domain block ends every user's follows with that domain and its subdomains, with an event to each user``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let at' = DateTimeOffset.UtcNow
            let spam, sub, subPort = "https://spam.example/users/a", "https://a.spam.example/users/b", "https://c.spam.example:8443/users/c"
            let cousin, longer = "https://notspam.example/users/d", "https://spam.example.org/users/e"

            // alex: an inbound follow from the domain, one from its subdomain, one from a lookalike
            do! Seed.followRow rig.Db "inbound" "u-1" spam "accepted" at'
            do! Seed.followRow rig.Db "inbound" "u-1" sub "pending" at'
            do! Seed.followRow rig.Db "inbound" "u-1" cousin "accepted" at'
            // bea: an outbound follow of the subdomain (with a port) and an inbound one from a longer name
            do! Seed.followRow rig.Db "outbound" "u-2" subPort "accepted" at'
            do! Seed.followRow rig.Db "inbound" "u-2" longer "accepted" at'

            let! status, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "Spam.Example." ]))
            Assert.Equal(201, status)
            let view = json text
            Assert.Equal("spam.example", jstr (at view "domain"))
            Assert.Null(at view "actor")
            Assert.True(fst (DateTimeOffset.TryParse(jstr (at view "createdAt"))))
            let! rows = count rig "SELECT count(*) FROM blocks WHERE local_actor IS NULL AND domain = 'spam.example' AND actor IS NULL"
            Assert.Equal(1L, rows)

            let statusOf (user: string) (actor: string) =
                task {
                    let! v = Db.scalar rig.Db $"SELECT status FROM follows WHERE local_actor = '{user}' AND remote_actor = '{actor}'"
                    return string v
                }

            for user, actor in [ "u-1", spam; "u-1", sub; "u-2", subPort ] do
                let! s = statusOf user actor
                Assert.Equal("removed", s)

            for user, actor in [ "u-1", cousin; "u-2", longer ] do
                let! s = statusOf user actor
                Assert.Equal("accepted", s)

            do! waitForEvents rig "follow.removed" 3
            do! settle ()
            let removed = events rig "follow.removed"
            Assert.Equal(3, removed.Length)

            let perUser =
                removed
                |> List.map (fun e -> jstr (at e.Json "user.id"), jstr (at e.Json "actor.id"), jstr (at e.Json "follow.direction"))
                |> List.sort

            Assert.Equal<(string * string * string) list>(
                [ "u-1", sub, "inbound"; "u-1", spam, "inbound"; "u-2", subPort, "outbound" ] |> List.sort,
                perUser
            )
        }

    [<Fact>]
    member _.``FR-004 a domain block cancels every waiting delivery to the domain whoever the author, and nothing is queued afterwards``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! alexs = pendingDelivery rig "u-1" "https://spam.example/inbox"
            let! beas = pendingDelivery rig "u-2" "https://a.spam.example/users/b/inbox"
            let! elsewhere = pendingDelivery rig "u-1" "https://notspam.example/inbox"

            let! status, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "spam.example" ]))
            Assert.Equal(201, status)

            for activity in [ alexs; beas ] do
                let! state, error = deliveryState rig activity
                Assert.Equal("skipped", state)
                Assert.Equal("blocked", error)

            let! state, _ = deliveryState rig elsewhere
            Assert.Equal("pending", state)

            // The enqueue leaves the blocked host out for every author
            let! activity, _ = Seed.activity rig.Db "u-2"

            let! inserted =
                Seed.enqueueDeliveries rig.Db rig.Time activity [ "https://spam.example/inbox"; "https://x.spam.example/inbox"; "https://notspam.example/inbox" ]

            Assert.Equal(1, inserted)
            let! hosts = Publishing.strings rig.Db $"SELECT host FROM deliveries WHERE activity = '{activity}'"
            Assert.Equal<string list>([ "notspam.example" ], hosts)
        }

    [<Fact>]
    member _.``FR-004 FR-009 a domain block sends nothing to the server and every lookup of its handles is 409 blocked without a request``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            do! outboundFollow rig "u-2" rig.Stub "bob" "pending"
            let handle = $"@bob@{rig.Stub.Authority}"
            let requestsBefore = rig.Stub.Gets.Length + rig.Stub.Posts.Length

            // The stub is 127.0.0.1:port; a block names the host, whatever the port
            let! status, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "127.0.0.1" ]))
            Assert.Equal(201, status)

            let! inbound = followStatus rig "inbound" "u-1" rig.Stub "bob"
            let! outbound = followStatus rig "outbound" "u-2" rig.Stub "bob"
            Assert.Equal("removed", inbound)
            Assert.Equal("removed", outbound)
            do! waitForEvents rig "follow.removed" 2

            // Nothing is told to the blocked server: no Block, Reject or Undo (R-41)
            do! settle ()
            Assert.Empty rig.Stub.Posts

            use client = rig.Host.InternalClient(Some MegafonoHost.token)
            use! lookup = client.GetAsync $"/internal/remote-actors?handle={Uri.EscapeDataString handle}"
            Assert.Equal(409, int lookup.StatusCode)
            let! text = lookup.Content.ReadAsStringAsync()
            Assert.Equal("blocked", jstr (at (json text) "error"))

            for target in [ handle; rig.Stub.ActorId "bob" ] do
                let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-1/following" (Some(body [ "target", target ]))
                Assert.Equal(409, status)
                Assert.Equal("blocked", jstr (at (json text) "error"))

            // Blocking a user of that server by handle is refused the same way: nothing is contacted
            let! status, text = blockActor rig "u-1" handle
            Assert.Equal(409, status)
            Assert.Equal("blocked", jstr (at (json text) "error"))
            Assert.Equal(requestsBefore, rig.Stub.Gets.Length + rig.Stub.Posts.Length)
        }

    [<Fact>]
    member _.``FR-002 an instance block of one actor covers only that actor: carol on the same server, the shared inbox and the others stay``() : Task =
        task {
            let! rig = start pg [ "bob"; "carol" ]
            use _ = disposeRig rig
            let shared = $"{rig.Stub.BaseUrl}/inbox"
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" (Some shared)
            do! addFollower rig.Db "u-2" rig.Stub "bob" "pending" (Some shared)
            do! addFollower rig.Db "u-1" rig.Stub "carol" "accepted" (Some shared)
            let! toBob = pendingDelivery rig "u-2" (rig.Stub.InboxUrl "bob")
            let! toCarol = pendingDelivery rig "u-1" (rig.Stub.InboxUrl "carol")
            let! toShared = pendingDelivery rig "u-1" shared

            let! status, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "actor", rig.Stub.ActorId "bob" ]))
            Assert.Equal(201, status)
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at (json text) "actor"))
            Assert.Null(at (json text) "domain")

            // Both users' follows of bob end, carol's stays
            let! alexBob = followStatus rig "inbound" "u-1" rig.Stub "bob"
            let! beaBob = followStatus rig "inbound" "u-2" rig.Stub "bob"
            let! alexCarol = followStatus rig "inbound" "u-1" rig.Stub "carol"
            Assert.Equal("removed", alexBob)
            Assert.Equal("removed", beaBob)
            Assert.Equal("accepted", alexCarol)
            do! waitForEvents rig "follow.removed" 2
            do! settle ()
            Assert.Equal(2, (events rig "follow.removed").Length)
            Assert.Equal<string list>([ "u-1"; "u-2" ], events rig "follow.removed" |> List.map (fun e -> jstr (at e.Json "user.id")) |> List.sort)

            // bob's own inbox is cut off whoever the author; the shared inbox serves carol too
            let! state, error = deliveryState rig toBob
            Assert.Equal("skipped", state)
            Assert.Equal("blocked", error)
            let! state, _ = deliveryState rig toCarol
            Assert.Equal("pending", state)
            let! state, _ = deliveryState rig toShared
            Assert.Equal("pending", state)
            Assert.Empty rig.Stub.Posts
        }

    [<Fact>]
    member _.``FR-002 a repeated instance block answers 200 and changes nothing``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! first, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "spam.example" ]))
            Assert.Equal(201, first)
            do! Seed.followRow rig.Db "inbound" "u-1" "https://spam.example/users/a" "accepted" DateTimeOffset.UtcNow

            let! again, again' = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "SPAM.example" ]))
            Assert.Equal(200, again)
            Assert.Equal(jstr (at (json text) "createdAt"), jstr (at (json again') "createdAt"))
            // The repeat does not re-run the block: a follow that appeared after the first one is left alone
            let! v = Db.scalar rig.Db "SELECT status FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal("accepted", string v)
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, rows)
            do! settle ()
            Assert.Empty(events rig "follow.removed")
        }

    [<Fact>]
    member _.``contract an instance block of the instance's own domain or a parent of it, or a malformed one, is 400``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig

            for fields in
                [ [ "domain", "megafono.test" ]
                  [ "domain", "MEGAFONO.test." ]
                  [ "domain", "test" ]
                  [ "domain", "spam.example:8443" ]
                  [ "domain", "https://spam.example" ]
                  [ "domain", "bad domain" ]
                  [ "domain", "spam.example"; "actor", rig.Stub.ActorId "bob" ]
                  []
                  [ "actor", "https://megafono.test/ap/actors/u-1" ]
                  [ "actor", "@alex@megafono.test" ] ] do
                let! status, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body fields))
                Assert.Equal(400, status)
                Assert.Equal("invalid_request", jstr (at (json text) "error"))

            // a lookalike is another name, not the instance's own
            let! status, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(body [ "domain", "notmegafono.test" ]))
            Assert.Equal(201, status)
            let! rows = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, rows)
        }

    // ---- a user blocks a whole server (User Story 3, T021) ------------------------------------------------

    [<Fact>]
    member _.``FR-004 FR-005 a user domain block sends a Reject of each inbound follow and an Undo of each outbound one, each at the actor's own inbox``() : Task =
        task {
            let! rig = start pg [ "bob"; "carol" ]
            use _ = disposeRig rig
            let shared = $"{rig.Stub.BaseUrl}/inbox"
            // alex: bob follows (with a shared inbox), carol's request is pending, and alex follows bob
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" (Some shared)
            do! addFollower rig.Db "u-1" rig.Stub "carol" "pending" None
            do! outboundFollow rig "u-1" rig.Stub "bob" "accepted"
            // bea has the same server's bob as a follower: the block is alex's alone
            do! addFollower rig.Db "u-2" rig.Stub "bob" "accepted" None
            let! toBob = pendingDelivery rig "u-1" (rig.Stub.InboxUrl "bob")
            let! toShared = pendingDelivery rig "u-1" shared
            let! beas = pendingDelivery rig "u-2" (rig.Stub.InboxUrl "bob")

            let! status, text = blockUser rig "u-1" [ "domain", "127.0.0.1" ]
            Assert.Equal(201, status)
            Assert.Equal("127.0.0.1", jstr (at (json text) "domain"))
            Assert.Null(at (json text) "actor")

            do! waitForPosts rig.Stub "Reject" 2
            do! waitForPosts rig.Stub "Undo" 1
            do! settle ()
            // Three requests in all: no Block next to them (R-41), and none at the shared inbox
            let sent = rig.Stub.Posts |> List.map (fun p -> p, json (Encoding.UTF8.GetString p.Body))
            Assert.Equal(3, sent.Length)
            Assert.Empty(posted rig.Stub "Block")
            Assert.DoesNotContain(sent, fun (p, _) -> p.Path = "/inbox")

            let local = "https://megafono.test/ap/actors/u-1"

            let rejects = posted rig.Stub "Reject" |> List.sortBy (fun (p, _) -> p.Path)
            Assert.Equal<string list>([ "/users/bob/inbox"; "/users/carol/inbox" ], rejects |> List.map (fun (p, _) -> p.Path))

            for (_, activity), name in List.zip rejects [ "bob"; "carol" ] do
                Assert.Equal(local, jstr (at activity "actor"))
                // The Follow it answers, by the id the remote side sent it with
                Assert.Equal(Signed.followId rig.Stub name "1", jstr (at activity "object.id"))
                Assert.Equal("Follow", jstr (at activity "object.type"))
                Assert.Equal(rig.Stub.ActorId name, jstr (at activity "object.actor"))
                Assert.Equal(local, jstr (at activity "object.object"))

            // The Undo of alex's own Follow, with the id Megáfono sent it under
            let! followIri = Db.scalar rig.Db $"SELECT follow_activity_id FROM follows WHERE direction = 'outbound' AND local_actor = 'u-1'"
            let undo, activity = posted rig.Stub "Undo" |> List.exactlyOne
            Assert.Equal("/users/bob/inbox", undo.Path)
            Assert.Equal(local, jstr (at activity "actor"))
            Assert.Equal(string followIri, jstr (at activity "object.id"))
            Assert.Equal("Follow", jstr (at activity "object.type"))
            Assert.Equal(local, jstr (at activity "object.actor"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at activity "object.object"))

            // The follows ended, each with an event to alex; bea's did not
            for direction, name in [ "inbound", "bob"; "inbound", "carol"; "outbound", "bob" ] do
                let! s = followStatus rig direction "u-1" rig.Stub name
                Assert.Equal("removed", s)

            let! beasFollow = followStatus rig "inbound" "u-2" rig.Stub "bob"
            Assert.Equal("accepted", beasFollow)
            do! waitForEvents rig "follow.removed" 3
            Assert.Equal(3, (events rig "follow.removed").Length)
            Assert.All(events rig "follow.removed", fun e -> Assert.Equal("u-1", jstr (at e.Json "user.id")))

            // alex's waiting deliveries to the domain are skipped, the shared inbox's too; bea's is not
            for activity in [ toBob; toShared ] do
                let! state, error = deliveryState rig activity
                Assert.Equal("skipped", state)
                Assert.Equal("blocked", error)

            let! state, _ = deliveryState rig beas
            Assert.Equal("pending", state)

            // The block belongs to alex and sent no Block, so there is nothing to undo later
            let! rows = count rig $"SELECT count(*) FROM blocks WHERE local_actor = 'u-1' AND domain = '127.0.0.1' AND actor IS NULL AND block_activity IS NULL"
            Assert.Equal(1L, rows)
            let! all = count rig "SELECT count(*) FROM blocks"
            Assert.Equal(1L, all)
        }

    [<Fact>]
    member _.``FR-005 a user domain block covers the domain and its subdomains, with a Reject or Undo each, and not a lookalike name``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let at' = DateTimeOffset.UtcNow
            let spam, sub, subPort = "https://spam.example/users/a", "https://a.spam.example/users/b", "https://c.spam.example:8443/users/c"
            let cousin, longer = "https://notspam.example/users/d", "https://spam.example.org/users/e"

            do! Seed.followRow rig.Db "inbound" "u-1" spam "accepted" at'
            do! Seed.followRow rig.Db "inbound" "u-1" sub "pending" at'
            do! Seed.followRow rig.Db "outbound" "u-1" subPort "accepted" at'
            do! Seed.followRow rig.Db "inbound" "u-1" cousin "accepted" at'
            do! Seed.followRow rig.Db "inbound" "u-1" longer "accepted" at'
            // bea has the blocked domain's actor too
            do! Seed.followRow rig.Db "inbound" "u-2" spam "accepted" at'

            let! status, text = blockUser rig "u-1" [ "domain", "Spam.Example." ]
            Assert.Equal(201, status)
            Assert.Equal("spam.example", jstr (at (json text) "domain"))

            let statusOf (user: string) (actor: string) =
                task {
                    let! v = Db.scalar rig.Db $"SELECT status FROM follows WHERE local_actor = '{user}' AND remote_actor = '{actor}'"
                    return string v
                }

            for actor in [ spam; sub; subPort ] do
                let! s = statusOf "u-1" actor
                Assert.Equal("removed", s)

            for user, actor in [ "u-1", cousin; "u-1", longer; "u-2", spam ] do
                let! s = statusOf user actor
                Assert.Equal("accepted", s)

            // What was queued: per ended follow one activity of alex's, to that actor's own inbox, and nothing else
            let! queued =
                Publishing.strings
                    rig.Db
                    """SELECT a.activity_type || ' ' || d.inbox_url FROM deliveries d JOIN activities a ON a.id = d.activity
                       ORDER BY d.inbox_url"""

            Assert.Equal<string list>(
                [ $"Reject {sub}/inbox"; $"Reject {spam}/inbox"; $"Undo {subPort}/inbox" ] |> List.sortBy (fun s -> s.Substring(s.IndexOf ' ' + 1)),
                queued
            )

            let! authors = Publishing.strings rig.Db "SELECT DISTINCT local_actor FROM activities"
            Assert.Equal<string list>([ "u-1" ], authors)

            // The Reject names the Follow of its row; the Undo the row of alex's own Follow
            let! follows = Publishing.strings rig.Db "SELECT follow_activity_id FROM follows WHERE status = 'removed'"
            let! bodies = Publishing.strings rig.Db "SELECT convert_from(body, 'UTF8') FROM activities"
            let embedded = bodies |> List.map (fun b -> jstr (at (json b) "object.id"))
            Assert.Equal<string list>(follows |> List.sort, embedded |> List.sort)

            do! waitForEvents rig "follow.removed" 3
            do! settle ()
            Assert.Equal(3, (events rig "follow.removed").Length)
            Assert.All(events rig "follow.removed", fun e -> Assert.Equal("u-1", jstr (at e.Json "user.id")))
        }

    [<Fact>]
    member _.``FR-006 FR-008 FR-010 afterwards a Follow from the domain to the user is 202 and dropped, to another user it is handled, and the domain's signed fetches of the user are 404``() : Task =
        task {
            let! rig = start pg [ "bob"; "carol" ]
            use _ = disposeRig rig
            let! alexs = publish rig "u-1" "public" "<p>from alex</p>"
            let! beas = publish rig "u-2" "public" "<p>from bea</p>"

            let! status, _ = blockUser rig "u-1" [ "domain", "127.0.0.1" ]
            Assert.Equal(201, status)

            // Shared inbox, and garbage at the personal one: dropped before the body is read
            let! status = send rig "bob" (rig.Host.FederationUrl + "/inbox") (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(Net.HttpStatusCode.Accepted, status)
            let! status = send rig "carol" (rig.Host.FederationUrl + "/ap/actors/u-1/inbox") "not json at all"
            Assert.Equal(Net.HttpStatusCode.Accepted, status)
            do! settle ()
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)
            Assert.Empty(events rig "follow.requested")
            Assert.Empty(events rig "follow.accepted")

            // The same server's carol follows bea, who blocked nobody
            let! status = send rig "carol" (rig.Host.FederationUrl + "/inbox") (followBody rig.Stub "carol" "2" (ourActor "u-2"))
            Assert.Equal(Net.HttpStatusCode.Accepted, status)
            do! waitForEvents rig "follow.accepted" 1
            Assert.Equal("u-2", jstr (at (events rig "follow.accepted").Head.Json "user.id"))
            let! beasFollowers = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2' AND status = 'accepted'"
            Assert.Equal(1L, beasFollowers)

            // Either user of the blocked domain reads nothing of alex's, but all of bea's
            for name in [ "bob"; "carol" ] do
                for path in [ "/ap/actors/u-1"; objectPath alexs; $"{objectPath alexs}/replies"; "/ap/actors/u-1/followers" ] do
                    use! response = signedGet rig rig.Stub name path
                    Assert.True((response.StatusCode = Net.HttpStatusCode.NotFound), $"{name} {path}: {response.StatusCode}")

                for path in [ "/ap/actors/u-2"; objectPath beas ] do
                    use! response = signedGet rig rig.Stub name path
                    Assert.True((response.StatusCode = Net.HttpStatusCode.OK), $"{name} {path}: {response.StatusCode}")

            use! response = federationGet rig (objectPath alexs) []
            Assert.Equal(Net.HttpStatusCode.OK, response.StatusCode)
        }

    [<Fact>]
    member _.``FR-005 FR-006 FR-008 the user's domain block refuses a subdomain's key and a Follow from its actor, and lets a lookalike name through``() : Task =
        task {
            let! rig = start pg [ "bob" ]
            use _ = disposeRig rig
            let! alexs = publish rig "u-1" "public" "<p>from alex</p>"
            let! beas = publish rig "u-2" "public" "<p>from bea</p>"
            // Actors cached with the stub's key, so a request that claims their key id verifies without any server
            let! onSub = cachedActorOn rig "a.spam.example" "x"
            let! onDomain = cachedActorOn rig "spam.example" "y"
            let! lookalike = cachedActorOn rig "notspam.example" "z"
            let! longer = cachedActorOn rig "spam.example.org" "w"

            let! status, _ = blockUser rig "u-1" [ "domain", "spam.example" ]
            Assert.Equal(201, status)

            let follow (actor: string) =
                $"""{{"@context":"https://www.w3.org/ns/activitystreams","id":"{actor}/follows/1","type":"Follow","actor":"{actor}","object":"{ourActor "u-1"}"}}"""

            for actor, blocked in [ onSub, true; onDomain, true; lookalike, false; longer, false ] do
                let key = $"{actor}#main-key"

                // A Follow: 202 whatever happens; the row says whether it was handled
                let! status = sendClaiming rig "bob" (Some key) (rig.Host.FederationUrl + "/inbox") (follow actor) id
                Assert.Equal(Net.HttpStatusCode.Accepted, status)
                let! rows = count rig $"SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{actor}'"
                Assert.Equal((if blocked then 0L else 1L), rows)

                // A signed fetch of alex's post, and of bea's
                let signedBy (path: string) =
                    federationGet rig path (claiming key (rig.Stub.Sign("bob", "GET", rig.Host.FederationUrl + path, None)))

                use! response = signedBy (objectPath alexs)
                let expected = if blocked then Net.HttpStatusCode.NotFound else Net.HttpStatusCode.OK
                Assert.True((response.StatusCode = expected), $"{actor} alex's post: {response.StatusCode}")
                use! response = signedBy (objectPath beas)
                Assert.True((response.StatusCode = Net.HttpStatusCode.OK), $"{actor} bea's post: {response.StatusCode}")
        }
