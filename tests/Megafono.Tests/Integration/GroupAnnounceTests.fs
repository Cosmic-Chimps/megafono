/// A Group actor announces the replies in its threads (specs/006-group-announce, User Story 1, FR-001 to FR-003,
/// FR-012, R-62 to R-64): the outbound side. The stub plays both the Group's followers' server and the replier's
/// own server.
module Megafono.Tests.Integration.GroupAnnounceTests

open System
open System.Net
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
let private objectIri (id: string) = Ids.objectId domain (Guid.Parse id)
let private followersOf (user: string) = Ids.actorFollowers domain user
let private jsonStrings (node: JsonNode) = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq

let private inboxUrl (rig: Rig) = rig.Host.FederationUrl + "/inbox"

let private send (rig: Rig) (name: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name (inboxUrl rig) body
        return response.StatusCode
    }

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

let private waitForPosts (rig: Rig) (path: string) (n: int) =
    waitUntil $"{n} post(s) at {path}" (TimeSpan.FromSeconds 15.0) (fun () -> (rig.Stub.Posts |> List.filter (fun p -> p.Path = path)).Length >= n)

/// The parsed bodies posted at `path`, of this activity `type`
let private activitiesAt (rig: Rig) (path: string) (activityType: string) : JsonNode list =
    rig.Stub.Posts
    |> List.filter (fun p -> p.Path = path)
    |> List.map (fun p -> json (Encoding.UTF8.GetString p.Body))
    |> List.filter (fun a -> jstr (at a "type") = activityType)

let private put (rig: Rig) (id: string) (body: string) = sendInternal rig HttpMethod.Put $"/internal/objects/{id}" (Some body)
let private delete (rig: Rig) (id: string) = sendInternal rig HttpMethod.Delete $"/internal/objects/{id}" None

let private replyBodyText (audience: string) (inReplyTo: string) (content: string) : string =
    let o = publishBody audience content
    o["object"]["inReplyTo"] <- JsonValue.Create inReplyTo
    o.ToJsonString()

let private localReply (rig: Rig) (user: string) (audience: string) (inReplyTo: string) : Task<int * string> =
    publishRaw rig user (replyBodyText audience inReplyTo "<p>a comment</p>") None

/// Adds `name` as a Group to the fake app and makes its row exist (or refreshes it), returning the PUT's status
let private putActor (rig: Rig) (id: string) : Task<int> =
    task {
        let! status, _ = sendInternal rig HttpMethod.Put $"/internal/actors/{id}" None
        return status
    }

/// alex (u-1), bea (u-2), bob and carl on the stub, and a Group `g-1` that announces, carl following it
let private start (pg: PostgresFixture) : Task<Rig> =
    task {
        let! rig = Publishing.start pg [ "bob"; "carl" ]
        rig.App.AddUser { FakeApp.user "g-1" "series" with Type = "Group"; AnnouncesReplies = Some true }
        let! status = putActor rig "g-1"
        Assert.Equal(202, status)
        do! addFollower rig.Db "g-1" rig.Stub "carl" "accepted" None
        return rig
    }

let private groupFollowers = followersOf "g-1"

type GroupAnnounceTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-002 a public reply to an announcing Group's post is boosted to its followers and the author``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let bob = rig.Stub.ActorId "bob"

            let note = Signed.note rig.Stub "bob" "1" (objectIri root)
            let! status = send rig "bob" (createNote rig.Stub "bob" "1" note)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1

            // carl already got the root's own Create (he follows the Group); the reply's Announce is the second post
            do! waitForPosts rig "/users/carl/inbox" 2
            do! waitForPosts rig "/users/bob/inbox" 1

            let carlAnnounce = (activitiesAt rig "/users/carl/inbox" "Announce") |> List.exactlyOne
            let bobAnnounce = (activitiesAt rig "/users/bob/inbox" "Announce") |> List.exactlyOne
            Assert.Equal(jstr (at carlAnnounce "id"), jstr (at bobAnnounce "id"))
            Assert.Equal(Ids.actorId domain "g-1", jstr (at carlAnnounce "actor"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at carlAnnounce "object"))
            Assert.Equal<string list>([ publicIri ], jsonStrings (at carlAnnounce "to"))
            Assert.Equal<string list>([ groupFollowers; bob ], jsonStrings (at carlAnnounce "cc"))

            let! activityCount =
                Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Announce' AND local_actor = 'g-1' AND object IS NULL AND audience = 'public'"

            Assert.Equal(1L, activityCount)

            let bobNoteId = noteId rig.Stub "bob" "1"
            let! announceSet = Db.count rig.Db $"SELECT count(*) FROM replies WHERE id = '{bobNoteId}' AND announce_activity IS NOT NULL"
            Assert.Equal(1L, announceSet)
        }

    [<Fact>]
    member _.``FR-002 an unlisted reply is addressed to the followers, cc Public and the author``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let bob = rig.Stub.ActorId "bob"
            let bobFollowers = bob + "/followers"

            let note = Signed.note rig.Stub "bob" "1" (objectIri root)
            Signed.setAddressing note [ bobFollowers ] [ publicIri ]
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" note)
            do! waitForEvents rig "reply.received" 1
            // carl already got the root's own Create (he follows the Group); the reply's Announce is the second post
            do! waitForPosts rig "/users/carl/inbox" 2

            let announce = (activitiesAt rig "/users/carl/inbox" "Announce") |> List.exactlyOne
            Assert.Equal<string list>([ groupFollowers ], jsonStrings (at announce "to"))
            Assert.Equal<string list>([ publicIri; bob ], jsonStrings (at announce "cc"))
        }

    [<Fact>]
    member _.``FR-002 followers-only and direct replies are not announced``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let bobFollowers = rig.Stub.ActorId "bob" + "/followers"

            let followersNote = Signed.note rig.Stub "bob" "1" (objectIri root)
            Signed.setAddressing followersNote [ bobFollowers ] []
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" followersNote)
            do! waitForEvents rig "reply.received" 1

            let directNote = Signed.note rig.Stub "bob" "2" (objectIri root)
            Signed.setAddressing directNote [ Ids.actorId domain "g-1" ] []
            let! _ = send rig "bob" (createNote rig.Stub "bob" "2" directNote)
            do! waitForEvents rig "reply.received" 2

            do! Task.Delay 700
            let! announces = Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Announce'"
            Assert.Equal(0L, announces)
        }

    [<Fact>]
    member _.``FR-001 nothing is announced for a Group without the flag, or for a Person's thread``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            rig.App.AddUser { FakeApp.user "g-2" "crew" with Type = "Group" }
            let! status = putActor rig "g-2"
            Assert.Equal(202, status)

            let! rootG2 = publish rig "g-2" "public" "<p>hi</p>"
            let noteG2 = Signed.note rig.Stub "bob" "10" (objectIri rootG2)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "10" noteG2)
            do! waitForEvents rig "reply.received" 1

            let! rootAlex = publish rig "u-1" "public" "<p>alex's post</p>"
            let noteAlex = Signed.note rig.Stub "bob" "11" (objectIri rootAlex)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "11" noteAlex)
            do! waitForEvents rig "reply.received" 2

            do! Task.Delay 700
            let! announces = Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Announce'"
            Assert.Equal(0L, announces)
        }

    [<Fact>]
    member _.``FR-002 the Group's own reply in its own thread is not announced``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let! status, _ = localReply rig "g-1" "public" (objectIri root)
            Assert.Equal(201, status)
            do! Task.Delay 700
            let! announces = Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Announce'"
            Assert.Equal(0L, announces)
        }

    [<Fact>]
    member _.``FR-003 an edit of an announced reply gets Announce{Update}; its deletion gets Undo{Announce}``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let bob = rig.Stub.ActorId "bob"
            let note = Signed.note rig.Stub "bob" "1" (objectIri root)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" note)
            do! waitForEvents rig "reply.received" 1
            // carl already got the root's own Create (he follows the Group); the reply's Announce is the second post
            do! waitForPosts rig "/users/carl/inbox" 2
            let firstAnnounce = (activitiesAt rig "/users/carl/inbox" "Announce") |> List.exactlyOne
            let firstAnnounceId = jstr (at firstAnnounce "id")

            note["content"] <- JsonValue.Create "<p>edited</p>"
            let! editStatus = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" note)
            Assert.Equal(HttpStatusCode.Accepted, editStatus)
            do! waitForEvents rig "reply.updated" 1
            do! waitForPosts rig "/users/carl/inbox" 3
            do! waitForPosts rig "/users/bob/inbox" 2

            let updateAnnounce =
                (activitiesAt rig "/users/carl/inbox" "Announce") |> List.filter (fun a -> jstr (at a "id") <> firstAnnounceId) |> List.exactlyOne

            Assert.Equal("Update", jstr (at updateAnnounce "object.type"))
            Assert.Equal(bob, jstr (at updateAnnounce "object.actor"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at updateAnnounce "object.object"))
            Assert.Equal($"""{noteId rig.Stub "bob" "1"}#updates/e1""", jstr (at updateAnnounce "object.id"))
            // the same recipients as the first announce
            Assert.Equal<string list>(jsonStrings (at firstAnnounce "cc"), jsonStrings (at updateAnnounce "cc"))

            let! deleteStatus = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            Assert.Equal(HttpStatusCode.Accepted, deleteStatus)
            do! waitForEvents rig "object.deleted" 1
            do! waitForPosts rig "/users/carl/inbox" 4
            do! waitForPosts rig "/users/bob/inbox" 3

            let undo = (activitiesAt rig "/users/carl/inbox" "Undo") |> List.exactlyOne
            Assert.Equal(firstAnnounceId, jstr (at undo "object.id"))
            Assert.Equal("Announce", jstr (at undo "object.type"))
            Assert.Equal(Ids.actorId domain "g-1", jstr (at undo "object.actor"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at undo "object.object"))
        }

    [<Fact>]
    member _.``FR-003 a reply recorded while the flag was off gets no announce even after the flag turns on``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            rig.App.AddUser { FakeApp.user "g-3" "newcrew" with Type = "Group" }
            let! status = putActor rig "g-3"
            Assert.Equal(202, status)
            do! addFollower rig.Db "g-3" rig.Stub "carl" "accepted" None

            let! root = publish rig "g-3" "public" "<p>hi</p>"
            let note = Signed.note rig.Stub "bob" "20" (objectIri root)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "20" note)
            do! waitForEvents rig "reply.received" 1

            rig.App.AddUser { FakeApp.user "g-3" "newcrew" with Type = "Group"; AnnouncesReplies = Some true }
            let! status = putActor rig "g-3"
            Assert.Equal(202, status)

            note["content"] <- JsonValue.Create "<p>edited</p>"
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "20" "1" note)
            do! waitForEvents rig "reply.updated" 1

            do! Task.Delay 700
            let! announces = Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Announce' AND local_actor = 'g-3'"
            Assert.Equal(0L, announces)
        }

    [<Fact>]
    member _.``R-64 a local reply into the Group's thread is announced too, with its edit and deletion``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let! status, text = localReply rig "u-1" "public" (objectIri root)
            Assert.Equal(201, status)
            let replyId = jstr (at (json text) "id")
            let replyIri = objectIri replyId

            // carl already got the root's own Create (he follows the Group); the reply's Announce is the second post
            do! waitForPosts rig "/users/carl/inbox" 2
            Assert.Empty(rig.Stub.Posts |> List.filter (fun p -> p.Path = "/users/bob/inbox"))
            let announce = (activitiesAt rig "/users/carl/inbox" "Announce") |> List.exactlyOne
            Assert.Equal(replyIri, jstr (at announce "object"))
            Assert.Equal(Ids.actorId domain "g-1", jstr (at announce "actor"))

            let! announceSet = Db.count rig.Db $"SELECT count(*) FROM replies WHERE id = '{replyIri}' AND announce_activity IS NOT NULL"
            Assert.Equal(1L, announceSet)

            let! editStatus, _ = put rig replyId """{"object":{"type":"Note","content":"<p>edited</p>"}}"""
            Assert.Equal(200, editStatus)
            do! waitForPosts rig "/users/carl/inbox" 3

            let updateAnnounce =
                (activitiesAt rig "/users/carl/inbox" "Announce")
                |> List.filter (fun a -> jstr (at a "id") <> jstr (at announce "id"))
                |> List.exactlyOne

            Assert.Equal("Update", jstr (at updateAnnounce "object.type"))
            Assert.Equal(Ids.actorId domain "u-1", jstr (at updateAnnounce "object.actor"))
            Assert.Equal(replyIri, jstr (at updateAnnounce "object.object"))

            let! deleteStatus, _ = delete rig replyId
            Assert.Equal(202, deleteStatus)
            do! waitForPosts rig "/users/carl/inbox" 4

            let undo = (activitiesAt rig "/users/carl/inbox" "Undo") |> List.exactlyOne
            Assert.Equal(jstr (at announce "id"), jstr (at undo "object.id"))
            Assert.Equal("Announce", jstr (at undo "object.type"))
            Assert.Equal(replyIri, jstr (at undo "object.object"))
        }

    [<Fact>]
    member _.``FR-012 a follower the Group's user blocks gets no announce delivery``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            // Deliveries are filtered by the inbox's actual host (every stub binds to 127.0.0.1, which a domain
            // block would also cover carl and bob with): an actor block isolates dora alone, as 004 intends. Blocking
            // a follower also ends the follow and sends it a Block notification (004), which legitimately reaches
            // dora's inbox: the assertion below is specifically that no Announce does.
            do! addFollower rig.Db "g-1" rig.Stub "dora" "accepted" None
            let dora = rig.Stub.ActorId "dora"

            let! blockStatus, _ = sendInternal rig HttpMethod.Post "/internal/actors/g-1/blocks" (Some(JsonSerializer.Serialize(dict [ "actor", dora ])))
            Assert.Equal(201, blockStatus)

            let! root = publish rig "g-1" "public" "<p>Episode 5</p>"
            let note = Signed.note rig.Stub "bob" "1" (objectIri root)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" note)
            do! waitForEvents rig "reply.received" 1
            // carl already got the root's own Create (he follows the Group); the reply's Announce is the second post
            do! waitForPosts rig "/users/carl/inbox" 2

            do! Task.Delay 700
            let doraInbox = rig.Stub.InboxUrl "dora"

            let! announcesToDora =
                Db.count rig.Db $"SELECT count(*) FROM deliveries d JOIN activities a ON a.id = d.activity WHERE a.activity_type = 'Announce' AND d.inbox_url = '{doraInbox}'"

            Assert.Equal(0L, announcesToDora)
        }
