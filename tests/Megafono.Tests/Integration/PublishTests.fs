module Megafono.Tests.Integration.PublishTests

open System
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

let private count (rig: Rig) (sql: string) : Task<int64> = Db.count rig.Db sql

let private bodyWith (mutate: JsonObject -> unit) : string =
    let body = publishBody "public" "<p>hello</p>"
    mutate body
    body.ToJsonString()

let private objectOf (body: JsonObject) = (at body "object").AsObject()

type PublishTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-040 FR-041 publish answers 201 with the object id, its URL and the delivery status reference``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! status, text = publishRaw rig "u-1" ((publishBody "public" "<p>hello</p>").ToJsonString()) None
            Assert.Equal(201, status)
            let answer = json text
            let id = jstr ((at answer "id"))
            Assert.True(Guid.TryParseExact(id, "D", ref Guid.Empty))
            Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at answer "objectUrl")))
            Assert.Equal($"/internal/objects/{id}/deliveries", jstr ((at answer "deliveriesUrl")))

            // The copy is served to anyone with the id, the attribution and the addressing of a public note
            use! response = federationGet rig (objectPath id) []
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! served = response.Content.ReadAsStringAsync()
            let note = json served
            Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at note "id")))
            Assert.Equal("https://megafono.test/ap/actors/u-1", jstr ((at note "attributedTo")))
            Assert.Equal("<p>hello</p>", jstr ((at note "content")))
            Assert.Equal("https://www.w3.org/ns/activitystreams#Public", jstr ((at note "to.0")))
            Assert.Equal("https://megafono.test/ap/actors/u-1/followers", jstr ((at note "cc.0")))
        }

    [<Fact>]
    member _.``FR-040 the app's lookup must know the user``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! status, _ = publishRaw rig "nobody" ((publishBody "public" "x").ToJsonString()) None
            Assert.Equal(404, status)
            let! status, _ = publishRaw rig "bad id!" ((publishBody "public" "x").ToJsonString()) None
            Assert.Equal(404, status)
        }

    [<Fact>]
    member _.``FR-040 publish refuses an invalid audience, type, content and URLs with 400``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig

            let invalid =
                [ "audience mutuals", bodyWith (fun b -> b["audience"] <- JsonValue.Create "mutuals")
                  "audience direct", bodyWith (fun b -> b["audience"] <- JsonValue.Create "direct")
                  "audience missing", bodyWith (fun b -> b.Remove "audience" |> ignore)
                  "type", bodyWith (fun b -> (objectOf b)["type"] <- JsonValue.Create "Video")
                  "type missing", bodyWith (fun b -> (objectOf b).Remove "type" |> ignore)
                  "empty content", bodyWith (fun b -> (objectOf b)["content"] <- JsonValue.Create "  ")
                  "content not a string", bodyWith (fun b -> (objectOf b)["content"] <- JsonValue.Create 5)
                  "url not absolute", bodyWith (fun b -> (objectOf b)["url"] <- JsonValue.Create "/watch/1")
                  "url not http", bodyWith (fun b -> (objectOf b)["url"] <- JsonValue.Create "ftp://example.com/x")
                  "inReplyTo relative", bodyWith (fun b -> (objectOf b)["inReplyTo"] <- JsonValue.Create "some-note")
                  "attachment without url",
                  bodyWith (fun b -> (objectOf b)["attachment"] <- JsonNode.Parse """[{"type":"Image","mediaType":"image/png"}]""")
                  "attachment url relative",
                  bodyWith (fun b -> (objectOf b)["attachment"] <- JsonNode.Parse """[{"url":"/img/x.png"}]""")
                  "sensitive not a boolean", bodyWith (fun b -> (objectOf b)["sensitive"] <- JsonValue.Create "yes")
                  "extension prefix uppercase",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example/ns#","prefix":"App","data":{}}""")
                  "extension prefix starts with a digit",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example/ns#","prefix":"1app","data":{}}""")
                  "extension prefix too long",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example/ns#","prefix":"abcdefghijklmnopq","data":{}}""")
                  "extension namespace relative",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"ns","prefix":"app","data":{}}""")
                  "extension data missing",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example/ns#","prefix":"app"}""")
                  "extension data key with @",
                  bodyWith (fun b -> b["extensions"] <- JsonNode.Parse """{"namespace":"https://app.example/ns#","prefix":"app","data":{"@id":"x"}}""")
                  "not JSON", "{nope"
                  "not an object", "[1,2]" ]

            for label, body in invalid do
                let! status, text = publishRaw rig "u-1" body None
                Assert.True((status = 400), $"{label}: expected 400, got {status} {text}")
                Assert.Equal("invalid_request", jstr ((at (json text) "error")))

            let! objects = count rig "SELECT count(*) FROM objects"
            Assert.Equal(0L, objects)

            let! longKey = publishRaw rig "u-1" ((publishBody "public" "x").ToJsonString()) (Some(String('k', 201)))
            Assert.Equal(400, fst longKey)
            let! maxKey = publishRaw rig "u-1" ((publishBody "public" "x").ToJsonString()) (Some(String('k', 200)))
            Assert.Equal(201, fst maxKey)
        }

    [<Fact>]
    member _.``FR-040 publish refuses a request body over 256 KB``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! status, _ = publishRaw rig "u-1" ((publishBody "public" (String('x', 257 * 1024))).ToJsonString()) None
            Assert.Equal(413, status)
            let! ok, _ = publishRaw rig "u-1" ((publishBody "public" (String('x', 200 * 1024))).ToJsonString()) None
            Assert.Equal(201, ok)
        }

    [<Fact>]
    member _.``FR-040 attachments, a reply reference, a summary and sensitivity are carried on the note``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            // A reply must answer one of our objects (feature 002, FR-010)
            let! parent = publish rig "u-1" "public" "<p>the post replied to</p>"
            let parentUrl = $"https://megafono.test/ap/objects/{parent}"

            let body =
                bodyWith (fun b ->
                    let o = objectOf b
                    o["url"] <- JsonValue.Create "https://app.example.com/u/alex/watch/123"
                    o["inReplyTo"] <- JsonValue.Create parentUrl
                    o["summary"] <- JsonValue.Create "spoilers"
                    o["sensitive"] <- JsonValue.Create true

                    o["attachment"] <-
                        JsonNode.Parse """[{"type":"Image","mediaType":"image/jpeg","url":"https://app.example.com/img/frieren.jpg","name":"Frieren cover"}]""")

            let! status, text = publishRaw rig "u-1" body None
            Assert.Equal(201, status)
            use! response = federationGet rig (objectPath (jstr ((at (json text) "id")))) []
            let! served = response.Content.ReadAsStringAsync()
            let note = json served
            Assert.Equal("https://app.example.com/u/alex/watch/123", jstr ((at note "url")))
            Assert.Equal(parentUrl, jstr ((at note "inReplyTo")))
            Assert.Equal("spoilers", jstr ((at note "summary")))
            Assert.True(boolOf ((at note "sensitive")))
            Assert.Equal("Document", jstr ((at note "attachment.0.type")))
            Assert.Equal("image/jpeg", jstr ((at note "attachment.0.mediaType")))
            Assert.Equal("Frieren cover", jstr ((at note "attachment.0.name")))
        }

    [<Fact>]
    member _.``SC-005 FR-042 ten retries with one Idempotency-Key make one object and one delivery per inbox``() : Task =
        task {
            let! rig = start pg [ "alice"; "bob" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            let body = (publishBody "followers" "<p>retry me</p>").ToJsonString()

            // Ten at the same time: the key must serialize them, not race
            let! answers = Task.WhenAll [| for _ in 1..10 -> publishRaw rig "u-1" body (Some "watch-123-finished") |]
            let summary = answers |> Array.map (fun (s, t) -> $"{s}: {t}") |> String.concat "\n"
            Assert.True((answers |> Array.filter (fun (s, _) -> s = 201) |> Array.length) = 1, summary)
            Assert.True((answers |> Array.filter (fun (s, _) -> s = 200) |> Array.length) = 9, summary)
            Assert.Single(answers |> Array.map (fun (_, t) -> jstr ((at (json t) "id"))) |> Array.distinct) |> ignore

            // And ten more, one after the other, still the same answer
            for _ in 1..10 do
                let! status, text = publishRaw rig "u-1" body (Some "watch-123-finished")
                Assert.Equal(200, status)
                Assert.Equal(jstr ((at (json (snd answers[0])) "id")), jstr ((at (json text) "id")))

            let! objects = count rig "SELECT count(*) FROM objects"
            Assert.Equal(1L, objects)
            let! activities = count rig "SELECT count(*) FROM activities"
            Assert.Equal(1L, activities)
            // One delivery per distinct inbox: alice and bob have a personal inbox each
            let! deliveries = count rig "SELECT count(*) FROM deliveries"
            Assert.Equal(2L, deliveries)
        }

    [<Fact>]
    member _.``FR-042 the same key with a different body is a 409, the same body in another property order is a replay``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let first = """{"audience":"public","object":{"type":"Note","content":"<p>one</p>"}}"""
            let reordered = """{"object":{"content":"<p>one</p>","type":"Note"},"audience":"public"}"""
            let different = """{"audience":"public","object":{"type":"Note","content":"<p>two</p>"}}"""
            let otherAudience = """{"audience":"unlisted","object":{"type":"Note","content":"<p>one</p>"}}"""

            let! s1, t1 = publishRaw rig "u-1" first (Some "k")
            Assert.Equal(201, s1)
            let! s2, t2 = publishRaw rig "u-1" reordered (Some "k")
            Assert.Equal(200, s2)
            Assert.Equal(jstr ((at (json t1) "id")), jstr ((at (json t2) "id")))

            let! s3, t3 = publishRaw rig "u-1" different (Some "k")
            Assert.Equal(409, s3)
            Assert.Equal("conflict", jstr ((at (json t3) "error")))
            let! s4, _ = publishRaw rig "u-1" otherAudience (Some "k")
            Assert.Equal(409, s4)

            // The key is per user: another user may reuse it, and no key means a new object every time
            let! s5, _ = publishRaw rig "u-2" different (Some "k")
            Assert.Equal(201, s5)
            let! s6, _ = publishRaw rig "u-1" first None
            let! s7, _ = publishRaw rig "u-1" first None
            Assert.Equal((201, 201), (s6, s7))
            let! objects = count rig "SELECT count(*) FROM objects"
            Assert.Equal(4L, objects)
        }

    [<Fact>]
    member _.``FR-043 R-08 deliveries go to one shared inbox per server, personal inboxes otherwise, accepted followers only``() : Task =
        task {
            let! rig = start pg [ "alice"; "bob"; "carol"; "dave"; "erin" ]
            use _ = disposeRig rig
            let! other = StubRemote.start [ "frank"; "gina" ]
            use _ = { new IDisposable with member _.Dispose() = (other :> IAsyncDisposable).DisposeAsync().AsTask().Wait() }
            let shared = rig.Stub.BaseUrl + "/inbox"

            // Server one advertises a shared inbox: alice and bob share it. Server two has none (like GoToSocial).
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" (Some shared)
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" (Some shared)
            do! addFollower rig.Db "u-1" other "frank" "accepted" None
            do! addFollower rig.Db "u-1" other "gina" "accepted" None
            // Not recipients: pending, rejected, removed, and a follower of someone else
            do! addFollower rig.Db "u-1" rig.Stub "carol" "pending" (Some shared)
            do! addFollower rig.Db "u-1" rig.Stub "dave" "removed" None
            do! addFollower rig.Db "u-2" rig.Stub "erin" "accepted" None

            let! id = publish rig "u-1" "public" "<p>to the followers</p>"

            let! inboxes = strings rig.Db "SELECT inbox_url FROM deliveries ORDER BY inbox_url"
            let expected = [ shared; other.InboxUrl "frank"; other.InboxUrl "gina" ] |> List.sort
            Assert.Equal<string list>(expected, inboxes)

            do! waitUntil "every inbox received the Create" (TimeSpan.FromSeconds 15.0) (fun () -> rig.Stub.Posts.Length = 1 && other.Posts.Length = 2)
            Assert.Equal("/inbox", (rig.Stub.Posts |> List.exactlyOne).Path)
            Assert.Equal<string list>([ "/users/frank/inbox"; "/users/gina/inbox" ], other.Posts |> List.map (fun p -> p.Path) |> List.sort)

            // The same bytes everywhere: the stored Create activity
            let! stored = Db.scalar rig.Db $"SELECT body FROM activities WHERE object = '{id}'"
            Assert.All(rig.Stub.Posts @ other.Posts, fun p -> Assert.Equal<byte[]>(stored :?> byte[], p.Body))
            let create = json (Text.Encoding.UTF8.GetString(rig.Stub.Posts[0].Body))
            Assert.Equal("Create", jstr ((at create "type")))
            Assert.Equal($"https://megafono.test/ap/objects/{id}", jstr ((at create "object.id")))
        }

    [<Fact>]
    member _.``FR-046 extension data appears under its prefix with the namespace in the context``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None

            let body =
                bodyWith (fun b ->
                    b["extensions"] <-
                        JsonNode.Parse
                            """{"namespace":"https://app.example.com/ns#","prefix":"app","data":{"watch":{"malId":52991,"episodes":28}}}""")

            let! status, text = publishRaw rig "u-1" body None
            Assert.Equal(201, status)
            let id = jstr ((at (json text) "id"))

            use! response = federationGet rig (objectPath id) []
            let! served = response.Content.ReadAsStringAsync()
            let note = json served
            Assert.Equal("https://app.example.com/ns#", jstr ((at note "@context.1.app")))
            Assert.Equal(52991, intOf ((at note "app:watch.malId")))
            Assert.Equal(28, intOf ((at note "app:watch.episodes")))

            do! waitUntil "the Create arrived" (TimeSpan.FromSeconds 15.0) (fun () -> not rig.Stub.Posts.IsEmpty)
            let create = json (Text.Encoding.UTF8.GetString(rig.Stub.Posts[0].Body))
            Assert.Equal("https://app.example.com/ns#", jstr ((at create "@context.1.app")))
            Assert.Equal(28, intOf ((at create "object.app:watch.episodes")))
        }
