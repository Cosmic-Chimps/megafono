module Megafono.Tests.Integration.UpdateDeleteTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

let private posted (stub: StubRemote.StubRemote) : JsonNode list =
    stub.Posts |> List.map (fun p -> json (Encoding.UTF8.GetString p.Body))

let private ofType (activityType: string) (stub: StubRemote.StubRemote) : JsonNode list =
    posted stub |> List.filter (fun a -> jstr ((at a "type")) = activityType)

let private waitForPosts (stub: StubRemote.StubRemote) (activityType: string) (n: int) =
    waitUntil $"{n} {activityType} at the stub" (TimeSpan.FromSeconds 15.0) (fun () -> (ofType activityType stub).Length >= n)

let private followersUrl = "https://megafono.test/ap/actors/u-1/followers"
let private publicUrl = "https://www.w3.org/ns/activitystreams#Public"
let private strings (node: JsonNode) = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq

let private put (rig: Rig) (id: string) (body: string) = sendInternal rig HttpMethod.Put $"/internal/objects/{id}" (Some body)
let private delete (rig: Rig) (id: string) = sendInternal rig HttpMethod.Delete $"/internal/objects/{id}" None

type UpdateDeleteTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-044 PUT sends an Update with updated and the same audience, and the served copy changes``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let! id = publish rig "u-1" "followers" "<p>v1</p>"
            do! waitForPosts rig.Stub "Create" 1

            let! status, text = put rig id """{"object":{"type":"Note","content":"<p>v2</p>"}}"""
            Assert.Equal(200, status)
            Assert.Equal(id, jstr ((at (json text) "id")))
            do! waitForPosts rig.Stub "Update" 1

            let update = (ofType "Update" rig.Stub).Head
            Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at update "object.id")))
            Assert.Equal("<p>v2</p>", jstr ((at update "object.content")))
            Assert.False(isNull (at update "object.updated"), "the object carries `updated`")
            // The same audience: followers-only, nothing public
            Assert.Equal<string list>([ followersUrl ], strings (at update "to"))
            Assert.Equal<string list>([], strings (at update "cc"))
            Assert.Equal<string list>([ followersUrl ], strings (at update "object.to"))
            // The Create and the Update are different activities with ids of their own
            Assert.NotEqual<string>(jstr (at update "id"), jstr (at (ofType "Create" rig.Stub).Head "id"))

            use! served = signedGet rig rig.Stub "alice" (objectPath id)
            let! body = served.Content.ReadAsStringAsync()
            Assert.Equal("<p>v2</p>", jstr ((at (json body) "content")))
            Assert.False(isNull (at (json body) "updated"))
            // `published` is kept from the first version
            Assert.Equal(jstr (at (ofType "Create" rig.Stub).Head "object.published"), jstr ((at (json body) "published")))
        }

    [<Fact>]
    member _.``FR-044 PUT refuses a changed audience or type, and an unknown object``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "followers" "<p>v1</p>"

            let! status, _ = put rig id """{"audience":"public","object":{"type":"Note","content":"<p>v2</p>"}}"""
            Assert.Equal(400, status)
            let! status, _ = put rig id """{"object":{"type":"Article","content":"<p>v2</p>"}}"""
            Assert.Equal(400, status)
            let! status, _ = put rig id """{"audience":"followers","object":{"type":"Note","content":"<p>v2</p>"}}"""
            Assert.Equal(200, status)
            let! status, _ = put rig (string (Guid.NewGuid())) """{"object":{"type":"Note","content":"x"}}"""
            Assert.Equal(404, status)
            let! status, _ = put rig "nope" """{"object":{"type":"Note","content":"x"}}"""
            Assert.Equal(404, status)
        }

    [<Fact>]
    member _.``FR-044 DELETE sends Delete of a Tombstone to followers only, a repeat is 202 and sends nothing``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let! id = publish rig "u-1" "followers" "<p>secret</p>"
            do! waitForPosts rig.Stub "Create" 1

            let! status, _ = delete rig id
            Assert.Equal(202, status)
            do! waitForPosts rig.Stub "Delete" 1
            let del = (ofType "Delete" rig.Stub).Head
            Assert.Equal("Tombstone", jstr ((at del "object.type")))
            Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at del "object.id")))
            // Never to Public for a followers-only object
            Assert.Equal<string list>([ followersUrl ], strings (at del "to"))
            Assert.DoesNotContain(publicUrl, strings (at del "to") @ strings (at del "cc"))

            let! again, _ = delete rig id
            Assert.Equal(202, again)
            do! Task.Delay 800
            Assert.Single(ofType "Delete" rig.Stub) |> ignore
            let! deletes = Db.count rig.Db "SELECT count(*) FROM activities WHERE activity_type = 'Delete'"
            Assert.Equal(1L, deletes)

            // Principle II: a deleted followers-only object answers 410 with a Tombstone only to an accepted
            // follower; to anyone else it is 404, so its existence is not revealed (object and its Create)
            let! activity = createActivityId rig id

            for path in [ objectPath id; $"/ap/activities/{activity}" ] do
                use! unsigned = federationGet rig path []
                Assert.Equal(HttpStatusCode.NotFound, unsigned.StatusCode)
                use! signed = signedGet rig rig.Stub "alice" path
                Assert.Equal(HttpStatusCode.Gone, signed.StatusCode)
                let! body = signed.Content.ReadAsStringAsync()
                Assert.Equal("Tombstone", jstr ((at (json body) "type")))
                Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at (json body) "id")))

            // An edit after the delete is refused, and deleting what does not exist is 404
            let! status, text = put rig id """{"object":{"type":"Note","content":"back"}}"""
            Assert.Equal(410, status)
            Assert.Equal("gone", jstr ((at (json text) "error")))
            let! status, _ = delete rig (string (Guid.NewGuid()))
            Assert.Equal(404, status)
        }

    [<Fact>]
    member _.``FR-044 a public object's Delete is addressed to Public``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let! id = publish rig "u-1" "public" "<p>hello</p>"
            let! status, _ = delete rig id
            Assert.Equal(202, status)
            do! waitForPosts rig.Stub "Delete" 1
            Assert.Equal<string list>([ publicUrl ], strings (at (ofType "Delete" rig.Stub).Head "to"))
            // A deleted public object answers 410 to anyone
            use! unsigned = federationGet rig (objectPath id) []
            Assert.Equal(HttpStatusCode.Gone, unsigned.StatusCode)
        }
