module Megafono.Tests.Integration.ActorDeletionTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Xunit
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Workers
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

let private publicUrl = "https://www.w3.org/ns/activitystreams#Public"
let private actorUrl = "https://megafono.test/ap/actors/u-1"

let private deleteActor (rig: Rig) (id: string) = sendInternal rig HttpMethod.Delete $"/internal/actors/{id}" None

let private bodiesOf (stub: StubRemote.StubRemote) : (StubRemote.ReceivedPost * JsonNode) list =
    stub.Posts |> List.map (fun p -> p, json (Encoding.UTF8.GetString p.Body))

let private ofType (activityType: string) (stub: StubRemote.StubRemote) =
    bodiesOf stub |> List.filter (fun (_, a) -> jstr (at a "type") = activityType)

let private waitForPosts (stub: StubRemote.StubRemote) (activityType: string) (n: int) =
    waitUntil $"{n} {activityType} at the stub" (TimeSpan.FromSeconds 20.0) (fun () -> (ofType activityType stub).Length >= n)

/// Lets anything wrongly queued reach the stubs before a test asserts that nothing did
let private settle () = Task.Delay 700

let private get (rig: Rig) (path: string) (headers: (string * string) list) : Task<HttpStatusCode * string * string> =
    task {
        use! response = federationGet rig path headers
        let! text = response.Content.ReadAsStringAsync()
        let contentType = if isNull response.Content.Headers.ContentType then "" else response.Content.Headers.ContentType.MediaType
        return response.StatusCode, contentType, text
    }

let private count (rig: Rig) (sql: string) : Task<int64> = Db.count rig.Db sql

let private outboundFollow (rig: Rig) (stub: StubRemote.StubRemote) (name: string) (status: string) =
    Seed.followRow rig.Db "outbound" "u-1" (stub.ActorId name) status (rig.Time.GetUtcNow())

let private deleteActivityCount (rig: Rig) =
    count rig "SELECT count(*) FROM activities WHERE activity_type = 'Delete' AND object IS NULL"

let private deleteDeliveryCount (rig: Rig) =
    count
        rig
        "SELECT count(*) FROM deliveries d JOIN activities a ON a.id = d.activity WHERE a.activity_type = 'Delete' AND a.object IS NULL"

/// Waits for the stub's single failed attempt at a delivery, so that it is `pending` with a retry scheduled
let private waitForFirstFailure (rig: Rig) =
    waitUntilAsync "the first failed attempt" (TimeSpan.FromSeconds 20.0) (fun () ->
        task {
            let! n = count rig "SELECT count(*) FROM deliveries WHERE status = 'pending' AND attempts = 1"
            return n >= 1L
        })

/// An RSA key that records that it was disposed (the platform's own keys keep working after Dispose)
type private TrackedRsa() =
    inherit System.Security.Cryptography.RSA()
    member val Disposed = false with get, set
    override _.ExportParameters(_: bool) = System.Security.Cryptography.RSAParameters()
    override _.ImportParameters(_: System.Security.Cryptography.RSAParameters) = ()

    override this.Dispose(disposing: bool) =
        this.Disposed <- true
        base.Dispose disposing

type ActorDeletionTests(pg: PostgresFixture) =

    // ---- US1: the call and the Delete deliveries (T005) --------------------------------------------------

    [<Fact>]
    member _.``003 FR-002 FR-003 one signed Delete{actor} per server for followers and follows, pending or accepted``() : Task =
        task {
            let! rig = start pg [ "alice"; "carl" ]
            use _ = disposeRig rig
            let! bobs = StubRemote.start [ "bob" ]
            let! doras = StubRemote.start [ "dora" ]
            let! erins = StubRemote.start [ "erin"; "fred" ]
            let! ginas = StubRemote.start [ "gina" ]

            // gina's server only ever received a delivery: she followed, got a post, and has no follow row any more
            do! addFollower rig.Db "u-1" ginas "gina" "accepted" None
            let! _ = publish rig "u-1" "public" "<p>hello</p>"
            do! waitForPosts ginas "Create" 1
            do! Db.execute rig.Db "DELETE FROM follows"

            // alice (accepted, with a shared inbox) and carl (pending) share a server; bob is followed, dora asked
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" (Some $"{rig.Stub.BaseUrl}/inbox")
            do! addFollower rig.Db "u-1" rig.Stub "carl" "pending" None
            do! outboundFollow rig bobs "bob" "accepted"
            do! outboundFollow rig doras "dora" "pending"
            // Rejected and removed relationships do not count
            do! addFollower rig.Db "u-1" erins "erin" "rejected" None
            do! addFollower rig.Db "u-1" erins "fred" "removed" None

            let! status, text = deleteActor rig "u-1"
            Assert.Equal(202, status)
            let answer = json text
            Assert.Equal("u-1", jstr (at answer "id"))
            Assert.Equal(3, intOf (at answer "deliveries"))
            Assert.True(fst (DateTimeOffset.TryParse(jstr (at answer "deletedAt"))))

            do! waitForPosts rig.Stub "Delete" 1
            do! waitForPosts bobs "Delete" 1
            do! waitForPosts doras "Delete" 1
            do! settle ()

            // One request per server, to the shared inbox where one is known
            let toAlice = rig.Stub.Posts |> List.exactlyOne
            Assert.Equal("/inbox", toAlice.Path)
            Assert.Equal("/users/bob/inbox", (bobs.Posts |> List.exactlyOne).Path)
            Assert.Equal("/users/dora/inbox", (doras.Posts |> List.exactlyOne).Path)
            // Neither the rejected and removed relationships nor the server that only got a delivery are told
            Assert.Empty erins.Posts
            Assert.Equal<string list>([ "Create" ], bodiesOf ginas |> List.map (fun (_, a) -> jstr (at a "type")))

            // The activity: actor and object are the actor, to Public, signed by the actor's own key
            let! pem = Db.scalar rig.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use pub = KeyVault.importPublicPem (string pem)

            for stub in [ rig.Stub; bobs; doras ] do
                let post, activity = (ofType "Delete" stub).Head
                Assert.Equal("Delete", jstr (at activity "type"))
                Assert.Equal(actorUrl, jstr (at activity "actor"))
                Assert.Equal(actorUrl, jstr (at activity "object"))
                Assert.Equal<string list>([ publicUrl ], (at activity "to").AsArray() |> Seq.map jstr |> List.ofSeq)
                Assert.StartsWith("https://megafono.test/ap/activities/", jstr (at activity "id"))

                let request: HttpSignatures.SignedRequest =
                    { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }

                Assert.Equal(Ok $"{actorUrl}#main-key", HttpSignatures.draftCavage.ExtractKeyId request)
                Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify rig.Time pub request)

            let! queued = deleteDeliveryCount rig
            Assert.Equal(3L, queued)
        }

    [<Fact>]
    member _.``003 FR-003 without a shared inbox one server gets the Delete at its smallest inbox URL``() : Task =
        task {
            let! rig = start pg [ "alice"; "carl"; "bea2" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "carl" "accepted" None
            do! addFollower rig.Db "u-1" rig.Stub "bea2" "accepted" None
            do! addFollower rig.Db "u-1" rig.Stub "alice" "pending" None

            let! status, text = deleteActor rig "u-1"
            Assert.Equal(202, status)
            Assert.Equal(1, intOf (at (json text) "deliveries"))
            do! waitForPosts rig.Stub "Delete" 1
            do! settle ()
            Assert.Equal("/users/alice/inbox", (rig.Stub.Posts |> List.exactlyOne).Path)
        }

    [<Fact>]
    member _.``003 FR-004 a repeated call answers the same and queues nothing again``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None

            let! first, firstText = deleteActor rig "u-1"
            do! waitForPosts rig.Stub "Delete" 1
            rig.Time.Advance(TimeSpan.FromMinutes 5.0)
            let! second, secondText = deleteActor rig "u-1"
            let! third, thirdText = deleteActor rig "u-1"

            Assert.Equal(202, first)
            Assert.Equal(202, second)
            Assert.Equal(202, third)
            Assert.Equal(firstText, secondText)
            Assert.Equal(firstText, thirdText)
            Assert.Equal(1, intOf (at (json firstText) "deliveries"))

            do! settle ()
            Assert.Single(rig.Stub.Posts) |> ignore
            let! activities = deleteActivityCount rig
            let! deliveries = deleteDeliveryCount rig
            Assert.Equal(1L, activities)
            Assert.Equal(1L, deliveries)
        }

    [<Fact>]
    member _.``003 FR-001 an unknown id is 404, and the call needs no answer from the app``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig

            let! status, text = deleteActor rig "nobody"
            Assert.Equal(404, status)
            Assert.Equal("not_found", jstr (at (json text) "error"))
            let! malformed, _ = deleteActor rig "not%20valid"
            Assert.Equal(404, malformed)
            let! rows = count rig "SELECT count(*) FROM local_actors WHERE deleted_at IS NOT NULL"
            Assert.Equal(0L, rows)

            // The app forgot u-1 and fails for u-2: Megáfono does not consult it
            let lookups = rig.App.LookupPaths.Length
            rig.App.Users.TryRemove "u-1" |> ignore
            let! gone, goneText = deleteActor rig "u-1"
            Assert.Equal(202, gone)
            Assert.Equal(0, intOf (at (json goneText) "deliveries"))
            rig.App.LookupMode <- FakeApp.Fail500
            let! failing, _ = deleteActor rig "u-2"
            Assert.Equal(202, failing)
            Assert.Equal(lookups, rig.App.LookupPaths.Length)
            let! deleted = count rig "SELECT count(*) FROM local_actors WHERE deleted_at IS NOT NULL"
            Assert.Equal(2L, deleted)
        }

    // ---- US2: a deleted actor stays gone (T007) ----------------------------------------------------------

    [<Fact>]
    member _.``003 FR-005 FR-006 the actor and its collections answer 410 with an actor Tombstone, whatever the app says``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            rig.App.AddUser { FakeApp.user "u-3" "club" with Type = "Group" }
            use! _ = federationGet rig "/ap/actors/u-3" []

            let! status, text = deleteActor rig "u-1"
            let! groupStatus, _ = deleteActor rig "u-3"
            Assert.Equal(202, status)
            Assert.Equal(202, groupStatus)
            let deletedAt = jstr (at (json text) "deletedAt")
            let! rows = count rig "SELECT count(*) FROM local_actors"
            let! keysBefore = Db.scalar rig.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"

            // The fake app still knows alex, and later a changed profile too
            rig.App.AddUser { FakeApp.user "u-1" "alex" with Name = "Alex again" }
            rig.Time.Advance(TimeSpan.FromHours 1.0)

            for path in [ "/ap/actors/u-1"; "/ap/actors/u-1/followers"; "/ap/actors/u-1/following"; "/ap/actors/u-1/outbox" ] do
                let! code, contentType, body = get rig path []
                Assert.Equal(HttpStatusCode.Gone, code)
                Assert.Equal("application/activity+json", contentType)
                let tombstone = json body
                Assert.Equal(actorUrl, jstr (at tombstone "id"))
                Assert.Equal("Tombstone", jstr (at tombstone "type"))
                Assert.Equal("Person", jstr (at tombstone "formerType"))
                Assert.Equal(deletedAt, jstr (at tombstone "deleted"))
                // Nothing of the actor is left in it
                Assert.Null(at tombstone "inbox")
                Assert.Null(at tombstone "publicKey")
                Assert.Null(at tombstone "name")

            let! _, _, groupBody = get rig "/ap/actors/u-3" []
            Assert.Equal("Group", jstr (at (json groupBody) "formerType"))

            // Not recreated: same rows, same key, nothing new
            let! rowsAfter = count rig "SELECT count(*) FROM local_actors"
            let! keysAfter = Db.scalar rig.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            Assert.Equal(rows, rowsAfter)
            Assert.Equal(string keysBefore, string keysAfter)
            let! name = Db.scalar rig.Db "SELECT name FROM local_actors WHERE app_user_id = 'u-1'"
            Assert.Equal("", string name)
            // An id that was never an actor stays a plain 404
            let! missing, _, _ = get rig "/ap/actors/nobody" []
            Assert.Equal(HttpStatusCode.NotFound, missing)
        }

    [<Fact>]
    member _.``003 FR-005 FR-010 a signed POST to the personal inbox of a deleted actor is 410 and nothing is processed``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            let! _ = deleteActor rig "u-1"
            use client = rig.Host.FederationClient()
            let follow = Signed.followBody rig.Stub "alice" "f1" (Signed.ourActor "u-1")

            use! response = Signed.postText client rig.Stub "alice" $"{rig.Host.FederationUrl}/ap/actors/u-1/inbox" follow
            Assert.Equal(HttpStatusCode.Gone, response.StatusCode)
            // The live actor next to it still takes the same request
            use! live = Signed.postText client rig.Stub "alice" $"{rig.Host.FederationUrl}/ap/actors/u-2/inbox" (Signed.followBody rig.Stub "alice" "f2" (Signed.ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, live.StatusCode)

            // An unsigned one is refused as before, not told anything about the actor
            use! unsigned = Signed.postRaw client $"{rig.Host.FederationUrl}/ap/actors/u-1/inbox" (Encoding.UTF8.GetBytes follow) []
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode)

            let! follows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            let followIri = Signed.followId rig.Stub "alice" "f1"
            let! processed = count rig $"SELECT count(*) FROM processed_activities WHERE activity_id = '{followIri}'"
            Assert.Equal(0L, follows)
            Assert.Equal(0L, processed)
        }

    [<Fact>]
    member _.``003 FR-007 a public post answers 410, a followers-only post 404 even to a former follower``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let! publicId = publish rig "u-1" "public" "<p>everyone</p>"
            let! privateId = publish rig "u-1" "followers" "<p>friends only</p>"
            let! publicActivity = createActivityId rig publicId
            let! privateActivity = createActivityId rig privateId

            // Before: the follower reads the private post
            use! before = signedGet rig rig.Stub "alice" (objectPath privateId)
            Assert.Equal(HttpStatusCode.OK, before.StatusCode)

            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)

            let! code, contentType, body = get rig (objectPath publicId) []
            Assert.Equal(HttpStatusCode.Gone, code)
            Assert.Equal("application/activity+json", contentType)
            Assert.Equal("Tombstone", jstr (at (json body) "type"))
            Assert.Equal("Note", jstr (at (json body) "formerType"))
            // The stored Create is gone with the object too, and its erased body is never served
            let! activityCode, _, activityBody = get rig $"/ap/activities/{publicActivity}" []
            Assert.Equal(HttpStatusCode.Gone, activityCode)
            Assert.DoesNotContain("everyone", activityBody)
            Assert.DoesNotContain("everyone", body)

            for path in [ objectPath privateId; $"/ap/activities/{privateActivity}" ] do
                let! unsigned, _, _ = get rig path []
                Assert.Equal(HttpStatusCode.NotFound, unsigned)
                use! signed = signedGet rig rig.Stub "alice" path
                Assert.Equal(HttpStatusCode.NotFound, signed.StatusCode)
        }

    [<Fact>]
    member _.``003 FR-006 WebFinger of the deleted handle is 404 while the app still answers, and resolves a new id that takes it``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! _ = deleteActor rig "u-1"
            let resource = "/.well-known/webfinger?resource=acct:alex@megafono.test"

            let! code, _, _ = get rig resource []
            Assert.Equal(HttpStatusCode.NotFound, code)
            rig.Time.Advance(TimeSpan.FromHours 2.0)
            let! again, _, _ = get rig resource []
            Assert.Equal(HttpStatusCode.NotFound, again)

            // The app gives the handle to a new user with another immutable id
            rig.App.Users.TryRemove "u-1" |> ignore
            rig.App.AddUser(FakeApp.user "u-9" "alex")
            let! resolved, _, body = get rig resource []
            Assert.Equal(HttpStatusCode.OK, resolved)
            Assert.Equal("https://megafono.test/ap/actors/u-9", jstr (at (json body) "links.0.href"))
            // The old id keeps answering 410
            let! old, _, _ = get rig "/ap/actors/u-1" []
            Assert.Equal(HttpStatusCode.Gone, old)
        }

    [<Fact>]
    member _.``003 FR-008 every internal call for a deleted actor answers 410 gone``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "pending" None
            let! objectId = publish rig "u-1" "public" "<p>before</p>"
            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)
            let followId = Guid.NewGuid()

            let calls =
                [ "publish", HttpMethod.Post, "/internal/actors/u-1/objects", Some((publishBody "public" "after").ToJsonString())
                  "profile refresh", HttpMethod.Put, "/internal/actors/u-1", None
                  "follow", HttpMethod.Post, "/internal/actors/u-1/following", Some """{"target":"@alice@example.test"}"""
                  "unfollow", HttpMethod.Delete, "/internal/actors/u-1/following?actor=https://example.test/users/x", None
                  "approve", HttpMethod.Post, $"/internal/actors/u-1/follow-requests/{followId}/approve", None
                  "reject", HttpMethod.Post, $"/internal/actors/u-1/follow-requests/{followId}/reject", None
                  "followers", HttpMethod.Get, "/internal/actors/u-1/followers", None
                  "following", HttpMethod.Get, "/internal/actors/u-1/following", None
                  "object update", HttpMethod.Put, $"/internal/objects/{objectId}", Some """{"object":{"type":"Note","content":"x"}}""" ]

            for name, verb, path, body in calls do
                let! code, text = sendInternal rig verb path body
                Assert.True((code = 410), $"{name} answered {code}: {text}")
                Assert.Equal("gone", jstr (at (json text) "error"))

            // Deleting an object of the deleted actor again keeps its repeat answer, and its deliveries stay readable
            let! again, _ = sendInternal rig HttpMethod.Delete $"/internal/objects/{objectId}" None
            Assert.Equal(202, again)
            let! deliveries, _ = sendInternal rig HttpMethod.Get $"/internal/objects/{objectId}/deliveries" None
            Assert.Equal(200, deliveries)

            // The live actor is not affected, and an unknown one is still 404
            let! live, _ = publishRaw rig "u-2" ((publishBody "public" "fine").ToJsonString()) None
            Assert.Equal(201, live)
            let! unknown, _ = sendInternal rig HttpMethod.Get "/internal/actors/nobody/followers" None
            Assert.Equal(404, unknown)
            let! unknownFollow, _ = sendInternal rig HttpMethod.Delete "/internal/actors/nobody/following?actor=x" None
            Assert.Equal(404, unknownFollow)
            // Nothing was created for the deleted actor
            let! objects = count rig "SELECT count(*) FROM objects WHERE local_actor = 'u-1'"
            Assert.Equal(1L, objects)
        }

    // ---- US3: stop speaking and erase (T009) -------------------------------------------------------------

    [<Fact>]
    member _.``003 FR-009 earlier pending deliveries are skipped with "actor deleted" and never sent after the Delete``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            rig.Stub.InboxMode <- StubRemote.Fail500
            let! objectId = publish rig "u-1" "public" "<p>stuck</p>"
            do! waitForFirstFailure rig
            Assert.Single(ofType "Create" rig.Stub) |> ignore

            rig.Stub.InboxMode <- StubRemote.Accept
            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)
            do! waitForPosts rig.Stub "Delete" 1

            // The retry is due, but the delivery ended with the deletion
            rig.Time.Advance(TimeSpan.FromMinutes 30.0)
            rig.Host.Wake()
            do! settle ()
            Assert.Single(ofType "Create" rig.Stub) |> ignore
            Assert.Single(ofType "Delete" rig.Stub) |> ignore

            let! report, text = sendInternal rig HttpMethod.Get $"/internal/objects/{objectId}/deliveries" None
            Assert.Equal(200, report)
            Assert.Equal("skipped", jstr (at (json text) "activities.0.deliveries.0.status"))
            Assert.Equal("actor deleted", jstr (at (json text) "activities.0.deliveries.0.lastError"))
            let! leased = count rig "SELECT count(*) FROM deliveries WHERE status = 'skipped' AND lease_token IS NULL AND lease_until IS NULL"
            Assert.Equal(1L, leased)
            // The skipped delivery did not tell the app anything
            Assert.Empty rig.App.Received
        }

    [<Fact>]
    member _.``003 FR-009 queued webhooks of the actor are dropped, the ones of other users stay``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let later = rig.Time.GetUtcNow().AddDays 1.0

            for user, n in [ "u-1", 1; "u-1", 2; "u-2", 1 ] do
                do!
                    Db.execute
                        rig.Db
                        $"""INSERT INTO app_events (id, local_actor, seq, event_type, payload, status, next_attempt_at)
                            VALUES ('evt-{user}-{n}', '{user}', {n}, 'follow.requested', '{{}}', 'pending', '{later.UtcDateTime:O}')"""

            let! before = count rig "SELECT count(*) FROM app_events WHERE local_actor = 'u-1'"
            Assert.Equal(2L, before)
            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)

            let! after = count rig "SELECT count(*) FROM app_events WHERE local_actor = 'u-1'"
            let! other = count rig "SELECT count(*) FROM app_events WHERE local_actor = 'u-2'"
            Assert.Equal(0L, after)
            Assert.Equal(1L, other)
        }

    [<Fact>]
    member _.``003 FR-009 no event is queued for a deleted actor, not even delivery.failed when its Delete fails for good``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            rig.Stub.InboxMode <- StubRemote.Fail500

            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)
            do! waitForFirstFailure rig
            // Past the 48 hour deadline the next failure ends the delivery
            rig.Time.Advance(TimeSpan.FromDays 3.0)
            rig.Host.Wake()

            do!
                waitUntilAsync "the Delete delivery gives up" (TimeSpan.FromSeconds 20.0) (fun () ->
                    task {
                        let! n = count rig "SELECT count(*) FROM deliveries WHERE status = 'failed'"
                        return n = 1L
                    })

            do! settle ()
            let! events = count rig "SELECT count(*) FROM app_events"
            Assert.Equal(0L, events)
            Assert.Empty rig.App.Received

            // And directly: the queue refuses to take an event for the deleted actor and keeps its sequence
            let! seqBefore = Db.scalar rig.Db "SELECT event_seq FROM local_actors WHERE app_user_id = 'u-1'"
            use! connection = rig.Db.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! queued = Outbox.enqueueEvent tx rig.Time "u-1" (Seed.followRequested "bob")
            do! tx.CommitAsync()
            Assert.Equal(None, queued)
            let! seqAfter = Db.scalar rig.Db "SELECT event_seq FROM local_actors WHERE app_user_id = 'u-1'"
            Assert.Equal(seqBefore, seqAfter)
            let! live = Seed.enqueueEvent rig.Db rig.Time "u-2" (Seed.followRequested "bob")
            Assert.NotEmpty live
        }

    [<Fact>]
    member _.``003 FR-010 a Follow of the deleted actor through the shared inbox is 404 and the app hears nothing``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            let! _ = deleteActor rig "u-1"
            use client = rig.Host.FederationClient()

            use! response = Signed.postText client rig.Stub "alice" $"{rig.Host.FederationUrl}/inbox" (Signed.followBody rig.Stub "alice" "f1" actorUrl)
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
            // The live actor takes the same Follow through the same inbox
            use! live = Signed.postText client rig.Stub "alice" $"{rig.Host.FederationUrl}/inbox" (Signed.followBody rig.Stub "alice" "f2" (Signed.ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, live.StatusCode)

            do! settle ()
            let! follows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            let! events = count rig "SELECT count(*) FROM app_events WHERE local_actor = 'u-1'"
            let! sent = count rig "SELECT count(*) FROM activities WHERE local_actor = 'u-1' AND activity_type = 'Accept'"
            Assert.Equal(0L, follows)
            Assert.Equal(0L, events)
            Assert.Equal(0L, sent)
        }

    [<Fact>]
    member _.``003 FR-011 follows, profile, post copies and activity bodies are erased in the deleting transaction``() : Task =
        task {
            let! rig = start pg [ "alice"; "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            do! outboundFollow rig rig.Stub "bob" "accepted"
            do! addFollower rig.Db "u-2" rig.Stub "alice" "accepted" None
            let! rootId = publish rig "u-1" "public" "<p>my root</p>"
            let! secretId = publish rig "u-1" "followers" "<p>my secret</p>"
            let! otherId = publish rig "u-2" "public" "<p>not mine</p>"

            // A reply of u-1 to its own root, and an edit (an Update activity)
            let reply = publishBody "public" "<p>my reply</p>"
            reply["object"]["inReplyTo"] <- JsonValue.Create $"https://megafono.test/ap/objects/{rootId}"
            let! replyStatus, replyText = publishRaw rig "u-1" (reply.ToJsonString()) None
            Assert.Equal(201, replyStatus)
            let replyId = jstr (at (json replyText) "id")
            let! putStatus, _ = sendInternal rig HttpMethod.Put $"/internal/objects/{secretId}" (Some """{"object":{"type":"Note","content":"<p>edited secret</p>"}}""")
            Assert.Equal(200, putStatus)

            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)

            let! follows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            let! otherFollows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2'"
            Assert.Equal(0L, follows)
            Assert.Equal(1L, otherFollows)

            let! profile =
                count
                    rig
                    "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-1' AND name = '' AND summary IS NULL AND icon_url IS NULL AND profile_url = '' AND deleted_at IS NOT NULL"

            Assert.Equal(1L, profile)
            let! kept = count rig "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-1' AND handle = 'alex' AND actor_type = 'Person'"
            Assert.Equal(1L, kept)

            let! live = count rig "SELECT count(*) FROM objects WHERE local_actor = 'u-1' AND (state <> 'deleted' OR deleted_at IS NULL)"
            let! documents = count rig "SELECT count(*) FROM objects WHERE local_actor = 'u-1' AND document::text <> '{}'"
            let! objects = count rig "SELECT count(*) FROM objects WHERE local_actor = 'u-1'"
            Assert.Equal(0L, live)
            Assert.Equal(0L, documents)
            Assert.Equal(3L, objects)

            let! bodies = count rig "SELECT count(*) FROM activities WHERE local_actor = 'u-1' AND activity_type IN ('Create', 'Update') AND length(body) > 0"
            let! erased = count rig "SELECT count(*) FROM activities WHERE local_actor = 'u-1' AND activity_type IN ('Create', 'Update') AND length(body) = 0"
            Assert.Equal(0L, bodies)
            Assert.Equal(4L, erased)

            // The reply left the thread; the other user's post and activities are untouched
            let! repliesGone = count rig $"SELECT count(*) FROM replies WHERE local_object = '{replyId}' AND state = 'deleted' AND deleted_at IS NOT NULL"
            Assert.Equal(1L, repliesGone)
            let! otherDocument = count rig $"SELECT count(*) FROM objects WHERE id = '{otherId}' AND state = 'live' AND document::text <> '{{}}'"
            let! otherBodies = count rig "SELECT count(*) FROM activities WHERE local_actor = 'u-2' AND length(body) > 0"
            Assert.Equal(1L, otherDocument)
            Assert.Equal(1L, otherBodies)
            let! objectCode, _, _ = get rig (objectPath otherId) []
            Assert.Equal(HttpStatusCode.OK, objectCode)
            let! replies, _, repliesBody = get rig $"{objectPath rootId}/replies" []
            Assert.Equal(HttpStatusCode.Gone, replies)
            Assert.DoesNotContain("my reply", repliesBody)
        }

    [<Fact>]
    member _.``003 SC-004 keys stay while a Delete delivery is pending and are cleared once every delivery has ended``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let cache = rig.Host.Services.GetRequiredService<Deliveries.KeyCache>()
            rig.Stub.InboxMode <- StubRemote.Fail500

            let! status, _ = deleteActor rig "u-1"
            Assert.Equal(202, status)
            do! waitForFirstFailure rig
            // The worker signed the Delete with the actor's key and cached it
            Assert.True((cache.TryGet "u-1").IsSome)

            // A pending delivery needs the key
            let! early = Housekeeping.clearDeletedActorKeys rig.Db
            Assert.Empty early
            let! kept = count rig "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-1' AND enc_private_key IS NOT NULL AND public_key_pem IS NOT NULL"
            Assert.Equal(1L, kept)

            // The server comes back: the Delete is delivered, the deliveries have all ended
            rig.Stub.InboxMode <- StubRemote.Accept
            rig.Time.Advance(TimeSpan.FromMinutes 2.0)
            rig.Host.Wake()
            do! waitForPosts rig.Stub "Delete" 2

            do!
                waitUntilAsync "the Delete is delivered" (TimeSpan.FromSeconds 20.0) (fun () ->
                    task {
                        let! n = count rig "SELECT count(*) FROM deliveries WHERE status = 'delivered'"
                        return n = 1L
                    })

            let! cleared = Housekeeping.clearDeletedActorKeys rig.Db
            Assert.Equal<string list>([ "u-1" ], cleared)
            let! none = count rig "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-1' AND enc_private_key IS NULL AND public_key_pem IS NULL"
            Assert.Equal(1L, none)
            // The other actor keeps its keys, and a second run has nothing to do
            let! others = count rig "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-2' AND enc_private_key IS NOT NULL AND public_key_pem IS NOT NULL"
            Assert.Equal(1L, others)
            let! again = Housekeeping.clearDeletedActorKeys rig.Db
            Assert.Empty again

            // Even though the app still answers for the user, nothing creates a key for the id again
            let! code, _, body = get rig "/ap/actors/u-1" []
            Assert.Equal(HttpStatusCode.Gone, code)
            Assert.Equal("Tombstone", jstr (at (json body) "type"))
            let! recreated = count rig "SELECT count(*) FROM local_actors WHERE app_user_id = 'u-1' AND enc_private_key IS NULL AND public_key_pem IS NULL AND deleted_at IS NOT NULL"
            Assert.Equal(1L, recreated)
        }

    [<Fact>]
    member _.``003 SC-004 a deleted actor without followers has its keys cleared at once``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! _ = deleteActor rig "u-1"
            let! cleared = Housekeeping.clearDeletedActorKeys rig.Db
            Assert.Equal<string list>([ "u-1" ], cleared)
        }

    [<Fact>]
    member _.``003 R-32 the key cache drops and disposes the keys it is told to evict``() : Task =
        task {
            let cache = Deliveries.KeyCache()
            let key = new TrackedRsa()
            Assert.Same(key, cache.GetOrAdd("u-1", key))
            Assert.True((cache.TryGet "u-1").IsSome)
            cache.Evict [ "u-1"; "never-cached" ]
            Assert.True((cache.TryGet "u-1").IsNone)
            Assert.True key.Disposed
            return ()
        }

    // ---- FR-012: serialization with other writes ---------------------------------------------------------

    [<Fact>]
    member _.``003 FR-012 the delete waits for a write holding the actor, and the write after it sees a deleted actor``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig

            // A write in flight (what publish and follow do first) holds the live actor
            use! connection = rig.Db.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! live = Outbox.lockLiveActor tx "u-1"
            Assert.True live

            let deleting = deleteActor rig "u-1"
            do! Task.Delay 500
            Assert.False(deleting.IsCompleted, "the delete must wait for the write that holds the actor")
            do! tx.CommitAsync()
            let! status, _ = deleting
            Assert.Equal(202, status)

            // After the delete the guard refuses, so nothing is created for the actor
            use! connection2 = rig.Db.OpenConnectionAsync()
            use! tx2 = connection2.BeginTransactionAsync()
            let! after = Outbox.lockLiveActor tx2 "u-1"
            let! deleted = Outbox.isDeletedActor tx2 "u-1"
            let! unknown = Outbox.isDeletedActor tx2 "nobody"
            Assert.False after
            Assert.True deleted
            Assert.False unknown
            do! tx2.RollbackAsync()
        }
