module Megafono.Tests.Integration.OutboundRepliesTests

open System
open System.Net
open System.Net.Http
open System.Text
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

let private replyBody (audience: string) (inReplyTo: string) (content: string) : string =
    let o = publishBody audience content
    o["object"]["inReplyTo"] <- JsonValue.Create inReplyTo
    o.ToJsonString()

let private reply (rig: Rig) (user: string) (audience: string) (inReplyTo: string) (key: string option) =
    publishRaw rig user (replyBody audience inReplyTo "<p>a comment</p>") key

/// The id of a reply that must have been accepted
let private replyOk (rig: Rig) (user: string) (inReplyTo: string) : Task<string> =
    task {
        let! status, text = reply rig user "public" inReplyTo None
        Assert.Equal(201, status)
        return jstr (at (json text) "id")
    }

/// The object as a remote server fetches it
let private fetchObject (rig: Rig) (id: string) : Task<JsonNode> =
    task {
        use! response = federationGet rig (objectPath id) []
        Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        let! text = response.Content.ReadAsStringAsync()
        return json text
    }

/// A reply from `name` on the stub to `inReplyTo`, delivered to Megáfono's inbox (so it is recorded in the thread)
let private remoteReply (rig: Rig) (name: string) (suffix: string) (inReplyTo: string) : Task =
    task {
        use client = new HttpClient()
        let note = Signed.note rig.Stub name suffix inReplyTo
        use! response = Signed.postText client rig.Stub name (rig.Host.FederationUrl + "/inbox") (createNote rig.Stub name suffix note)
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode)
        let! recorded = Db.count rig.Db $"SELECT count(*) FROM replies WHERE id = '{noteId rig.Stub name suffix}'"
        Assert.Equal(1L, recorded)
    }

/// The inboxes an activity of this type for this object is queued for, sorted
let private inboxesOf (rig: Rig) (objectId: string) (activityType: string) : Task<string list> =
    Publishing.strings rig.Db $"SELECT d.inbox_url FROM deliveries d JOIN activities a ON a.id = d.activity WHERE a.object = '{objectId}' AND a.activity_type = '{activityType}' ORDER BY 1"

let private waitForPosts (rig: Rig) (path: string) (n: int) =
    waitUntil $"{n} post(s) at {path}" (TimeSpan.FromSeconds 15.0) (fun () -> (rig.Stub.Posts |> List.filter (fun p -> p.Path = path)).Length >= n)

let private put (rig: Rig) (id: string) (body: string) = sendInternal rig HttpMethod.Put $"/internal/objects/{id}" (Some body)
let private delete (rig: Rig) (id: string) = sendInternal rig HttpMethod.Delete $"/internal/objects/{id}" None

/// alex (u-1) has carl as an accepted follower; bea (u-2) published the thread root; bob has replied to it
let private start' (pg: PostgresFixture) : Task<Rig * string> =
    task {
        let! rig = Publishing.start pg [ "bob"; "carl" ]
        do! addFollower rig.Db "u-1" rig.Stub "carl" "accepted" None
        let! root = publish rig "u-2" "public" "<p>Episode 5</p>"
        return rig, root
    }

type OutboundRepliesTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-010 an inReplyTo that is unknown is refused with 400, a deleted parent with 410``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! status, text = reply rig "u-1" "public" "https://other.example/users/x/statuses/1" None
            Assert.Equal(400, status)
            Assert.Equal("invalid_request", jstr (at (json text) "error"))
            Assert.Contains("inReplyTo", jstr (at (json text) "message"))
            // one of our IRIs that names nothing
            let! status, _ = reply rig "u-1" "public" (objectIri (string (Guid.NewGuid()))) None
            Assert.Equal(400, status)
            // not an https URL at all
            let! status, _ = reply rig "u-1" "public" "not a url" None
            Assert.Equal(400, status)

            let! status, _ = sendInternal rig HttpMethod.Delete $"/internal/objects/{root}" None
            Assert.Equal(202, status)
            let! status, text = reply rig "u-1" "public" (objectIri root) None
            Assert.Equal(410, status)
            Assert.Equal("gone", jstr (at (json text) "error"))

            // nothing was published
            let! objects = Db.count rig.Db "SELECT count(*) FROM objects WHERE in_reply_to IS NOT NULL"
            Assert.Equal(0L, objects)
        }

    [<Fact>]
    member _.``FR-012 a reply must be public or unlisted: followers and direct are 400``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! status, _ = reply rig "u-1" "followers" (objectIri root) None
            Assert.Equal(400, status)
            let! status, _ = reply rig "u-1" "direct" (objectIri root) None
            Assert.Equal(400, status)
            let! status, _ = reply rig "u-1" "unlisted" (objectIri root) None
            Assert.Equal(201, status)
        }

    [<Fact>]
    member _.``FR-011 a reply to a post of ours: threaded in the root's context, addressed to the followers, answers 201 with the thread``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! status, text = reply rig "u-1" "public" (objectIri root) (Some "comment:1")
            Assert.Equal(201, status)
            let answer = json text
            let id = jstr (at answer "id")
            Assert.Equal(root, jstr (at answer "thread.objectId"))
            Assert.Equal(objectIri root, jstr (at answer "thread.id"))

            let! doc = fetchObject rig id
            Assert.Equal(objectIri root, jstr (at doc "inReplyTo"))
            Assert.Equal(Ids.contextId domain (Guid.Parse root), jstr (at doc "context"))
            Assert.Equal(Ids.contextId domain (Guid.Parse root), jstr (at doc "conversation"))
            Assert.Equal<string list>([ publicIri ], jsonStrings (at doc "to"))
            Assert.Equal<string list>([ followersOf "u-1" ], jsonStrings (at doc "cc"))
            // the parent is ours: nobody remote to mention
            Assert.Null(at doc "tag")
            Assert.Equal("Note", jstr (at doc "type"))
            Assert.Equal(Ids.actorId domain "u-1", jstr (at doc "attributedTo"))

            // stored: the parent, the root, and a record in the thread
            let! stored = Db.scalar rig.Db $"SELECT in_reply_to || ' ' || thread_root FROM objects WHERE id = '{id}'"
            Assert.Equal($"{objectIri root} {root}", stored :?> string)
            let! recorded = Db.count rig.Db $"SELECT count(*) FROM replies WHERE id = '{objectIri id}' AND local_object = '{id}' AND parent_object = '{root}' AND thread_root = '{root}' AND audience = 'public' AND state = 'live'"
            Assert.Equal(1L, recorded)

            // it is in the parent's collection
            let! page = Threads.repliesPage rig.Db (objectIri root) 1
            Assert.Equal<string list>([ objectIri id ], page.Items)

            // and delivered to alex's follower
            do! waitForPosts rig "/users/carl/inbox" 1
            let create = json (Encoding.UTF8.GetString (rig.Stub.Posts |> List.find (fun p -> p.Path = "/users/carl/inbox")).Body)
            Assert.Equal("Create", jstr (at create "type"))
            Assert.Equal(objectIri root, jstr (at create "object.inReplyTo"))
            Assert.Equal<string list>(jsonStrings (at doc "cc"), jsonStrings (at create "cc"))
        }

    [<Fact>]
    member _.``FR-011 a reply to a remote reply mentions its author, is addressed to them and delivered to their inbox``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            do! remoteReply rig "bob" "1" (objectIri root)
            let bob = rig.Stub.ActorId "bob"
            let! status, text = reply rig "u-1" "public" (noteId rig.Stub "bob" "1") None
            Assert.Equal(201, status)
            let answer = json text
            let id = jstr (at answer "id")
            // still the root's thread, however deep
            Assert.Equal(root, jstr (at answer "thread.objectId"))

            let! doc = fetchObject rig id
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at doc "inReplyTo"))
            Assert.Equal(Ids.contextId domain (Guid.Parse root), jstr (at doc "context"))
            Assert.Equal<string list>([ publicIri ], jsonStrings (at doc "to"))
            Assert.Equal<string list>([ followersOf "u-1"; bob ], jsonStrings (at doc "cc"))
            Assert.Equal("Mention", jstr (at doc "tag.0.type"))
            Assert.Equal(bob, jstr (at doc "tag.0.href"))
            Assert.Equal($"@bob@{rig.Stub.Authority}", jstr (at doc "tag.0.name"))

            // the recipients: the follower's inbox and bob's own, once each
            let! inboxes = inboxesOf rig id "Create"
            Assert.Equal<string list>([ rig.Stub.InboxUrl "bob"; rig.Stub.InboxUrl "carl" ] |> List.sort, inboxes)
            do! waitForPosts rig "/users/bob/inbox" 1
            do! waitForPosts rig "/users/carl/inbox" 1
            let create = json (Encoding.UTF8.GetString (rig.Stub.Posts |> List.find (fun p -> p.Path = "/users/bob/inbox")).Body)
            Assert.Equal("Create", jstr (at create "type"))
            Assert.Equal(objectIri id, jstr (at create "object.id"))
            Assert.Contains(bob, jsonStrings (at create "cc"))

            // the reply sits under bob's reply, not under the root
            let! under = Threads.repliesPage rig.Db (noteId rig.Stub "bob" "1") 1
            Assert.Equal<string list>([ objectIri id ], under.Items)
            let! underRoot = Threads.repliesPage rig.Db (objectIri root) 1
            Assert.Equal<string list>([ noteId rig.Stub "bob" "1" ], underRoot.Items)
        }

    [<Fact>]
    member _.``FR-011 an unlisted reply keeps the followers in to, and Public and the parent's author in cc``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            do! remoteReply rig "bob" "1" (objectIri root)
            let! status, text = reply rig "u-1" "unlisted" (noteId rig.Stub "bob" "1") None
            Assert.Equal(201, status)
            let! doc = fetchObject rig (jstr (at (json text) "id"))
            Assert.Equal<string list>([ followersOf "u-1" ], jsonStrings (at doc "to"))
            Assert.Equal<string list>([ publicIri; rig.Stub.ActorId "bob" ], jsonStrings (at doc "cc"))
            Assert.Equal("Mention", jstr (at doc "tag.0.type"))
        }

    [<Fact>]
    member _.``FR-011 a parent's author who also follows is sent each inbox once, and a shared inbox is not doubled``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            rig.Stub.SharedInbox <- Some(rig.Stub.BaseUrl + "/inbox")
            // carl follows through the shared inbox; bob follows too and has replied
            let carl = rig.Stub.ActorId "carl"
            do! Db.execute rig.Db $"UPDATE remote_actors SET shared_inbox = '{rig.Stub.BaseUrl}/inbox' WHERE id = '{carl}'"
            do! remoteReply rig "bob" "1" (objectIri root)
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" (Some(rig.Stub.BaseUrl + "/inbox"))
            let! created = replyOk rig "u-1" (noteId rig.Stub "bob" "1")
            let! inboxes = inboxesOf rig created "Create"
            // the shared inbox for both followers, bob's personal inbox for the mention: each once
            Assert.Equal<string list>([ rig.Stub.BaseUrl + "/inbox"; rig.Stub.InboxUrl "bob" ] |> List.sort, inboxes)
        }

    [<Fact>]
    member _.``FR-010 a reply to a reply of ours stays in the root's thread``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! first = replyOk rig "u-1" (objectIri root)
            let! status, text = reply rig "u-2" "public" (objectIri first) None
            Assert.Equal(201, status)
            let answer = json text
            Assert.Equal(root, jstr (at answer "thread.objectId"))
            let! doc = fetchObject rig (jstr (at answer "id"))
            Assert.Equal(Ids.contextId domain (Guid.Parse root), jstr (at doc "context"))
            Assert.Null(at doc "tag")
            let! under = Threads.repliesPage rig.Db (objectIri first) 1
            Assert.Equal(1, under.Items.Length)
        }

    [<Fact>]
    member _.``FR-010 a reply to a remote reply that was deleted is 410``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            do! remoteReply rig "bob" "1" (objectIri root)
            use client = new HttpClient()
            use! deleted = Signed.postText client rig.Stub "bob" (rig.Host.FederationUrl + "/inbox") (deleteNote rig.Stub "bob" "1" true)
            Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode)
            let! status, _ = reply rig "u-1" "public" (noteId rig.Stub "bob" "1") None
            Assert.Equal(410, status)
        }

    [<Fact>]
    member _.``FR-013 summary and sensitive are carried on a reply``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let body = publishBody "public" "<p>S2 E5 was great</p>"
            body["object"]["inReplyTo"] <- JsonValue.Create(objectIri root)
            body["object"]["summary"] <- JsonValue.Create "Spoilers: S2 E5"
            body["object"]["sensitive"] <- JsonValue.Create true
            let! status, text = publishRaw rig "u-1" (body.ToJsonString()) None
            Assert.Equal(201, status)
            let! doc = fetchObject rig (jstr (at (json text) "id"))
            Assert.Equal("Spoilers: S2 E5", jstr (at doc "summary"))
            Assert.True(boolOf (at doc "sensitive"))
        }

    [<Fact>]
    member _.``FR-010 a retried reply with the same key is one reply and one set of deliveries``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! s1, t1 = reply rig "u-1" "public" (objectIri root) (Some "comment:8812")
            let! s2, t2 = reply rig "u-1" "public" (objectIri root) (Some "comment:8812")
            Assert.Equal(201, s1)
            Assert.Equal(200, s2)
            Assert.Equal(jstr (at (json t1) "id"), jstr (at (json t2) "id"))
            // the retry also answers with the thread
            Assert.Equal(root, jstr (at (json t2) "thread.objectId"))
            let! replies = Db.count rig.Db "SELECT count(*) FROM replies WHERE local_object IS NOT NULL"
            Assert.Equal(1L, replies)
            let! deliveries = Db.count rig.Db "SELECT count(*) FROM deliveries"
            Assert.Equal(1L, deliveries)
            let! status, _ = publishRaw rig "u-1" (replyBody "public" (objectIri root) "<p>different</p>") (Some "comment:8812")
            Assert.Equal(409, status)
        }

    [<Fact>]
    member _.``FR-010 a post that is not a reply still answers with its own thread``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            let! status, text = publishRaw rig "u-1" ((publishBody "public" "<p>a post</p>").ToJsonString()) None
            Assert.Equal(201, status)
            let id = jstr (at (json text) "id")
            Assert.Equal(id, jstr (at (json text) "thread.objectId"))
            Assert.Equal(objectIri id, jstr (at (json text) "thread.id"))
            // a root has no parent and no root of its own
            let! parent = Db.scalar rig.Db $"SELECT in_reply_to IS NULL AND thread_root IS NULL FROM objects WHERE id = '{id}'"
            Assert.True(parent :?> bool)
            ignore root
        }

    [<Fact>]
    member _.``FR-011 PUT of a reply sends an Update to the same recipients and keeps it a reply with its mention``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            do! remoteReply rig "bob" "1" (objectIri root)
            let! id = replyOk rig "u-1" (noteId rig.Stub "bob" "1")
            do! waitForPosts rig "/users/bob/inbox" 1

            let! status, text = put rig id """{"object":{"type":"Note","content":"<p>edited</p>","summary":"Spoilers","sensitive":true}}"""
            Assert.Equal(200, status)
            Assert.Equal(root, jstr (at (json text) "thread.objectId"))

            let! created = inboxesOf rig id "Create"
            let! updated = inboxesOf rig id "Update"
            Assert.Equal<string list>(created, updated)

            let! doc = fetchObject rig id
            Assert.Equal("<p>edited</p>", jstr (at doc "content"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at doc "inReplyTo"))
            Assert.Equal(Ids.contextId domain (Guid.Parse root), jstr (at doc "context"))
            Assert.Equal("Mention", jstr (at doc "tag.0.type"))
            Assert.Equal<string list>([ followersOf "u-1"; rig.Stub.ActorId "bob" ], jsonStrings (at doc "cc"))
            Assert.False(isNull (at doc "updated"))

            do! waitForPosts rig "/users/bob/inbox" 2
            let update = rig.Stub.Posts |> List.filter (fun p -> p.Path = "/users/bob/inbox") |> List.map (fun p -> json (Encoding.UTF8.GetString p.Body)) |> List.find (fun a -> jstr (at a "type") = "Update")
            Assert.Equal<string list>(jsonStrings (at doc "cc"), jsonStrings (at update "cc"))

            // the reply cannot be moved to another parent
            let! status, _ = put rig id (replyBody "public" (objectIri root) "<p>moved</p>")
            Assert.Equal(400, status)
            // naming the same parent again is fine
            let! status, _ = put rig id (replyBody "public" (noteId rig.Stub "bob" "1") "<p>same parent</p>")
            Assert.Equal(200, status)
            // a post that is not a reply cannot become one
            let! post = publish rig "u-1" "public" "<p>a post</p>"
            let! status, _ = put rig post (replyBody "public" (objectIri root) "<p>now a reply</p>")
            Assert.Equal(400, status)
        }

    [<Fact>]
    member _.``FR-011 DELETE of a reply sends a Delete to the same recipients, takes it out of the collection, and it answers 410``() : Task =
        task {
            let! rig, root = start' pg
            use _ = disposeRig rig
            do! remoteReply rig "bob" "1" (objectIri root)
            let! id = replyOk rig "u-1" (noteId rig.Stub "bob" "1")
            let! page = Threads.repliesPage rig.Db (noteId rig.Stub "bob" "1") 1
            Assert.Equal<string list>([ objectIri id ], page.Items)

            let! status, _ = delete rig id
            Assert.Equal(202, status)
            let! created = inboxesOf rig id "Create"
            let! deleted = inboxesOf rig id "Delete"
            Assert.Equal<string list>(created, deleted)

            do! waitForPosts rig "/users/bob/inbox" 2
            let del = rig.Stub.Posts |> List.filter (fun p -> p.Path = "/users/bob/inbox") |> List.map (fun p -> json (Encoding.UTF8.GetString p.Body)) |> List.find (fun a -> jstr (at a "type") = "Delete")
            Assert.Equal(objectIri id, jstr (at del "object.id"))
            Assert.Contains(rig.Stub.ActorId "bob", jsonStrings (at del "cc"))

            let! after = Threads.repliesPage rig.Db (noteId rig.Stub "bob" "1") 1
            Assert.Empty after.Items
            use! gone = federationGet rig (objectPath id) []
            Assert.Equal(HttpStatusCode.Gone, gone.StatusCode)
            // nobody can reply to it any more
            let! status, _ = reply rig "u-2" "public" (objectIri id) None
            Assert.Equal(410, status)
            // a repeat answers like the first call
            let! status, _ = delete rig id
            Assert.Equal(202, status)
        }
