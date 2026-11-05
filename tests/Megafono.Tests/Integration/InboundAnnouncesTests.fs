/// A followed Group's announces reach the app (specs/006-group-announce, User Stories 2 and 3, FR-004 to FR-009,
/// R-65 to R-67, SC-002, SC-003): the inbound side. The stub plays a remote `Group` (`series`) that alex (u-1) and bea
/// (u-2) follow, and the author (`bob`) of the announced notes. The bodies are made with the sender's own builders
/// (`ActivityStreams.announce`, `announceUpdate`, `undoAnnounce`) with the ids moved onto the stub's host, so what
/// Megáfono sends is what its own inbox understands.
module Megafono.Tests.Integration.InboundAnnouncesTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Domain
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private domain = "megafono.test"
let private inboxUrl (rig: Rig) = rig.Host.FederationUrl + "/inbox"

let private sendAs (rig: Rig) (name: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name (inboxUrl rig) body
        return response.StatusCode
    }

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private recipients (rig: Rig) (eventType: string) : string list =
    events rig eventType |> List.map (fun e -> jstr (at e.Json "user.id")) |> List.sort

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private postEvents (rig: Rig) = rig.App.Received |> List.filter (fun e -> e.Type.StartsWith "post.")

let private feedRows (rig: Rig) = Db.count rig.Db "SELECT count(*) FROM feed_posts"

let private statusGets (stub: StubRemote.StubRemote) = stub.Gets |> List.filter (fun g -> g.Path.Contains "/statuses/")

let private internalJson (fields: (string * string) list) : string = JsonSerializer.Serialize(dict fields)

let private groupName = "series"
let private groupId (rig: Rig) = rig.Stub.ActorId groupName

/// alex (u-1) and bea (u-2) follow the stub's Group `series` (accepted); bob and carl are people on the same stub
let private start (pg: PostgresFixture) : Task<Rig> =
    task {
        let! rig = Publishing.start pg [ groupName; "bob"; "carl" ]
        rig.Stub.SetActorType(groupName, "Group")

        for user in [ "u-1"; "u-2" ] do
            do! addFollowingTyped rig.Db user rig.Stub groupName "accepted" "Group"

        return rig
    }

// ---- the sender's own builders, with the stub's ids ------------------------------------------------------

let private announceId (rig: Rig) (suffix: string) = $"{groupId rig}/announces/{suffix}"

let private addressingFor (rig: Rig) (authorId: string) =
    ActivityStreams.announceAddressing (groupId rig + "/followers") (Some authorId) Audience.ReceivedPublic

/// The builder's document with the Group's identity on the stub in place of an instance-local actor id
let private asStubGroup (rig: Rig) (doc: JsonNode) : string =
    doc["actor"] <- JsonValue.Create(groupId rig)

    match doc["object"] with
    | :? JsonObject as inner when jstr inner["type"] = "Announce" -> inner["actor"] <- JsonValue.Create(groupId rig)
    | _ -> ()

    doc.ToJsonString()

/// `Announce` of `target`, by the Group, by the sender's builder
let private announceOf (rig: Rig) (authorId: string) (suffix: string) (target: string) : string =
    ActivityStreams.announce domain groupName (announceId rig suffix) DateTimeOffset.UtcNow (addressingFor rig authorId) target
    |> asStubGroup rig

/// `Announce{Update}` of `target`; `suffix` makes the edit's id and the announce's id unique
let private announceUpdateOf (rig: Rig) (authorId: string) (suffix: string) (target: string) : string =
    ActivityStreams.announceUpdate
        domain
        groupName
        (announceId rig suffix)
        DateTimeOffset.UtcNow
        (addressingFor rig authorId)
        $"{target}#updates/{suffix}"
        authorId
        target
    |> asStubGroup rig

/// `Undo` of the Group's `Announce` made with `announceSuffix`
let private undoOf (rig: Rig) (authorId: string) (suffix: string) (announceSuffix: string) (target: string) : string =
    ActivityStreams.undoAnnounce
        domain
        groupName
        $"{groupId rig}/undos/{suffix}"
        DateTimeOffset.UtcNow
        (addressingFor rig authorId)
        (announceId rig announceSuffix)
        target
    |> asStubGroup rig

/// A public post of bob on the stub, served from its origin, and its id
let private serveNote (rig: Rig) (suffix: string) : JsonObject * string =
    let note = feedNote rig.Stub "bob" suffix
    rig.Stub.ServeNote("bob", suffix, note)
    note, noteId rig.Stub "bob" suffix

/// The post as an edit reads: new content and the edit's own `updated` time
let private serveEdit (rig: Rig) (suffix: string) (content: string) (updated: string) : unit =
    let note = feedNote rig.Stub "bob" suffix
    note["content"] <- JsonValue.Create content
    note["updated"] <- JsonValue.Create updated
    rig.Stub.ServeNote("bob", suffix, note)

let private liveRows (rig: Rig) (id: string) =
    Db.count rig.Db $"SELECT count(*) FROM feed_posts WHERE id = '{id}' AND state = 'live'"

let private rootIri (objectId: string) = Ids.objectId domain (Guid.Parse objectId)

type InboundAnnouncesTests(pg: PostgresFixture) =

    // ---- User Story 2: the announce reaches the app ----------------------------------------------------------

    [<Fact>]
    member _.``FR-004 FR-009 SC-002 an Announce of a post reaches each follower of the Group once, the post read from its origin``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig

            // alex published with the namespace once: that is how Megáfono knows it is the app's
            let body = publishBody "public" "<p>Episode 6</p>"
            body["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example.com/ns#","prefix":"app","data":{"episode":6}}"""
            let! created, _ = publishRaw rig "u-1" (body.ToJsonString()) None
            Assert.Equal(201, created)

            let note, target = serveNote rig "1"
            note["@context"] <- JsonNode.Parse """["https://www.w3.org/ns/activitystreams",{"x":"https://app.example.com/ns#"}]"""
            note["x:rating"] <- JsonNode.Parse """{"stars":5}"""
            note["content"] <- JsonValue.Create "<p>that ending!</p>"
            note["summary"] <- JsonValue.Create "Spoilers: S2 E1"
            note["sensitive"] <- JsonValue.Create true
            note["attachment"] <- JsonNode.Parse """[{"type":"Image","mediaType":"image/png","url":"https://files.example/a.png","name":"alt text"}]"""
            note["inReplyTo"] <- JsonValue.Create $"{rig.Stub.BaseUrl}/users/series/statuses/1"
            rig.Stub.ServeNote("bob", "1", note)

            let! status = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" target)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 2
            do! settle ()

            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")

            for e in events rig "post.received" do
                Assert.True e.SignatureValid
                // the author in `actor`, the Group in `announcedBy`
                Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
                Assert.Equal($"@bob@{rig.Stub.Authority}", jstr (at e.Json "actor.handle"))
                Assert.Equal(groupId rig, jstr (at e.Json "announcedBy.id"))
                Assert.Equal($"@series@{rig.Stub.Authority}", jstr (at e.Json "announcedBy.handle"))
                Assert.Equal("series (stub)", jstr (at e.Json "announcedBy.name"))
                // every field of 005's object
                Assert.Equal(target, jstr (at e.Json "object.id"))
                Assert.Equal($"{rig.Stub.BaseUrl}/@bob/1", jstr (at e.Json "object.url"))
                Assert.Equal("Note", jstr (at e.Json "object.type"))
                Assert.Equal("<p>that ending!</p>", jstr (at e.Json "object.content"))
                Assert.Equal("Spoilers: S2 E1", jstr (at e.Json "object.summary"))
                Assert.True(boolOf (at e.Json "object.sensitive"))
                Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))
                Assert.Null(at e.Json "object.updated")
                Assert.Equal("public", jstr (at e.Json "object.audience"))
                Assert.Equal($"{rig.Stub.BaseUrl}/users/series/statuses/1", jstr (at e.Json "object.inReplyTo"))
                Assert.Equal("Image", jstr (at e.Json "object.attachment.0.type"))
                // FR-009: the app's namespace comes through
                Assert.Equal("https://app.example.com/ns#", jstr (at e.Json "extensions.namespace"))
                Assert.Equal("x", jstr (at e.Json "extensions.prefix"))
                Assert.Equal(5, intOf (at e.Json "extensions.data.rating.stars"))

            // the rows: the Group brought it, bob wrote it
            let group = groupId rig
            let author = rig.Stub.ActorId "bob"

            let! rows =
                Db.count
                    rig.Db
                    $"SELECT count(*) FROM feed_posts WHERE id = '{target}' AND announced_by = '{group}' AND remote_actor = '{author}' AND state = 'live'"

            Assert.Equal(2L, rows)

            // the note was read once from its origin, signed; bob's actor once
            let fetched = statusGets rig.Stub
            Assert.Single fetched |> ignore
            Assert.True(fetched.Head.Headers |> List.exists (fun (n, _) -> n.ToLowerInvariant() = "signature"))
            Assert.Single(rig.Stub.Gets |> List.filter (fun g -> g.Path = "/users/bob")) |> ignore
        }

    [<Fact>]
    member _.``FR-004 an embedded object in the Announce is not trusted: the content is the origin's``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let note, target = serveNote rig "1"
            note["content"] <- JsonValue.Create "<p>from the origin</p>"
            rig.Stub.ServeNote("bob", "1", note)

            // Lemmy's shape too: an Announce of a Create whose object is embedded; the embedded copy lies
            let forged = feedNote rig.Stub "bob" "1"
            forged["content"] <- JsonValue.Create "<p>forged</p>"
            let create = JsonObject()
            create["id"] <- JsonValue.Create $"{target}/activity"
            create["type"] <- JsonValue.Create "Create"
            create["actor"] <- JsonValue.Create(rig.Stub.ActorId "bob")
            create["object"] <- forged
            let doc = JsonNode.Parse(announceOf rig (rig.Stub.ActorId "bob") "a1" target)
            doc["object"] <- create

            let! status = sendAs rig groupName (doc.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 2

            for e in events rig "post.received" do
                Assert.Equal("<p>from the origin</p>", jstr (at e.Json "object.content"))
        }

    [<Fact>]
    member _.``FR-006 SC-005 the same Announce twice, or another Announce id of the same post, is one event per user``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let body = announceOf rig bob "a1" target

            let! first = sendAs rig groupName body
            Assert.Equal(HttpStatusCode.Accepted, first)
            do! waitForEvents rig "post.received" 2
            let fetchedOnce = (statusGets rig.Stub).Length

            // the exact repeat is not even fetched again
            let! second = sendAs rig groupName body
            Assert.Equal(HttpStatusCode.Accepted, second)
            do! settle ()
            Assert.Equal(fetchedOnce, (statusGets rig.Stub).Length)

            // another activity id of the same object: whatever it costs, nobody is told twice
            let! third = sendAs rig groupName (announceOf rig bob "a2" target)
            Assert.Equal(HttpStatusCode.Accepted, third)
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")
            let! rows = feedRows rig
            Assert.Equal(2L, rows)
        }

    [<Fact>]
    member _.``FR-007 R-67 an Announce of an Update gives post.updated with the new content, the stored audience and published, once per edit``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! _ = sendAs rig groupName (announceOf rig bob "a1" target)
            do! waitForEvents rig "post.received" 2

            // the origin now serves the edit, with another `published`: the app is told the one the post arrived with
            serveEdit rig "1" "<p>v2</p>" "2026-10-12T18:30:00Z"
            let edit = feedNote rig.Stub "bob" "1"
            edit["content"] <- JsonValue.Create "<p>v2</p>"
            edit["updated"] <- JsonValue.Create "2026-10-12T18:30:00Z"
            edit["published"] <- JsonValue.Create "2026-10-13T00:00:00Z"
            rig.Stub.ServeNote("bob", "1", edit)

            let update = announceUpdateOf rig bob "e1" target
            let! status = sendAs rig groupName update
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.updated" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.updated")

            for e in events rig "post.updated" do
                Assert.True e.SignatureValid
                Assert.Equal(bob, jstr (at e.Json "actor.id"))
                Assert.Equal(groupId rig, jstr (at e.Json "announcedBy.id"))
                Assert.Equal(target, jstr (at e.Json "object.id"))
                Assert.Equal("<p>v2</p>", jstr (at e.Json "object.content"))
                Assert.Equal("2026-10-12T18:30:00Z", jstr (at e.Json "object.updated"))
                Assert.Equal("public", jstr (at e.Json "object.audience"))
                Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))

            // the same Announce twice is one edit, and costs no second fetch
            let fetched = (statusGets rig.Stub).Length
            let! _ = sendAs rig groupName update
            do! settle ()
            Assert.Equal(2, (events rig "post.updated").Length)
            Assert.Equal(fetched, (statusGets rig.Stub).Length)
        }

    [<Fact>]
    member _.``FR-007 R-67 a user who also follows the author hears the edit from the author, not from the Group``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! _ = sendAs rig groupName (announceOf rig bob "a1" target)
            do! waitForEvents rig "post.received" 2

            // bea follows bob too
            do! addFollowing rig.Db "u-2" rig.Stub "bob" "accepted"
            serveEdit rig "1" "<p>v2</p>" "2026-10-12T18:30:00Z"
            let! _ = sendAs rig groupName (announceUpdateOf rig bob "e1" target)
            do! waitForEvents rig "post.updated" 1
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "post.updated")

            // and bob's own Update reaches bea, who follows him, through 005's path, matching the announced row by its author
            serveEdit rig "1" "<p>v3</p>" "2026-10-12T19:00:00Z"
            let own = updateNote rig.Stub "bob" "1" "own1" (feedNote rig.Stub "bob" "1")
            let ownNote = JsonNode.Parse own
            ownNote["object"]["content"] <- JsonValue.Create "<p>v3</p>"
            ownNote["object"]["updated"] <- JsonValue.Create "2026-10-12T19:00:00Z"
            let! _ = sendAs rig "bob" (ownNote.ToJsonString())
            do! waitForEvents rig "post.updated" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.updated")
        }

    [<Fact>]
    member _.``FR-007 an Undo of the Announce gives post.deleted with nothing fetched, and later edits or repeats change nothing``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! _ = sendAs rig groupName (announceOf rig bob "a1" target)
            do! waitForEvents rig "post.received" 2
            let fetched = rig.Stub.Gets.Length

            let undo = undoOf rig bob "u1" "a1" target
            let! status = sendAs rig groupName undo
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.deleted" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.deleted")

            for e in events rig "post.deleted" do
                Assert.True e.SignatureValid
                Assert.Equal(target, jstr (at e.Json "object.id"))
                Assert.Equal(1, (at e.Json "object").AsObject().Count)
                Assert.Equal(bob, jstr (at e.Json "actor.id"))
                Assert.Equal(groupId rig, jstr (at e.Json "announcedBy.id"))

            Assert.Equal(fetched, rig.Stub.Gets.Length)
            let! live = liveRows rig target
            Assert.Equal(0L, live)

            // a later edit's announce, the same Undo again, and another Undo: nothing, and nothing fetched
            let! _ = sendAs rig groupName (announceUpdateOf rig bob "e1" target)
            let! _ = sendAs rig groupName undo
            let! _ = sendAs rig groupName (undoOf rig bob "u2" "a1" target)
            do! settle ()
            Assert.Empty(events rig "post.updated")
            Assert.Equal(2, (events rig "post.deleted").Length)
            Assert.Equal(fetched, rig.Stub.Gets.Length)
        }

    [<Fact>]
    member _.``FR-007 an Announce of an Update of a post never announced is dropped without a fetch``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let! status = sendAs rig groupName (announceUpdateOf rig (rig.Stub.ActorId "bob") "e1" target)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(statusGets rig.Stub)
        }

    [<Fact>]
    member _.``FR-005 R-53 a post replying into the app's own thread is not a feed post for the thread's author``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let reply = note rig.Stub "bob" "1" (rootIri root)
            rig.Stub.ServeNote("bob", "1", reply)

            let! status = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" (noteId rig.Stub "bob" "1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
            Assert.Empty(events rig "reply.received")
            let! alexRows = Db.count rig.Db "SELECT count(*) FROM feed_posts WHERE local_actor = 'u-1'"
            Assert.Equal(0L, alexRows)
        }

    // ---- User Story 3: trust, privacy and blocks (SC-003) -----------------------------------------------------

    [<Fact>]
    member _.``FR-008 an Announce from a Person, or from a Group nobody follows, is dropped and nothing is fetched``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            rig.Stub.SetActorType("carl", "Group")
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"

            // bob is a Person, whoever follows him
            do! addFollowing rig.Db "u-1" rig.Stub "bob" "accepted"
            let personal = JsonNode.Parse(announceOf rig bob "p1" target)
            personal["actor"] <- JsonValue.Create bob
            let! fromPerson = sendAs rig "bob" (personal.ToJsonString())

            // carl is a Group, but nobody follows it; a pending follow is not a follow
            do! addFollowingTyped rig.Db "u-1" rig.Stub "carl" "pending" "Group"
            let carls = JsonNode.Parse(announceOf rig bob "c1" target)
            carls["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let carlId = rig.Stub.ActorId "carl"
            carls["id"] <- JsonValue.Create $"{carlId}/announces/c1"
            let! fromGroup = sendAs rig "carl" (carls.ToJsonString())

            Assert.Equal(HttpStatusCode.Accepted, fromPerson)
            Assert.Equal(HttpStatusCode.Accepted, fromGroup)
            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(statusGets rig.Stub)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-008 an Announce that is not the signer's own is dropped and nothing is fetched``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"

            // the Group signs, the activity says another actor announced
            let other = JsonNode.Parse(announceOf rig bob "a1" target)
            other["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let! otherActor = sendAs rig groupName (other.ToJsonString())

            // an activity id on another host than the signer
            let foreign = JsonNode.Parse(announceOf rig bob "a2" target)
            foreign["id"] <- JsonValue.Create "https://elsewhere.example/announces/a2"
            let! foreignId = sendAs rig groupName (foreign.ToJsonString())

            Assert.Equal(HttpStatusCode.Accepted, otherActor)
            Assert.Equal(HttpStatusCode.Accepted, foreignId)
            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(statusGets rig.Stub)
        }

    [<Fact>]
    member _.``FR-004 an Announce of an object on this instance is dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! status = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" (rootIri root))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(postEvents rig)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-004 a note attributed to an actor of another host than its own id is fetched and dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]

            // the Group's server serves a note whose author lives on another server
            let note = feedNote rig.Stub "bob" "1"
            note["attributedTo"] <- JsonValue.Create(other.ActorId "bob")
            rig.Stub.ServeNote("bob", "1", note)

            let! status = sendAs rig groupName (announceOf rig (other.ActorId "bob") "a1" (noteId rig.Stub "bob" "1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.NotEmpty(statusGets rig.Stub)
            Assert.Empty(postEvents rig)
            Assert.Empty(other.Gets)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-004 a note whose served id is not the announced one is dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let note = feedNote rig.Stub "bob" "2"
            rig.Stub.ServeNote("bob", "1", note)
            let! status = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" (noteId rig.Stub "bob" "1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.NotEmpty(statusGets rig.Stub)
            Assert.Empty(postEvents rig)
        }

    [<Fact>]
    member _.``FR-004 FR-008 a Question, and a followers-only or a direct note, are dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let bob = rig.Stub.ActorId "bob"

            let question = feedNote rig.Stub "bob" "1"
            question["type"] <- JsonValue.Create "Question"
            rig.Stub.ServeNote("bob", "1", question)

            let followersOnly = feedNote rig.Stub "bob" "2"
            setAddressing followersOnly [ bob + "/followers" ] []
            rig.Stub.ServeNote("bob", "2", followersOnly)

            let direct = feedNote rig.Stub "bob" "3"
            setAddressing direct [ Signed.ourActor "u-1" ] []
            rig.Stub.ServeNote("bob", "3", direct)

            for suffix in [ "1"; "2"; "3" ] do
                let! status = sendAs rig groupName (announceOf rig bob $"a{suffix}" (noteId rig.Stub "bob" suffix))
                Assert.Equal(HttpStatusCode.Accepted, status)

            do! settle ()
            Assert.Empty(postEvents rig)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-004 an unlisted note is passed on with its audience``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let bob = rig.Stub.ActorId "bob"
            let unlisted = feedNote rig.Stub "bob" "1"
            setAddressing unlisted [ bob + "/followers" ] [ publicIri ]
            rig.Stub.ServeNote("bob", "1", unlisted)

            let! _ = sendAs rig groupName (announceOf rig bob "a1" (noteId rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 2
            Assert.Equal("unlisted", jstr (at (events rig "post.received").Head.Json "object.audience"))
        }

    [<Fact>]
    member _.``FR-008 an Announce of a Delete, of a Like, or of an object without an id is dropped without a fetch``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"

            let inner (kind: string) =
                let o = JsonObject()
                o["id"] <- JsonValue.Create $"{target}#{kind}"
                o["type"] <- JsonValue.Create kind
                o["actor"] <- JsonValue.Create bob
                o["object"] <- JsonValue.Create target
                o

            for kind in [ "Delete"; "Like" ] do
                let doc = JsonNode.Parse(announceOf rig bob $"a-{kind}" target)
                doc["object"] <- inner kind
                let! status = sendAs rig groupName (doc.ToJsonString())
                Assert.Equal(HttpStatusCode.Accepted, status)

            let anonymous = JsonNode.Parse(announceOf rig bob "a-none" target)
            anonymous["object"] <- JsonNode.Parse """{"type":"Note","content":"<p>no id</p>"}"""
            let! anonymousStatus = sendAs rig groupName (anonymous.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, anonymousStatus)

            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(statusGets rig.Stub)
        }

    [<Fact>]
    member _.``FR-005 an Undo whose embedded Announce is not the signer's changes nothing``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! _ = sendAs rig groupName (announceOf rig bob "a1" target)
            do! waitForEvents rig "post.received" 2

            // the Group signs an Undo of an Announce that says carl sent it
            let undo = JsonNode.Parse(undoOf rig bob "u1" "a1" target)
            undo["object"]["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let! status = sendAs rig groupName (undo.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)

            // carl (not the Group the rows name) retracts the same post
            let carls = JsonNode.Parse(undoOf rig bob "u2" "a1" target)
            carls["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let carlId = rig.Stub.ActorId "carl"
            carls["id"] <- JsonValue.Create $"{carlId}/undos/u2"
            carls["object"]["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let! carlStatus = sendAs rig "carl" (carls.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, carlStatus)

            do! settle ()
            Assert.Empty(events rig "post.deleted")
            let! live = liveRows rig target
            Assert.Equal(2L, live)
        }

    [<Fact>]
    member _.``FR-005 a user who blocks the author hears nothing of the post, its edit or its deletion, the others do``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, first = serveNote rig "1"
            let _, second = serveNote rig "2"
            let bob = rig.Stub.ActorId "bob"

            // the first post arrives before the block
            let! _ = sendAs rig groupName (announceOf rig bob "a1" first)
            do! waitForEvents rig "post.received" 2

            do! Db.execute rig.Db $"INSERT INTO blocks (id, local_actor, actor) VALUES (gen_random_uuid(), 'u-1', '{bob}')"

            // a new post, an edit and a retraction: alex hears none of them, bea all
            let! _ = sendAs rig groupName (announceOf rig bob "a2" second)
            do! waitForEvents rig "post.received" 3
            serveEdit rig "1" "<p>v2</p>" "2026-10-12T18:30:00Z"
            let! _ = sendAs rig groupName (announceUpdateOf rig bob "e1" first)
            do! waitForEvents rig "post.updated" 1
            let! _ = sendAs rig groupName (undoOf rig bob "u1" "a1" first)
            do! waitForEvents rig "post.deleted" 1
            do! settle ()

            Assert.Equal<string list>([ "u-1"; "u-2"; "u-2" ], recipients rig "post.received")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.updated")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.deleted")

            // the row is deleted for alex too, though he was told nothing
            let! alexLive = Db.count rig.Db $"SELECT count(*) FROM feed_posts WHERE id = '{first}' AND local_actor = 'u-1' AND state = 'live'"
            Assert.Equal(0L, alexLive)
        }

    [<Fact>]
    member _.``FR-005 a user who blocks the author's domain hears nothing, nor does the Group's domain need to be blocked``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]

            // the author lives on `localhost:port`, which the same stub also answers: another domain than the Group's 127.0.0.1
            let alias = other.BaseUrl.Replace("127.0.0.1", "localhost")
            let authority = Uri(alias).Authority
            let authorId = $"{alias}/users/bob"
            do!
                Db.execute
                    rig.Db
                    $"""INSERT INTO remote_actors (id, host, handle, actor_type, inbox, followers_url, key_id, public_key_pem)
                        VALUES ('{authorId}', '{authority}', 'bob@{authority}', 'Person', '{authorId}/inbox', '{authorId}/followers', '{authorId}#main-key', '{other.PublicKeyPem}')"""

            let note = feedNote other "bob" "1"
            note["id"] <- JsonValue.Create $"{authorId}/statuses/1"
            note["attributedTo"] <- JsonValue.Create authorId
            note["to"] <- JsonArray(JsonValue.Create publicIri)
            note["cc"] <- JsonArray(JsonValue.Create(authorId + "/followers"))
            other.ServeNote("bob", "1", note)

            do! Db.execute rig.Db $"INSERT INTO blocks (id, local_actor, domain) VALUES (gen_random_uuid(), 'u-1', 'localhost')"

            let! status = sendAs rig groupName (announceOf rig authorId "a1" $"{authorId}/statuses/1")
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
            Assert.Equal(authorId, jstr (at (events rig "post.received").Head.Json "actor.id"))
        }

    [<Fact>]
    member _.``FR-005 a user who blocks the Group through the API has no follow left and hears nothing``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let! blocked, _ = sendInternal rig HttpMethod.Post "/internal/actors/u-1/blocks" (Some(internalJson [ "actor", groupId rig ]))
            Assert.Equal(201, blocked)
            let! follow = Db.scalar rig.Db "SELECT status FROM follows WHERE direction = 'outbound' AND local_actor = 'u-1'"
            Assert.Equal("removed", string follow)

            let! _ = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" target)
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
        }

    [<Fact>]
    member _.``FR-005 a block of the Group next to an accepted follow stops the edit and the retraction for that user``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! _ = sendAs rig groupName (announceOf rig bob "a1" target)
            do! waitForEvents rig "post.received" 2

            do! Db.execute rig.Db $"INSERT INTO blocks (id, local_actor, actor) VALUES (gen_random_uuid(), 'u-1', '{groupId rig}')"
            serveEdit rig "1" "<p>v2</p>" "2026-10-12T18:30:00Z"
            let! _ = sendAs rig groupName (announceUpdateOf rig bob "e1" target)
            do! waitForEvents rig "post.updated" 1
            let! _ = sendAs rig groupName (undoOf rig bob "u1" "a1" target)
            do! waitForEvents rig "post.deleted" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.updated")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.deleted")
        }

    [<Fact>]
    member _.``FR-005 an instance block of the Group's domain answers 403 and nothing is fetched``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let host = Megafono.Infrastructure.Blocks.hostName (rig.Stub.Authority.ToLowerInvariant())
            let! blocked, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(internalJson [ "domain", host ]))
            Assert.Equal(201, blocked)

            let! status = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" target)
            Assert.Equal(HttpStatusCode.Forbidden, status)
            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(statusGets rig.Stub)
        }

    [<Fact>]
    member _.``FR-005 an instance block of the author's domain: the domain is never contacted and no user hears of the post``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]
            let alias = other.BaseUrl.Replace("127.0.0.1", "localhost")
            let authorId = $"{alias}/users/bob"
            let note = feedNote other "bob" "1"
            note["id"] <- JsonValue.Create $"{authorId}/statuses/1"
            note["attributedTo"] <- JsonValue.Create authorId
            other.ServeNote("bob", "1", note)

            let! blocked, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(internalJson [ "domain", "localhost" ]))
            Assert.Equal(201, blocked)

            let! status = sendAs rig groupName (announceOf rig authorId "a1" $"{authorId}/statuses/1")
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(postEvents rig)
            Assert.Empty(other.Gets)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-005 an instance block of the author's actor drops the post``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let bob = rig.Stub.ActorId "bob"
            let! blocked, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(internalJson [ "actor", bob ]))
            Assert.Equal(201, blocked)

            let! status = sendAs rig groupName (announceOf rig bob "a1" target)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(postEvents rig)
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-005 a deleted user hears nothing, the other follower still does``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let _, target = serveNote rig "1"
            let! deleted, _ = sendInternal rig HttpMethod.Delete "/internal/actors/u-1" None
            Assert.Equal(202, deleted)

            let! _ = sendAs rig groupName (announceOf rig (rig.Stub.ActorId "bob") "a1" target)
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
            let! rows = Db.count rig.Db "SELECT count(*) FROM feed_posts WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)
        }
