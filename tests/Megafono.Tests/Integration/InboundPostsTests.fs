module Megafono.Tests.Integration.InboundPostsTests

open System
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open System.Text.Json
open Xunit
open Megafono.Domain
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private inboxUrl (rig: Rig) = rig.Host.FederationUrl + "/inbox"

let private sendTo (url: string) (rig: Rig) (name: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name url body
        return response.StatusCode
    }

let private send (rig: Rig) (name: string) (body: string) = sendTo (inboxUrl rig) rig name body

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

/// The users an event type reached, sorted
let private recipients (rig: Rig) (eventType: string) : string list =
    events rig eventType |> List.map (fun e -> jstr (at e.Json "user.id")) |> List.sort

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private feedRows (rig: Rig) = Db.count rig.Db "SELECT count(*) FROM feed_posts"

/// The app users of the scenarios: u-1 alex and u-2 bea follow bob (accepted); u-3 dave's follow is pending, u-4's
/// rejected, u-5's removed; u-6 erin follows nobody. bob, carl and mallory live on the stub remote.
let private start (pg: PostgresFixture) : Task<Rig> =
    task {
        let! rig = Publishing.start pg [ "bob"; "carl"; "mallory" ]

        for id, handle in [ "u-3", "dave"; "u-4", "fay"; "u-5", "gil"; "u-6", "erin" ] do
            rig.App.AddUser(FakeApp.user id handle)
            use client = rig.Host.FederationClient()
            use! response = client.GetAsync $"/ap/actors/{id}"
            response.EnsureSuccessStatusCode() |> ignore

        for user, status in [ "u-1", "accepted"; "u-2", "accepted"; "u-3", "pending"; "u-4", "rejected"; "u-5", "removed" ] do
            do! addFollowing rig.Db user rig.Stub "bob" status

        return rig
    }

let private followers (rig: Rig) (name: string) = rig.Stub.ActorId name + "/followers"

let private rootIri (objectId: string) = Ids.objectId "megafono.test" (Guid.Parse objectId)

/// A followed account's post, the way an edit of it reads: new content and the edit's own `updated` time
let private edited (rig: Rig) (name: string) (suffix: string) (content: string) (updated: string) : JsonObject =
    let note = feedNote rig.Stub name suffix
    note["content"] <- JsonValue.Create content
    note["updated"] <- JsonValue.Create updated
    note

let private liveRows (rig: Rig) (id: string) =
    Db.count rig.Db $"SELECT count(*) FROM feed_posts WHERE id = '{id}' AND state = 'live'"

let private announce (rig: Rig) (name: string) (suffix: string) (target: string) : string =
    let o = JsonObject()
    o["@context"] <- JsonValue.Create "https://www.w3.org/ns/activitystreams"
    o["id"] <- JsonValue.Create $"{rig.Stub.ActorId name}/statuses/{suffix}/announce"
    o["type"] <- JsonValue.Create "Announce"
    o["actor"] <- JsonValue.Create(rig.Stub.ActorId name)
    o["to"] <- JsonArray(JsonValue.Create Signed.publicIri)
    o["object"] <- JsonValue.Create target
    o.ToJsonString()

let private internalJson (fields: (string * string) list) : string =
    JsonSerializer.Serialize(dict fields)

type InboundPostsTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-001 FR-007 a public post of a followed account reaches each follower once, with every field of the contract``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let note = feedNote rig.Stub "bob" "1"
            note["content"] <- JsonValue.Create "<p>Started Frieren S2</p>"
            note["summary"] <- JsonValue.Create "Spoilers: S2 E1"
            note["sensitive"] <- JsonValue.Create true
            note["contentMap"] <- JsonNode.Parse """{"en":"<p>Started Frieren S2</p>"}"""
            note["attachment"] <- JsonNode.Parse """[{"type":"Image","mediaType":"image/png","url":"https://files.example/a.png","name":"alt text"}]"""

            let! status = send rig "bob" (createNote rig.Stub "bob" "1" note)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 2
            do! settle ()

            // alex and bea only: pending, rejected and removed follows and a user who follows nobody hear nothing
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")

            for e in events rig "post.received" do
                Assert.True e.SignatureValid
                Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
                Assert.Equal($"@bob@{rig.Stub.Authority}", jstr (at e.Json "actor.handle"))
                Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
                Assert.Equal($"{rig.Stub.BaseUrl}/@bob/1", jstr (at e.Json "object.url"))
                Assert.Equal("Note", jstr (at e.Json "object.type"))
                Assert.Equal("<p>Started Frieren S2</p>", jstr (at e.Json "object.content"))
                Assert.Equal("Spoilers: S2 E1", jstr (at e.Json "object.summary"))
                Assert.True(boolOf (at e.Json "object.sensitive"))
                Assert.Equal("en", jstr (at e.Json "object.language"))
                Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))
                Assert.Null(at e.Json "object.updated")
                Assert.Equal("public", jstr (at e.Json "object.audience"))
                Assert.Null(at e.Json "object.inReplyTo")
                Assert.Null(at e.Json "object.parent")
                Assert.Null(at e.Json "object.thread")
                Assert.Equal("Image", jstr (at e.Json "object.attachment.0.type"))
                Assert.Equal("image/png", jstr (at e.Json "object.attachment.0.mediaType"))
                Assert.Equal("https://files.example/a.png", jstr (at e.Json "object.attachment.0.url"))
                Assert.Equal("alt text", jstr (at e.Json "object.attachment.0.name"))
                Assert.Null(at e.Json "extensions")

            let! rows = feedRows rig
            Assert.Equal(2L, rows)
        }

    [<Fact>]
    member _.``FR-002 unlisted and followers-only posts reach the followers with their audience``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let unlisted = feedNote rig.Stub "bob" "1"
            setAddressing unlisted [ followers rig "bob" ] [ Signed.publicIri ]
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" unlisted)

            let followersOnly = feedNote rig.Stub "bob" "2"
            setAddressing followersOnly [ followers rig "bob" ] []
            let! _ = send rig "bob" (createNote rig.Stub "bob" "2" followersOnly)

            do! waitForEvents rig "post.received" 4
            let audience (id: string) (user: string) =
                events rig "post.received"
                |> List.find (fun e -> jstr (at e.Json "object.id") = id && jstr (at e.Json "user.id") = user)
                |> fun e -> jstr (at e.Json "object.audience")

            for user in [ "u-1"; "u-2" ] do
                Assert.Equal("unlisted", audience (noteId rig.Stub "bob" "1") user)
                Assert.Equal("followers", audience (noteId rig.Stub "bob" "2") user)
        }

    [<Fact>]
    member _.``FR-002 a direct post reaches only the followers it is addressed to, and a post for a non-follower reaches nobody``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let toAlex = feedNote rig.Stub "bob" "1"
            setAddressing toAlex [ Signed.ourActor "u-1" ] []
            let! status = send rig "bob" (createNote rig.Stub "bob" "1" toAlex)
            Assert.Equal(HttpStatusCode.Accepted, status)

            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "post.received")
            Assert.Equal("direct", jstr (at (events rig "post.received").Head.Json "object.audience"))

            // addressed to erin, who does not follow bob: nobody hears of it, and nothing is recorded for it
            let toErin = feedNote rig.Stub "bob" "2"
            setAddressing toErin [ Signed.ourActor "u-6" ] []
            let! erinStatus = send rig "bob" (createNote rig.Stub "bob" "2" toErin)
            Assert.Equal(HttpStatusCode.Accepted, erinStatus)
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "post.received")
            let erinPost = noteId rig.Stub "bob" "2"
            let! rows = Db.count rig.Db $"SELECT count(*) FROM feed_posts WHERE id = '{erinPost}'"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-004 SC-003 the same post twice, or under another activity id on a personal inbox, is one event per user``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let body = createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1")
            let! first = send rig "bob" body
            let! second = send rig "bob" body
            Assert.Equal(HttpStatusCode.Accepted, first)
            Assert.Equal(HttpStatusCode.Accepted, second)

            // the same Note under a new activity id, on bea's personal inbox: the inbox says nothing about who gets it
            let again = JsonNode.Parse body
            again["id"] <- JsonValue.Create(noteId rig.Stub "bob" "1" + "/activity-2")
            let! third = sendTo (rig.Host.FederationUrl + "/ap/actors/u-2/inbox") rig "bob" (again.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, third)

            do! waitForEvents rig "post.received" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")
            let! rows = feedRows rig
            Assert.Equal(2L, rows)
        }

    [<Fact>]
    member _.``FR-004 a post sent to one user's personal inbox still reaches every follower``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! status = sendTo (rig.Host.FederationUrl + "/ap/actors/u-6/inbox") rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.received" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")
        }

    [<Fact>]
    member _.``FR-012 a reply by a followed account to a third party's post is a feed post with inReplyTo``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let parent = "https://other.example/users/zed/statuses/9"
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (note rig.Stub "bob" "1" parent))
            do! waitForEvents rig "post.received" 2
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")

            for e in events rig "post.received" do
                Assert.Equal(parent, jstr (at e.Json "object.inReplyTo"))
        }

    [<Fact>]
    member _.``FR-001 an object that is not a Note or Article is acknowledged and dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig

            for kind, suffix in [ "Question", "1"; "Page", "2" ] do
                let note = feedNote rig.Stub "bob" suffix
                note["type"] <- JsonValue.Create kind
                let! status = send rig "bob" (createNote rig.Stub "bob" suffix note)
                Assert.Equal(HttpStatusCode.Accepted, status)

            do! settle ()
            Assert.Empty(events rig "post.received")
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-001 an Article is a feed post too``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let article = feedNote rig.Stub "bob" "1"
            article["type"] <- JsonValue.Create "Article"
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" article)
            do! waitForEvents rig "post.received" 2
            Assert.Equal("Article", jstr (at (events rig "post.received").Head.Json "object.type"))
        }

    [<Fact>]
    member _.``FR-001 a Create that names the Note by id is fetched from its origin, signed``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let note = feedNote rig.Stub "bob" "1"
            note["content"] <- JsonValue.Create "<p>from the origin</p>"
            rig.Stub.ServeNote("bob", "1", note)
            let activity = JsonNode.Parse(createNote rig.Stub "bob" "1" note)
            activity["object"] <- JsonValue.Create(noteId rig.Stub "bob" "1")
            let! status = send rig "bob" (activity.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)

            do! waitForEvents rig "post.received" 2
            Assert.Equal("<p>from the origin</p>", jstr (at (events rig "post.received").Head.Json "object.content"))
            Assert.Single(rig.Stub.Gets |> List.filter (fun g -> g.Path = "/users/bob/statuses/1")) |> ignore
        }

    [<Fact>]
    member _.``FR-001 an embedded Note with another host's id is fetched from that origin, and must be attributed to the signer``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]

            // the Note lives on another server than bob's; bob signs the Create, the Note says bob wrote it
            let theirs = feedNote rig.Stub "bob" "1"
            theirs["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/1"
            theirs["content"] <- JsonValue.Create "<p>the origin's copy</p>"
            other.ServeNote("bob", "1", theirs)

            let embedded = feedNote rig.Stub "bob" "1"
            embedded["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/1"
            embedded["content"] <- JsonValue.Create "<p>the embedded copy is not trusted</p>"
            let! status = send rig "bob" (createNote rig.Stub "bob" "1" embedded)
            Assert.Equal(HttpStatusCode.Accepted, status)

            do! waitForEvents rig "post.received" 2
            Assert.Equal("<p>the origin's copy</p>", jstr (at (events rig "post.received").Head.Json "object.content"))
            Assert.NotEmpty(other.Gets |> List.filter (fun g -> g.Path = "/users/bob/statuses/1"))

            // the origin serves a Note attributed to someone else: dropped
            let forged = feedNote rig.Stub "carl" "2"
            forged["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/2"
            other.ServeNote("bob", "2", forged)
            let claimed = feedNote rig.Stub "bob" "2"
            claimed["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/2"
            let! forgedStatus = send rig "bob" (createNote rig.Stub "bob" "2" claimed)
            Assert.Equal(HttpStatusCode.Accepted, forgedStatus)
            do! settle ()
            Assert.Equal(2, (events rig "post.received").Length)
        }

    [<Fact>]
    member _.``FR-008 data under the app's own namespace is passed back in extensions, whatever the prefix; any other namespace is not``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig

            // alex published with the namespace once: that is how Megáfono knows it is the app's
            let body = publishBody "public" "<p>Episode 6</p>"
            body["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example.com/ns#","prefix":"app","data":{"episode":6}}"""
            let! created, _ = publishRaw rig "u-1" (body.ToJsonString()) None
            Assert.Equal(201, created)

            let note = feedNote rig.Stub "bob" "1"
            note["x:rating"] <- JsonNode.Parse """{"stars":5}"""
            note["other:ignored"] <- JsonValue.Create "nope"
            let context = JsonNode.Parse """["https://www.w3.org/ns/activitystreams",{"x":"https://app.example.com/ns#","other":"https://elsewhere.example/ns#"}]"""
            let! _ = send rig "bob" (createNoteWithContext rig.Stub "bob" "1" note context)
            do! waitForEvents rig "post.received" 2

            for e in events rig "post.received" do
                Assert.Equal("https://app.example.com/ns#", jstr (at e.Json "extensions.namespace"))
                Assert.Equal("x", jstr (at e.Json "extensions.prefix"))
                Assert.Equal(5, intOf (at e.Json "extensions.data.rating.stars"))
                Assert.Null(at e.Json "extensions.data.ignored")

            // a namespace the app never published with: null
            let foreign = feedNote rig.Stub "bob" "2"
            foreign["y:rating"] <- JsonNode.Parse """{"stars":1}"""
            let foreignContext = JsonNode.Parse """["https://www.w3.org/ns/activitystreams",{"y":"https://elsewhere.example/ns#"}]"""
            let! _ = send rig "bob" (createNoteWithContext rig.Stub "bob" "2" foreign foreignContext)
            do! waitForEvents rig "post.received" 4

            for e in events rig "post.received" |> List.filter (fun e -> jstr (at e.Json "object.id") = noteId rig.Stub "bob" "2") do
                Assert.Null(at e.Json "extensions")
        }

    [<Fact>]
    member _.``FR-001 a post of an author nobody follows is acknowledged and leaves no row``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! status = send rig "carl" (createNote rig.Stub "carl" "1" (feedNote rig.Stub "carl" "1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(events rig "post.received")
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-001 a post whose Note is attributed to someone other than the signer is dropped``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            // mallory signs, the Note says bob wrote it
            let activity = JsonNode.Parse(createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            activity["actor"] <- JsonValue.Create(rig.Stub.ActorId "mallory")
            let! status = send rig "mallory" (activity.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(events rig "post.received")
        }

    // ---- User Story 2: edits and deletions follow the post (T014) --------------------------------------------

    [<Fact>]
    member _.``FR-005 an edit reaches each recipient as post.updated with the new content, updated set, and the original audience and published``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let original = feedNote rig.Stub "bob" "1"
            setAddressing original [ followers rig "bob" ] []
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" original)
            do! waitForEvents rig "post.received" 2

            // the edit arrives addressed to everyone, with another `published`: neither changes what the app is told
            let edit = edited rig "bob" "1" "<p>v2</p>" "2026-10-12T18:30:00Z"
            edit["published"] <- JsonValue.Create "2026-10-13T00:00:00Z"
            let! status = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" edit)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "post.updated" 2
            do! settle ()

            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.updated")

            for e in events rig "post.updated" do
                Assert.True e.SignatureValid
                Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
                Assert.Equal("<p>v2</p>", jstr (at e.Json "object.content"))
                Assert.Equal("2026-10-12T18:30:00Z", jstr (at e.Json "object.updated"))
                Assert.Equal("followers", jstr (at e.Json "object.audience"))
                Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))
                Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
        }

    [<Fact>]
    member _.``R-55 one activity id with two different updated times is two edits, and the same edit twice is one``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 2

            // GoToSocial gives every Update of a status the same activity id
            let first = updateNote rig.Stub "bob" "1" "e1" (edited rig "bob" "1" "<p>v2</p>" "2026-10-12T18:30:00Z")
            let! _ = send rig "bob" first
            let! _ = send rig "bob" first
            do! waitForEvents rig "post.updated" 2
            do! settle ()
            Assert.Equal(2, (events rig "post.updated").Length)

            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" (edited rig "bob" "1" "<p>v3</p>" "2026-10-12T19:00:00Z"))
            do! waitForEvents rig "post.updated" 4
            Assert.Equal<string list>([ "u-1"; "u-1"; "u-2"; "u-2" ], recipients rig "post.updated")
        }

    member private _.AfterUnfollow(tombstone: bool) : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 2

            // alex unfollows: an edit is new content from an account alex no longer follows, a deletion is not
            do! setFollowStatus rig.Db "u-1" rig.Stub "bob" "removed"
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" (edited rig "bob" "1" "<p>v2</p>" "2026-10-12T18:30:00Z"))
            do! waitForEvents rig "post.updated" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.updated")

            let! deleteStatus = send rig "bob" (deleteNote rig.Stub "bob" "1" tombstone)
            Assert.Equal(HttpStatusCode.Accepted, deleteStatus)
            do! waitForEvents rig "post.deleted" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.deleted")

            for e in events rig "post.deleted" do
                Assert.True e.SignatureValid
                Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
                Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
                Assert.Equal(1, (at e.Json "object").AsObject().Count)

            // a later edit or a repeated Delete changes nothing
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "e2" (edited rig "bob" "1" "<p>v3</p>" "2026-10-12T20:00:00Z"))
            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" tombstone)
            do! settle ()
            Assert.Equal(1, (events rig "post.updated").Length)
            Assert.Equal(2, (events rig "post.deleted").Length)
            let! live = liveRows rig (noteId rig.Stub "bob" "1")
            Assert.Equal(0L, live)
        }

    [<Fact>]
    member this.``FR-006 after an unfollow an edit reaches only the one still following, a Delete (by IRI) both``() : Task = this.AfterUnfollow false

    [<Fact>]
    member this.``FR-006 a Delete with a Tombstone is the same``() : Task = this.AfterUnfollow true

    [<Fact>]
    member _.``FR-005 an Update or Delete of a post never passed on, or signed by someone else, changes nothing and fetches nothing``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 2

            // bob edits and deletes a post that was never passed on: the Note is embedded on another host, so reading it
            // would mean a fetch; nothing is fetched because no row exists
            let unknown = edited rig "bob" "7" "<p>x</p>" "2026-10-12T18:30:00Z"
            unknown["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/7"
            other.ServeNote("bob", "7", unknown)
            let! updateStatus = send rig "bob" (updateNote rig.Stub "bob" "7" "e1" unknown)
            let! deleteStatus = send rig "bob" (deleteNote rig.Stub "bob" "8" true)
            Assert.Equal(HttpStatusCode.Accepted, updateStatus)
            Assert.Equal(HttpStatusCode.Accepted, deleteStatus)

            // carl signs an edit and a Delete of bob's post
            let theirs = edited rig "bob" "1" "<p>hijacked</p>" "2026-10-12T18:30:00Z"
            let hijack = JsonNode.Parse(updateNote rig.Stub "carl" "1" "e1" theirs)
            let! hijackStatus = send rig "carl" (hijack.ToJsonString())
            let carlsDelete = JsonNode.Parse(deleteNote rig.Stub "carl" "1" false)
            carlsDelete["object"] <- JsonValue.Create(noteId rig.Stub "bob" "1")
            let! carlsDeleteStatus = send rig "carl" (carlsDelete.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, hijackStatus)
            Assert.Equal(HttpStatusCode.Accepted, carlsDeleteStatus)

            do! settle ()
            Assert.Empty(events rig "post.updated")
            Assert.Empty(events rig "post.deleted")
            Assert.Empty(other.Gets |> List.filter (fun g -> g.Path.Contains "/statuses/"))
            Assert.Empty(rig.Stub.Gets |> List.filter (fun g -> g.Path.Contains "/statuses/"))
            let! live = liveRows rig (noteId rig.Stub "bob" "1")
            Assert.Equal(2L, live)
        }

    [<Fact>]
    member _.``R-54 an Update whose re-read object has another id changes nothing``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            use! other = StubRemote.start [ "bob" ]
            let id = $"{other.BaseUrl}/users/bob/statuses/9"
            let bob = rig.Stub.ActorId "bob"
            do!
                Db.execute
                    rig.Db
                    $"INSERT INTO feed_posts (id, local_actor, remote_actor, audience, published_at) VALUES ('{id}', 'u-1', '{bob}', 'public', now())"

            // the origin answers the fetch with a Note of another id
            let served = edited rig "bob" "9" "<p>other</p>" "2026-10-12T18:30:00Z"
            served["id"] <- JsonValue.Create $"{other.BaseUrl}/users/bob/statuses/10"
            other.ServeNote("bob", "9", served)
            let claimed = edited rig "bob" "9" "<p>claimed</p>" "2026-10-12T18:30:00Z"
            claimed["id"] <- JsonValue.Create id
            let! status = send rig "bob" (updateNote rig.Stub "bob" "9" "e1" claimed)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(events rig "post.updated")
            Assert.NotEmpty(other.Gets |> List.filter (fun g -> g.Path = "/users/bob/statuses/9"))
        }

    // ---- User Story 3: blocks, deleted users and threads (T017, T018) ----------------------------------------

    [<Fact>]
    member _.``FR-009 a user who blocks the author through the API no longer hears of its posts, the others do``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! status, _ = sendInternal rig HttpMethod.Post "/internal/actors/u-1/blocks" (Some(internalJson [ "actor", rig.Stub.ActorId "bob" ]))
            Assert.Equal(201, status)
            let! followStatus = Db.scalar rig.Db "SELECT status FROM follows WHERE direction = 'outbound' AND local_actor = 'u-1'"
            Assert.Equal("removed", string followStatus)

            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
        }

    member private _.BlockNextToFollow(blockSql: string) : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let host = Megafono.Infrastructure.Blocks.hostName (rig.Stub.Authority.ToLowerInvariant())
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 2

            // a block row beside alex's still-accepted follow (a race, or a block made before the follow was accepted)
            do! Db.execute rig.Db (blockSql.Replace("{bob}", rig.Stub.ActorId "bob").Replace("{host}", host))
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" (edited rig "bob" "1" "<p>v2</p>" "2026-10-12T18:30:00Z"))
            let! _ = send rig "bob" (createNote rig.Stub "bob" "2" (feedNote rig.Stub "bob" "2"))
            do! waitForEvents rig "post.updated" 1
            do! waitForEvents rig "post.received" 3
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.updated")
            Assert.Equal<string list>([ "u-1"; "u-2"; "u-2" ], recipients rig "post.received")

            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            do! waitForEvents rig "post.deleted" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.deleted")

            // alex was told nothing, but the post is deleted for alex too: its row is not live any more
            let postId = noteId rig.Stub "bob" "1"
            let! alexLive = Db.count rig.Db $"SELECT count(*) FROM feed_posts WHERE id = '{postId}' AND local_actor = 'u-1' AND state = 'live'"
            Assert.Equal(0L, alexLive)
        }

    [<Fact>]
    member this.``R-56 an actor block next to an accepted follow stops post.updated and post.deleted, the row still turns deleted``() : Task =
        this.BlockNextToFollow "INSERT INTO blocks (id, local_actor, actor) VALUES (gen_random_uuid(), 'u-1', '{bob}')"

    [<Fact>]
    member this.``R-56 a domain block next to an accepted follow does the same``() : Task =
        this.BlockNextToFollow "INSERT INTO blocks (id, local_actor, domain) VALUES (gen_random_uuid(), 'u-1', '{host}')"

    [<Fact>]
    member _.``FR-009 an instance block of the author's domain answers 403 and no user hears of the post``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let host = Megafono.Infrastructure.Blocks.hostName (rig.Stub.Authority.ToLowerInvariant())
            let! status, _ = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(internalJson [ "domain", host ]))
            Assert.Equal(201, status)

            let! refused = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            Assert.Equal(HttpStatusCode.Forbidden, refused)
            do! settle ()
            Assert.Empty(events rig "post.received")
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-009 a deleted user hears nothing, the other followers still do``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! status, _ = sendInternal rig HttpMethod.Delete "/internal/actors/u-1" None
            Assert.Equal(202, status)

            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (feedNote rig.Stub "bob" "1"))
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
            let! rows = Db.count rig.Db "SELECT count(*) FROM feed_posts WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-003 a reply into the app's own thread reaches its author as the reply events only, the other followers get the feed events``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let reply = note rig.Stub "bob" "1" (rootIri root)

            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" reply)
            do! waitForEvents rig "reply.received" 1
            do! waitForEvents rig "post.received" 1
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "reply.received")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.received")
            Assert.Equal(rootIri root, jstr (at (events rig "post.received").Head.Json "object.inReplyTo"))
            let! alexRows = Db.count rig.Db "SELECT count(*) FROM feed_posts WHERE local_actor = 'u-1'"
            Assert.Equal(0L, alexRows)

            // the edit
            let edit = note rig.Stub "bob" "1" (rootIri root)
            edit["content"] <- JsonValue.Create "<p>edited</p>"
            edit["updated"] <- JsonValue.Create "2026-10-12T18:30:00Z"
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "e1" edit)
            do! waitForEvents rig "reply.updated" 1
            do! waitForEvents rig "post.updated" 1
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "reply.updated")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.updated")

            // the deletion
            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            do! waitForEvents rig "object.deleted" 1
            do! waitForEvents rig "post.deleted" 1
            do! settle ()
            Assert.Equal<string list>([ "u-1" ], recipients rig "object.deleted")
            Assert.Equal<string list>([ "u-2" ], recipients rig "post.deleted")
        }

    [<Fact>]
    member _.``R-53 a reply to a deleted post of the app is an ordinary feed post for everyone who follows the author``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! deleted, _ = sendInternal rig HttpMethod.Delete $"/internal/objects/{root}" None
            Assert.Equal(202, deleted)

            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (note rig.Stub "bob" "1" (rootIri root)))
            do! waitForEvents rig "post.received" 2
            do! settle ()
            Assert.Equal<string list>([ "u-1"; "u-2" ], recipients rig "post.received")
            Assert.Empty(events rig "reply.received")
        }

    [<Fact>]
    member _.``R-57 posts of two followed authors to overlapping followers, next to an instance block, all arrive without a deadlock``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            do! addFollowing rig.Db "u-1" rig.Stub "carl" "accepted"
            do! addFollowing rig.Db "u-2" rig.Stub "carl" "accepted"
            do! addFollowing rig.Db "u-3" rig.Stub "carl" "accepted"
            do! addFollowing rig.Db "u-3" rig.Stub "bob" "accepted"

            let each = 8

            let sends =
                [ for i in 1..each do
                      for name in [ "bob"; "carl" ] do
                          let suffix = string i
                          yield send rig name (createNote rig.Stub name suffix (feedNote rig.Stub name suffix)) ]

            // the instance block of a third domain locks every local actor while the posts are being passed on
            let blocks =
                [ for domain in [ "third-1.example"; "third-2.example"; "third-3.example" ] do
                      yield sendInternal rig HttpMethod.Post "/internal/blocks" (Some(internalJson [ "domain", domain ])) ]

            let! statuses = Task.WhenAll sends
            let! blockResults = Task.WhenAll blocks
            Assert.All(statuses, fun s -> Assert.Equal(HttpStatusCode.Accepted, s))
            Assert.All(blockResults, fun (s, _) -> Assert.Equal(201, s))

            // bob's posts reach u-1, u-2 and u-3; carl's reach u-1, u-2 and u-3 as well: 2 authors * 8 posts * 3 users
            do! waitForEvents rig "post.received" (2 * each * 3)
            do! settle ()
            Assert.Equal(2 * each * 3, (events rig "post.received").Length)
            let! rows = feedRows rig
            Assert.Equal(int64 (2 * each * 3), rows)
        }

    // ---- User Story 4: boosts are not passed on (T020) -------------------------------------------------------

    [<Fact>]
    member _.``FR-012 an Announce of the author's own post or of a third party's is acknowledged and dropped, nothing fetched``() : Task =
        task {
            let! rig = start pg
            use _ = disposeRig rig
            let! own = send rig "bob" (announce rig "bob" "1" (noteId rig.Stub "bob" "1"))
            let! theirs = send rig "bob" (announce rig "bob" "2" "https://other.example/users/zed/statuses/5")
            Assert.Equal(HttpStatusCode.Accepted, own)
            Assert.Equal(HttpStatusCode.Accepted, theirs)

            do! settle ()
            Assert.Empty(events rig "post.received")
            Assert.Empty(rig.App.Received |> List.filter (fun e -> e.Type.StartsWith "post."))
            Assert.Empty(rig.Stub.Gets |> List.filter (fun g -> g.Path.Contains "/statuses/"))
            let! rows = feedRows rig
            Assert.Equal(0L, rows)
        }
