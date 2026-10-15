module Megafono.Tests.Integration.InboundReplyChangesTests

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

let private settle () = Task.Delay 700

let private rootIri (objectId: string) = Ids.objectId "megafono.test" (Guid.Parse objectId)

let private replyState (rig: Rig) (suffix: string) =
    let id = noteId rig.Stub "bob" suffix
    Db.scalar rig.Db $"SELECT state FROM replies WHERE id = '{id}'"

/// A thread root and one reply from bob already received
let private start (pg: PostgresFixture) : Task<Rig * string * JsonObject> =
    task {
        let! rig = Publishing.start pg [ "bob"; "carl" ]
        let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
        let note = Signed.note rig.Stub "bob" "1" (rootIri root)
        let! _ = send rig "bob" (createNote rig.Stub "bob" "1" note)
        do! waitForEvents rig "reply.received" 1
        return rig, root, note
    }

type InboundReplyChangesTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-004 an Update of a received reply by its author gives reply.updated with the new content``() : Task =
        task {
            let! rig, root, note = start pg
            use _ = disposeRig rig
            note["content"] <- JsonValue.Create "<p>that ending, again!</p>"
            note["summary"] <- JsonValue.Create "Spoilers"
            note["sensitive"] <- JsonValue.Create true
            note["updated"] <- JsonValue.Create "2026-10-12T18:05:00Z"

            let! status = send rig "bob" (updateNote rig.Stub "bob" "1" "1" note)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.updated" 1

            let e = (events rig "reply.updated").Head
            Assert.Equal("u-1", jstr (at e.Json "user.id"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
            Assert.Equal("<p>that ending, again!</p>", jstr (at e.Json "object.content"))
            Assert.Equal("Spoilers", jstr (at e.Json "object.summary"))
            Assert.True(boolOf (at e.Json "object.sensitive"))
            Assert.Equal("2026-10-12T18:05:00Z", jstr (at e.Json "object.updated"))
            // the original time, the audience and the parent are the reply's, not the edit's
            Assert.Equal("2026-10-12T17:59:58Z", jstr (at e.Json "object.published"))
            Assert.Equal("public", jstr (at e.Json "object.audience"))
            Assert.Equal(root, jstr (at e.Json "object.thread.objectId"))
            Assert.Equal(root, jstr (at e.Json "object.parent.objectId"))
            // seq continues the user's own sequence
            Assert.Equal(2L, (at e.Json "seq").GetValue<int64>())

            // a second edit is a second event; the same Update twice is one
            note["content"] <- JsonValue.Create "<p>third</p>"
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "2" note)
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "2" note)
            do! waitForEvents rig "reply.updated" 2
            do! settle ()
            Assert.Equal(2, (events rig "reply.updated").Length)
        }

    [<Fact>]
    member _.``FR-004 two edits under one activity id (GoToSocial's way) are two events, the same edit twice is one``() : Task =
        task {
            let! rig, _, note = start pg
            use _ = disposeRig rig
            let edit (content: string) (updated: string) =
                note["content"] <- JsonValue.Create content
                note["updated"] <- JsonValue.Create updated
                // the same activity id every time, as `…/activity#Update` is
                updateNote rig.Stub "bob" "1" "same" note

            let first = edit "<p>one</p>" "2026-10-12T18:05:00Z"
            let second = edit "<p>two</p>" "2026-10-12T18:06:00Z"
            let! _ = send rig "bob" first
            let! _ = send rig "bob" first
            let! _ = send rig "bob" second
            do! waitForEvents rig "reply.updated" 2
            do! settle ()
            let contents = events rig "reply.updated" |> List.map (fun e -> jstr (at e.Json "object.content"))
            Assert.Equal<string list>([ "<p>one</p>"; "<p>two</p>" ], contents)
        }

    [<Fact>]
    member _.``FR-004 an Update by someone else, of an unknown reply, or of a deleted one is ignored``() : Task =
        task {
            let! rig, root, note = start pg
            use _ = disposeRig rig
            note["content"] <- JsonValue.Create "<p>hijacked</p>"

            // carl signs an Update of bob's reply
            let hijack = JsonNode.Parse(updateNote rig.Stub "bob" "1" "x" note)
            hijack["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let! a = send rig "carl" (hijack.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, a)

            // bob edits a reply nobody recorded
            let stranger = Signed.note rig.Stub "bob" "77" (rootIri root)
            let! b = send rig "bob" (updateNote rig.Stub "bob" "77" "1" stranger)
            Assert.Equal(HttpStatusCode.Accepted, b)

            // a reply that was deleted can no longer be edited
            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            do! waitForEvents rig "object.deleted" 1
            let! c = send rig "bob" (updateNote rig.Stub "bob" "1" "late" note)
            Assert.Equal(HttpStatusCode.Accepted, c)

            do! settle ()
            Assert.Empty(events rig "reply.updated")
        }

    [<Fact>]
    member _.``FR-005 a Delete with a Tombstone by the author gives object.deleted once and marks the reply deleted``() : Task =
        task {
            let! rig, root, _ = start pg
            use _ = disposeRig rig

            let! status = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "object.deleted" 1

            let e = (events rig "object.deleted").Head
            Assert.Equal("u-1", jstr (at e.Json "user.id"))
            Assert.Equal(noteId rig.Stub "bob" "1", jstr (at e.Json "object.id"))
            Assert.Equal(root, jstr (at e.Json "object.thread.objectId"))
            Assert.Equal(rootIri root, jstr (at e.Json "object.thread.id"))
            Assert.Equal(rig.Stub.ActorId "bob", jstr (at e.Json "actor.id"))
            let! state = replyState rig "1"
            Assert.Equal("deleted", state :?> string)

            // the same Delete again, and a Delete under another activity id: nothing more
            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" true)
            let again = JsonNode.Parse(deleteNote rig.Stub "bob" "1" false)
            again["id"] <- JsonValue.Create (noteId rig.Stub "bob" "1" + "#delete-2")
            let! _ = send rig "bob" (again.ToJsonString())
            do! settle ()
            Assert.Single(events rig "object.deleted") |> ignore
        }

    [<Fact>]
    member _.``FR-005 a Delete by the bare id works too, and a deleted reply no longer takes replies``() : Task =
        task {
            let! rig, _, _ = start pg
            use _ = disposeRig rig
            let! _ = send rig "bob" (deleteNote rig.Stub "bob" "1" false)
            do! waitForEvents rig "object.deleted" 1

            // a reply to the deleted reply is dropped
            let! _ = send rig "carl" (createNote rig.Stub "carl" "2" (Signed.note rig.Stub "carl" "2" (noteId rig.Stub "bob" "1")))
            do! settle ()
            Assert.Single(events rig "reply.received") |> ignore
        }

    [<Fact>]
    member _.``FR-005 a Delete by someone else, or of a reply we never recorded, does nothing``() : Task =
        task {
            let! rig, _, note = start pg
            use _ = disposeRig rig
            let forged = JsonNode.Parse(deleteNote rig.Stub "bob" "1" true)
            forged["actor"] <- JsonValue.Create(rig.Stub.ActorId "carl")
            let! a = send rig "carl" (forged.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, a)
            let! b = send rig "bob" (deleteNote rig.Stub "bob" "404" true)
            Assert.Equal(HttpStatusCode.Accepted, b)
            // an account deletion (the object is the actor) is not a reply deletion
            let account = JsonNode.Parse(deleteNote rig.Stub "bob" "1" false)
            account["id"] <- JsonValue.Create (rig.Stub.ActorId "bob" + "#delete")
            account["object"] <- JsonValue.Create(rig.Stub.ActorId "bob")
            let! c = send rig "bob" (account.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, c)

            do! settle ()
            Assert.Empty(events rig "object.deleted")
            let! state = replyState rig "1"
            Assert.Equal("live", state :?> string)

            // the reply is still editable by its author
            note["content"] <- JsonValue.Create "<p>still here</p>"
            let! _ = send rig "bob" (updateNote rig.Stub "bob" "1" "1" note)
            do! waitForEvents rig "reply.updated" 1
        }

    [<Fact>]
    member _.``FR-004 an Update{Person} still reaches the profile handler, not the reply one``() : Task =
        task {
            let! rig, _, _ = start pg
            use _ = disposeRig rig
            let person = JsonObject()
            person["@context"] <- JsonValue.Create "https://www.w3.org/ns/activitystreams"
            person["id"] <- JsonValue.Create (rig.Stub.ActorId "bob" + "#updates/1")
            person["type"] <- JsonValue.Create "Update"
            person["actor"] <- JsonValue.Create(rig.Stub.ActorId "bob")
            person["object"] <- JsonValue.Create(rig.Stub.ActorId "bob")
            let! status = send rig "bob" (person.ToJsonString())
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            Assert.Empty(events rig "reply.updated")
        }
