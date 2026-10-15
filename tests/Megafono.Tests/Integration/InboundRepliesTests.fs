module Megafono.Tests.Integration.InboundRepliesTests

open System
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Domain
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

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

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private rootIri (objectId: string) = Ids.objectId "megafono.test" (Guid.Parse objectId)

let private repliesCount (rig: Rig) = Db.count rig.Db "SELECT count(*) FROM replies"

/// A thread root published by u-1 (alex) and the rig around it
let private start (pg: PostgresFixture) : Task<Rig * string> =
    task {
        let! rig = Publishing.start pg [ "bob"; "carl"; "mallory" ]
        let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
        return rig, root
    }

type InboundRepliesTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-001 a reply to our object reaches the thread owner once, with every field of the contract``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            note["content"] <- JsonValue.Create "<p>that ending!</p>"
            note["summary"] <- JsonValue.Create "Spoilers: S2 E5"
            note["sensitive"] <- JsonValue.Create true
            note["contentMap"] <- JsonNode.Parse """{"en":"<p>that ending!</p>"}"""
            note["attachment"] <- JsonNode.Parse """[{"type":"Document","mediaType":"image/png","url":"https://files.example/a.png","name":"alt text"}]"""

            let! status = send rig "bob" (createNote rig.Stub "bob" "1" note)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1

            let e = (events rig "reply.received").Head
            Assert.True e.SignatureValid
            Assert.Equal("u-1", jstr (at e.Json "user.id"))
            Assert.Equal("alex", jstr (at e.Json "user.handle"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
            Assert.Equal($"@bob@{rig.Stub.Authority}", jstr (at e.Json "actor.handle"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
            Assert.Equal($"{rig.Stub.BaseUrl}/@bob/1", jstr (at e.Json "object.url"))
            Assert.Equal("Note", jstr (at e.Json "object.type"))
            Assert.Equal("<p>that ending!</p>", jstr (at e.Json "object.content"))
            Assert.Equal("Spoilers: S2 E5", jstr (at e.Json "object.summary"))
            Assert.True(boolOf (at e.Json "object.sensitive"))
            Assert.Equal("en", jstr (at e.Json "object.language"))
            Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))
            Assert.Null(at e.Json "object.updated")
            Assert.Equal("public", jstr (at e.Json "object.audience"))
            Assert.Equal(rootIri root, jstr (at e.Json "object.inReplyTo"))
            Assert.True(boolOf (at e.Json "object.parent.local"))
            Assert.Equal(root, jstr (at e.Json "object.parent.objectId"))
            Assert.Equal("u-1", jstr (at e.Json "object.parent.userId"))
            Assert.Equal(root, jstr (at e.Json "object.thread.objectId"))
            Assert.Equal(rootIri root, jstr (at e.Json "object.thread.id"))
            Assert.Equal("Document", jstr (at e.Json "object.attachment.0.type"))
            Assert.Equal("image/png", jstr (at e.Json "object.attachment.0.mediaType"))
            Assert.Equal("https://files.example/a.png", jstr (at e.Json "object.attachment.0.url"))
            Assert.Equal("alt text", jstr (at e.Json "object.attachment.0.name"))
            Assert.Null(at e.Json "extensions")

            let! count = repliesCount rig
            Assert.Equal(1L, count)
        }

    [<Fact>]
    member _.``FR-001 a reply to a reply stays in the thread, owned by the root's author, with parent.local false``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" (Signed.note rig.Stub "bob" "1" (rootIri root)))
            do! waitForEvents rig "reply.received" 1

            let! status = send rig "carl" (createNote rig.Stub "carl" "2" (Signed.note rig.Stub "carl" "2" (noteId rig.Stub "bob" "1")))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 2

            let e = (events rig "reply.received")[1]
            Assert.Equal("u-1", jstr (at e.Json "user.id"))
            Assert.Equal(rig.Stub.ActorId "carl", jstr (at e.Json "actor.id"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.inReplyTo"))
            Assert.False(boolOf (at e.Json "object.parent.local"))
            Assert.Null(at e.Json "object.parent.objectId")
            Assert.Null(at e.Json "object.parent.userId")
            Assert.Equal(root, jstr (at e.Json "object.thread.objectId"))
            Assert.Equal(2L, (at e.Json "seq").GetValue<int64>())
        }

    [<Fact>]
    member _.``FR-002 a reply to something we never published or to a deleted post is acknowledged and dropped``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let! unknownStatus = send rig "bob" (createNote rig.Stub "bob" "1" (Signed.note rig.Stub "bob" "1" "https://other.example/users/x/statuses/1"))
            Assert.Equal(HttpStatusCode.Accepted, unknownStatus)
            let! missingStatus = send rig "bob" (createNote rig.Stub "bob" "2" (Signed.note rig.Stub "bob" "2" (rootIri (string (Guid.NewGuid())))))
            Assert.Equal(HttpStatusCode.Accepted, missingStatus)

            let! deleteStatus, _ = sendInternal rig HttpMethod.Delete $"/internal/objects/{root}" None
            Assert.Equal(202, deleteStatus)
            let! goneStatus = send rig "bob" (createNote rig.Stub "bob" "3" (Signed.note rig.Stub "bob" "3" (rootIri root)))
            Assert.Equal(HttpStatusCode.Accepted, goneStatus)

            do! settle ()
            Assert.Empty(events rig "reply.received")
            let! count = repliesCount rig
            Assert.Equal(0L, count)
        }

    [<Fact>]
    member _.``FR-006 a reply attributed to someone other than the signer is dropped``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            // mallory signs, the note says bob wrote it
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            let activity = JsonNode.Parse(createNote rig.Stub "bob" "1" note)
            activity["actor"] <- JsonValue.Create(rig.Stub.ActorId "mallory")
            let! status = send rig "mallory" (activity.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)

            // and the other way: the activity is the signer's, the note someone else's
            let! forged = send rig "mallory" (createNote rig.Stub "mallory" "2" (Signed.note rig.Stub "bob" "2" (rootIri root)))
            Assert.Equal(HttpStatusCode.Accepted, forged)

            do! settle ()
            Assert.Empty(events rig "reply.received")
            let! count = repliesCount rig
            Assert.Equal(0L, count)
        }

    [<Fact>]
    member _.``FR-006 an unsigned or badly signed reply is refused with 401 and the app hears nothing``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let body = Text.Encoding.UTF8.GetBytes(createNote rig.Stub "bob" "1" (Signed.note rig.Stub "bob" "1" (rootIri root)))
            use client = new HttpClient()
            use! unsigned = postRaw client (inboxUrl rig) body []
            Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode)

            // signed for another body: the digest no longer matches
            let headers = rig.Stub.Sign("bob", "POST", inboxUrl rig, Some(Text.Encoding.UTF8.GetBytes "{}"))
            use! tampered = postRaw client (inboxUrl rig) body headers
            Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode)

            do! settle ()
            Assert.Empty(events rig "reply.received")
        }

    [<Fact>]
    member _.``FR-006 the same reply delivered twice is one event, even under another activity id``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            let body = createNote rig.Stub "bob" "1" note
            let! first = send rig "bob" body
            let! second = send rig "bob" body
            Assert.Equal(HttpStatusCode.Accepted, first)
            Assert.Equal(HttpStatusCode.Accepted, second)

            // a retry that carries a new activity id for the same Note
            let again = JsonNode.Parse body
            again["id"] <- JsonValue.Create (noteId rig.Stub "bob" "1" + "/activity-2")
            let! third = send rig "bob" (again.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, third)

            do! waitForEvents rig "reply.received" 1
            do! settle ()
            Assert.Single(events rig "reply.received") |> ignore
            let! count = repliesCount rig
            Assert.Equal(1L, count)
            let! processed = Db.count rig.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(2L, processed)
        }

    [<Fact>]
    member _.``FR-001 a followers-only reply reaches the app with its audience, and a direct one too``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let followersOnly = Signed.note rig.Stub "bob" "1" (rootIri root)
            setAddressing followersOnly [ (rig.Stub.ActorId "bob" + "/followers") ] [ Signed.ourActor "u-1" ]
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" followersOnly)

            let direct = Signed.note rig.Stub "bob" "2" (rootIri root)
            setAddressing direct [ Signed.ourActor "u-1" ] []
            let! _ = send rig "bob" (createNote rig.Stub "bob" "2" direct)

            let unlisted = Signed.note rig.Stub "bob" "3" (rootIri root)
            setAddressing unlisted [ (rig.Stub.ActorId "bob" + "/followers") ] [ Signed.publicIri ]
            let! _ = send rig "bob" (createNote rig.Stub "bob" "3" unlisted)

            do! waitForEvents rig "reply.received" 3
            let audiences = events rig "reply.received" |> List.map (fun e -> jstr (at e.Json "object.id"), jstr (at e.Json "object.audience")) |> Map.ofList
            Assert.Equal("followers", audiences[noteId rig.Stub "bob" "1"])
            Assert.Equal("direct", audiences[noteId rig.Stub "bob" "2"])
            Assert.Equal("unlisted", audiences[noteId rig.Stub "bob" "3"])
        }

    [<Fact>]
    member _.``FR-001 the content is sanitized, the summary is text, and a missing sensitive is false``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            note["content"] <- JsonValue.Create """<p onclick="x()">hi <script>alert(1)</script><a href="javascript:evil()">bad</a> <a href="https://ok.example/">ok</a><img src="https://x/y.png" onerror="z()"></p>"""
            note["summary"] <- JsonValue.Create "<p>Spoilers: <b>S2</b> E5</p>"
            note.Remove "sensitive" |> ignore
            let! _ = send rig "bob" (createNote rig.Stub "bob" "1" note)
            do! waitForEvents rig "reply.received" 1

            let e = (events rig "reply.received").Head
            let content = jstr (at e.Json "object.content")
            Assert.DoesNotContain("script", content)
            Assert.DoesNotContain("alert", content)
            Assert.DoesNotContain("javascript", content)
            Assert.DoesNotContain("onclick", content)
            Assert.DoesNotContain("onerror", content)
            Assert.DoesNotContain("<img", content)
            Assert.Contains("https://ok.example/", content)
            Assert.Equal("Spoilers: S2 E5", jstr (at e.Json "object.summary"))
            Assert.False(boolOf (at e.Json "object.sensitive"))
            Assert.Null(at e.Json "object.language")
            Assert.Equal(0, (at e.Json "object.attachment").AsArray().Count)
        }

    [<Fact>]
    member _.``FR-001 data under the app's own namespace is passed back in extensions, whatever prefix the reply uses``() : Task =
        task {
            let! rig, _ = start pg
            use _ = disposeRig rig
            // a root that carries the app's extension
            let body = publishBody "public" "<p>Episode 6</p>"
            body["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example.com/ns#","prefix":"app","data":{"episode":6}}"""
            let! status, text = publishRaw rig "u-1" (body.ToJsonString()) None
            Assert.Equal(201, status)
            let root = jstr ((JsonNode.Parse text)["id"])

            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            note["x:rating"] <- JsonNode.Parse """{"stars":5}"""
            note["other:ignored"] <- JsonValue.Create "nope"
            let context = JsonNode.Parse """["https://www.w3.org/ns/activitystreams",{"x":"https://app.example.com/ns#","other":"https://elsewhere.example/ns#"}]"""
            let! _ = send rig "bob" (createNoteWithContext rig.Stub "bob" "1" note context)
            do! waitForEvents rig "reply.received" 1

            let e = (events rig "reply.received").Head
            Assert.Equal("https://app.example.com/ns#", jstr (at e.Json "extensions.namespace"))
            Assert.Equal("x", jstr (at e.Json "extensions.prefix"))
            Assert.Equal(5, intOf (at e.Json "extensions.data.rating.stars"))
            // only the app's namespace, never another one
            Assert.Null(at e.Json "extensions.data.ignored")

            // a reply that does not carry it has none
            let plain = Signed.note rig.Stub "bob" "2" (rootIri root)
            let! _ = send rig "bob" (createNote rig.Stub "bob" "2" plain)
            do! waitForEvents rig "reply.received" 2
            let second = (events rig "reply.received")[1]
            Assert.Null(at second.Json "extensions")
        }

    [<Fact>]
    member _.``FR-006 a Create that names the Note by id is fetched from its origin, signed``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            note["content"] <- JsonValue.Create "<p>from the origin</p>"
            rig.Stub.ServeNote("bob", "1", note)

            // the activity embeds nothing but the id
            let activity = JsonNode.Parse(createNote rig.Stub "bob" "1" note)
            activity["object"] <- JsonValue.Create(noteId rig.Stub "bob" "1")
            let! status = send rig "bob" (activity.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1
            Assert.Equal("<p>from the origin</p>", jstr (at (events rig "reply.received").Head.Json "object.content"))

            let fetched = rig.Stub.Gets |> List.filter (fun g -> g.Path = "/users/bob/statuses/1")
            Assert.Single fetched |> ignore
            Assert.True(fetched.Head.Headers |> List.exists (fun (n, _) -> n.Equals("Signature", StringComparison.OrdinalIgnoreCase)), "the fetch is signed")
        }

    [<Fact>]
    member _.``FR-006 an embedded Note on another host than the signer's is not trusted: it is fetched from there, or dropped``() : Task =
        task {
            let! rig, root = start pg
            use _ = disposeRig rig
            // bob signs; the embedded Note claims an id on a host that is not bob's, which serves nothing
            let note = Signed.note rig.Stub "bob" "1" (rootIri root)
            note["id"] <- JsonValue.Create "http://127.0.0.1:1/users/bob/statuses/1"
            let! status = send rig "bob" (createNote rig.Stub "bob" "1" note)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(events rig "reply.received")
            let! count = repliesCount rig
            Assert.Equal(0L, count)
        }
