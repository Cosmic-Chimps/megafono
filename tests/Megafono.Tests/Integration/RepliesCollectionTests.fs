/// User Story 3 (T015): the `replies` collection, its pages and the context collection (FR-020 to FR-023,
/// SC-004). Replies are recorded straight in the tables: how they arrive is the inbound tests' business.
module Megafono.Tests.Integration.RepliesCollectionTests

open System
open System.Net
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private iri (id: string) = $"https://{domain}/ap/objects/{id}"

let private getJson (rig: Rig) (path: string) : Task<int * JsonNode> =
    task {
        use! response = federationGet rig path []
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, (if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text)
    }

let private remoteActor (db: NpgsqlDataSource) : Task =
    Db.execute
        db
        "INSERT INTO remote_actors (id, host, actor_type, inbox, key_id, public_key_pem) VALUES ('https://r.example/users/bob', 'r.example', 'Person', 'https://r.example/i', 'https://r.example/users/bob#k', 'p') ON CONFLICT DO NOTHING"

/// `count` remote replies to `parent` (an IRI) in the thread of `root`, with the audience and state given
let private remoteReplies (db: NpgsqlDataSource) (root: string) (parent: string) (prefix: string) (count: int) (audience: string) (state: string) : Task =
    Db.execute
        db
        $"""INSERT INTO replies (id, remote_actor, parent, thread_root, audience, state, published_at)
            SELECT 'https://r.example/users/bob/statuses/{prefix}' || n, 'https://r.example/users/bob', '{parent}', '{root}', '{audience}', '{state}',
                   timestamptz '2026-10-12 12:00:00+00' + n * interval '1 second'
            FROM generate_series(1, {count}) AS n"""

/// A reply of the app: its own object (a child of `root`) and its row in `replies`; returns its IRI
// Each seeded local reply gets its own, later published_at: two replies in the same instant would be ordered by
// their random ids, and the collection's order (local first, then by time) would look flaky
let mutable private localSeq = 0

let private localReply (db: NpgsqlDataSource) (root: string) (parent: string) (audience: string) : Task<string> =
    task {
        let id = Guid.NewGuid()
        let second = System.Threading.Interlocked.Increment(&localSeq)
        let replyIri = iri (string id)

        do!
            Db.execute
                db
                $"""INSERT INTO objects (id, local_actor, audience, object_type, document, in_reply_to, thread_root)
                    VALUES ('{id}', 'u-2', '{(if audience = "direct" then "followers" else audience)}', 'Note', '{{}}', '{parent}', '{root}');
                    INSERT INTO replies (id, local_object, parent, parent_object, thread_root, audience, state, published_at)
                    VALUES ('{replyIri}', '{id}', '{parent}', NULL, '{root}', '{audience}', 'live', timestamptz '2026-10-13 12:00:00+00' + make_interval(secs => {second}))"""

        return replyIri
    }

let private total (node: JsonNode) : int = node["totalItems"].GetValue<int>()
let private items (node: JsonNode) : string list = [ for i in (node["items"] :?> JsonArray) -> jstr i ]

type RepliesCollectionTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-020 FR-023 a published object carries replies, context, conversation and interactionPolicy``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>hello</p>"
            let! status, doc = getJson rig (objectPath id)
            Assert.Equal(200, status)
            Assert.Equal(iri id, jstr doc["id"])
            let replies = doc["replies"]
            Assert.Equal("Collection", jstr replies["type"])
            Assert.Equal(iri id + "/replies", jstr replies["id"])
            Assert.Equal(0, total replies)
            let first = replies["first"]
            Assert.Equal("CollectionPage", jstr first["type"])
            Assert.Equal(iri id + "/replies", jstr first["partOf"])
            Assert.Empty(items first)
            Assert.Null first["next"]
            Assert.Equal($"https://{domain}/ap/contexts/{id}", jstr doc["context"])
            Assert.Equal($"https://{domain}/ap/contexts/{id}", jstr doc["conversation"])
            Assert.Equal("https://www.w3.org/ns/activitystreams#Public", jstr (at doc "interactionPolicy.canReply.automaticApproval.0"))
        }

    [<Fact>]
    member _.``FR-020 replies is computed when served, never stored, and objects stored before 002 get the fields too``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>old</p>"
            // As 002's predecessor stored it: no thread fields and the old @context
            do!
                Db.execute
                    rig.Db
                    $"UPDATE objects SET document = (document - 'context' - 'conversation' - 'interactionPolicy' - 'replies') || '{{\"@context\":\"https://www.w3.org/ns/activitystreams\"}}'::jsonb WHERE id = '{id}'"

            do! remoteActor rig.Db
            do! remoteReplies rig.Db id (iri id) "a" 2 "public" "live"
            let! _, doc = getJson rig (objectPath id)
            Assert.Equal(2, total doc["replies"])
            Assert.Equal(2, (items (doc["replies"]["first"])).Length)
            Assert.Equal($"https://{domain}/ap/contexts/{id}", jstr doc["context"])
            Assert.NotNull doc["interactionPolicy"]
            // The terms `conversation` and `interactionPolicy` are declared in the served @context
            Assert.Contains("interactionPolicy", doc["@context"].ToJsonString())
            Assert.Equal(iri id, jstr doc["id"])
            let! stored = Db.scalar rig.Db $"SELECT document ? 'replies' FROM objects WHERE id = '{id}'"
            Assert.False(stored :?> bool)
        }

    [<Fact>]
    member _.``FR-020 SC-003 120 replies make three pages of at most 50, the app's own first, `next` only when more follow``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>popular</p>"
            do! remoteActor rig.Db
            do! remoteReplies rig.Db id (iri id) "r" 120 "public" "live"
            let! l1 = localReply rig.Db id (iri id) "public"
            let! l2 = localReply rig.Db id (iri id) "unlisted"

            let! _, doc = getJson rig (objectPath id)
            Assert.Equal(122, total doc["replies"])
            let first = doc["replies"]["first"]
            Assert.Equal(50, (items first).Length)
            Assert.Equal<string list>([ l1; l2 ], items first |> List.truncate 2)
            Assert.Equal(iri id + "/replies?page=2", jstr first["next"])

            let! s, whole = getJson rig $"{objectPath id}/replies"
            Assert.Equal(200, s)
            Assert.Equal("Collection", jstr whole["type"])
            Assert.Equal(122, total whole)
            Assert.Equal<string list>(items first, items whole["first"])

            let! _, p1 = getJson rig $"{objectPath id}/replies?page=1"
            Assert.Equal<string list>(items first, items p1)
            let! _, p2 = getJson rig $"{objectPath id}/replies?page=2"
            Assert.Equal("CollectionPage", jstr p2["type"])
            Assert.Equal(iri id + "/replies?page=2", jstr p2["id"])
            Assert.Equal(50, (items p2).Length)
            Assert.Equal(iri id + "/replies?page=3", jstr p2["next"])
            let! _, p3 = getJson rig $"{objectPath id}/replies?page=3"
            Assert.Equal(22, (items p3).Length)
            Assert.Null p3["next"]

            let all = items p1 @ items p2 @ items p3
            Assert.Equal(122, all.Length)
            Assert.Equal(122, (List.distinct all).Length)

            for bad in [ "page=0"; "page=-1"; "page=x"; "page=2&x=1"; "other=1" ] do
                let! status, _ = getJson rig $"{objectPath id}/replies?{bad}"
                Assert.Equal(404, status)
        }

    [<Fact>]
    member _.``SC-004 followers-only, direct and deleted replies are never listed``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>careful</p>"
            do! remoteActor rig.Db
            do! remoteReplies rig.Db id (iri id) "pub" 1 "public" "live"
            do! remoteReplies rig.Db id (iri id) "fol" 3 "followers" "live"
            do! remoteReplies rig.Db id (iri id) "dir" 3 "direct" "live"
            do! remoteReplies rig.Db id (iri id) "del" 3 "public" "deleted"
            let! _ = localReply rig.Db id (iri id) "followers"
            let! _ = localReply rig.Db id (iri id) "direct"

            let! _, replies = getJson rig $"{objectPath id}/replies"
            Assert.Equal(1, total replies)
            Assert.Equal<string list>([ "https://r.example/users/bob/statuses/pub1" ], items replies["first"])

            let! _, context = getJson rig $"/ap/contexts/{id}"
            Assert.Equal<string list>([ iri id; "https://r.example/users/bob/statuses/pub1" ], items context["first"])
        }

    [<Fact>]
    member _.``FR-021 a followers-only object's collections are 404 to the unauthorized and served to an accepted follower``() : Task =
        task {
            let! rig = start pg [] |> fun t -> t
            use _ = disposeRig rig
            let! stub = StubRemote.start [ "alice"; "dave" ]
            do! addFollower rig.Db "u-1" stub "alice" "accepted" None
            do! addFollower rig.Db "u-1" stub "dave" "pending" None
            let! id = publish rig "u-1" "followers" "<p>friends</p>"
            do! remoteActor rig.Db
            do! remoteReplies rig.Db id (iri id) "x" 1 "public" "live"

            for path in [ $"{objectPath id}/replies"; $"{objectPath id}/replies?page=1"; $"/ap/contexts/{id}" ] do
                use! anonymous = federationGet rig path []
                Assert.Equal(HttpStatusCode.NotFound, anonymous.StatusCode)
                use! pending = federationGet rig path (stub.Sign("dave", "GET", rig.Host.FederationUrl + path, None))
                Assert.Equal(HttpStatusCode.NotFound, pending.StatusCode)
                use! follower = federationGet rig path (stub.Sign("alice", "GET", rig.Host.FederationUrl + path, None))
                Assert.Equal(HttpStatusCode.OK, follower.StatusCode)
                Assert.Contains("private", follower.Headers.CacheControl.ToString())
        }

    [<Fact>]
    member _.``FR-021 a deleted object's collections answer 410, and an unknown object's 404``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>gone soon</p>"
            let! status, _ = sendInternal rig Net.Http.HttpMethod.Delete $"/internal/objects/{id}" None
            Assert.Equal(202, status)
            let! replies, _ = getJson rig $"{objectPath id}/replies"
            Assert.Equal(410, replies)
            let! context, _ = getJson rig $"/ap/contexts/{id}"
            Assert.Equal(410, context)
            let! unknown, _ = getJson rig $"{objectPath (string (Guid.NewGuid()))}/replies"
            Assert.Equal(404, unknown)
            let! unknownContext, _ = getJson rig $"/ap/contexts/{Guid.NewGuid()}"
            Assert.Equal(404, unknownContext)
        }

    [<Fact>]
    member _.``FR-022 the context lists the root first and every public reply of the thread, in pages; a reply has no context URL of its own``() : Task =
        task {
            let! rig = start pg []
            use _ = disposeRig rig
            let! id = publish rig "u-1" "public" "<p>thread</p>"
            do! remoteActor rig.Db
            let! l1 = localReply rig.Db id (iri id) "public"
            // A reply to the reply: still in the thread
            do! remoteReplies rig.Db id l1 "n" 60 "public" "live"

            let! _, whole = getJson rig $"/ap/contexts/{id}"
            Assert.Equal("Collection", jstr whole["type"])
            Assert.Equal($"https://{domain}/ap/contexts/{id}", jstr whole["id"])
            Assert.Equal(62, total whole)
            let first = items whole["first"]
            Assert.Equal(50, first.Length)
            Assert.Equal(iri id, first.Head)
            Assert.Equal(l1, first[1])

            let! _, p2 = getJson rig $"/ap/contexts/{id}?page=2"
            Assert.Equal(12, (items p2).Length)
            Assert.Null p2["next"]
            Assert.Equal(62, (List.distinct (first @ items p2)).Length)

            // The reply's own object document points at the root's context
            let replyId = l1.Substring(l1.LastIndexOf '/' + 1)
            let! status, _ = getJson rig $"/ap/contexts/{replyId}"
            Assert.Equal(404, status)
        }
