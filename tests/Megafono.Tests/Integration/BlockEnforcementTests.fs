/// What a block refuses afterwards (specs/004-blocks): inbound activities, signed fetches and the reply collections. The
/// user part is User Story 1 (T008); the instance part is added with User Story 2.
module Megafono.Tests.Integration.BlockEnforcementTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

let private domain = "megafono.test"
let private objectIri (id: string) = $"https://{domain}/ap/objects/{id}"

let private blockActor (rig: Rig) (user: string) (actor: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Post $"/internal/actors/{user}/blocks" (Some(JsonSerializer.Serialize {| actor = actor |}))
        Assert.True((status = 201 || status = 200), $"block answered {status}: {text}")
    }

let private liftActor (rig: Rig) (user: string) (actor: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Delete $"/internal/actors/{user}/blocks?actor={Uri.EscapeDataString actor}" None
        Assert.True((status = 204), $"lift answered {status}: {text}")
    }

let private events (rig: Rig) (eventType: string) = rig.App.Received |> List.filter (fun e -> e.Type = eventType)

let private waitForEvents (rig: Rig) (eventType: string) (count: int) =
    waitUntil $"{count} {eventType} event(s)" (TimeSpan.FromSeconds 20.0) (fun () -> (events rig eventType).Length >= count)

/// Long enough for the queue to have delivered an event that was going to be sent
let private settle () = Task.Delay 700

let private count (rig: Rig) (sql: string) : Task<int64> = Db.count rig.Db sql

/// POST to a Megáfono inbox, signed as `name` on the stub
let private send (rig: Rig) (name: string) (url: string) (body: string) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        use! response = Signed.postText client rig.Stub name url body
        return response.StatusCode
    }

let private sharedInbox (rig: Rig) = rig.Host.FederationUrl + "/inbox"
let private personalInbox (rig: Rig) (user: string) = $"{rig.Host.FederationUrl}/ap/actors/{user}/inbox"

let private reply (rig: Rig) (name: string) (suffix: string) (inReplyTo: string) : Task<HttpStatusCode> =
    send rig name (sharedInbox rig) (createNote rig.Stub name suffix (Signed.note rig.Stub name suffix inReplyTo))

let private getJson (rig: Rig) (path: string) : Task<int * JsonNode> =
    task {
        use! response = federationGet rig path []
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, (if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text)
    }

let private total (node: JsonNode) : int = node["totalItems"].GetValue<int>()
let private items (node: JsonNode) : string list = [ for i in (node["items"] :?> JsonArray) -> jstr i ]

let private blockDomain (rig: Rig) (domain: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(JsonSerializer.Serialize {| domain = domain |}))
        Assert.True((status = 201 || status = 200), $"instance block answered {status}: {text}")
    }

let private blockInstanceActor (rig: Rig) (actor: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Post "/internal/blocks" (Some(JsonSerializer.Serialize {| actor = actor |}))
        Assert.True((status = 201 || status = 200), $"instance block answered {status}: {text}")
    }

let private liftDomain (rig: Rig) (domain: string) =
    task {
        let! status, text = sendInternal rig HttpMethod.Delete $"/internal/blocks?domain={Uri.EscapeDataString domain}" None
        Assert.True((status = 204), $"lift answered {status}: {text}")
    }

/// The headers of a request the stub signed, claiming `keyId` instead of the stub's own. The string a signature covers
/// does not include the key id, so the signature still verifies for whoever holds the stub's key: a request that comes
/// from "another server" without a second stub, whose host is the same 127.0.0.1 a domain block names.
let private claiming (keyId: string) (headers: (string * string) list) : (string * string) list =
    headers
    |> List.map (fun (name, value) ->
        if name = "Signature" then name, Regex.Replace(value, "keyId=\"[^\"]*\"", $"keyId=\"{keyId}\"") else name, value)

/// An actor on `other.example` whose key is the stub's, cached, so a request claiming its key id is verified from the
/// cache and Megáfono never has to reach the server
let private otherServerActor (rig: Rig) (name: string) : Task<string> =
    task {
        let id = $"https://other.example/users/{name}"

        do!
            Db.execute
                rig.Db
                $"""INSERT INTO remote_actors (id, host, handle, actor_type, inbox, key_id, public_key_pem)
                    VALUES ('{id}', 'other.example', '{name}@other.example', 'Person', '{id}/inbox', '{id}#main-key', '{rig.Stub.PublicKeyPem}')"""

        return id
    }

/// POST signed by the stub's key, claiming `keyId`; `alter` may change what the stub signed
let private sendClaiming (rig: Rig) (name: string) (keyId: string option) (url: string) (body: string) (alter: (string * string) list -> (string * string) list) : Task<HttpStatusCode> =
    task {
        use client = new HttpClient()
        let bytes = Encoding.UTF8.GetBytes body
        let headers = rig.Stub.Sign(name, "POST", url, Some bytes)
        let headers = (match keyId with Some k -> claiming k headers | None -> headers) |> alter
        use! response = Signed.postRaw client url bytes headers
        return response.StatusCode
    }

let private breakSignature (headers: (string * string) list) : (string * string) list =
    headers
    |> List.map (fun (name, value) ->
        if name = "Signature" then name, Regex.Replace(value, "signature=\"[^\"]*\"", "signature=\"AAAA\"") else name, value)

let private deliveryRow (rig: Rig) (activity: Guid) : Task<string * string> =
    task {
        use command = rig.Db.CreateCommand $"SELECT status, COALESCE(last_error, '') FROM deliveries WHERE activity = '{activity}'"
        use! reader = command.ExecuteReaderAsync()
        reader.Read() |> ignore
        return reader.GetString 0, reader.GetString 1
    }

/// A delivery to `inbox` of one of alex's activities, due long ago: the worker takes it at once
let private dueDelivery (rig: Rig) (inbox: string) : Task<Guid> =
    task {
        let! activity, _ = Seed.activity rig.Db "u-1"

        do!
            Db.execute
                rig.Db
                $"""INSERT INTO deliveries (id, activity, inbox_url, host, next_attempt_at)
                    VALUES (gen_random_uuid(), '{activity}', '{inbox}', '{Uri(inbox).Authority}', '2000-01-01')"""

        return activity
    }

/// A rig whose stub server has bob and carol
let private rigWithStub (pg: PostgresFixture) : Task<Rig> = start pg [ "bob"; "carol" ]

type BlockEnforcementTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-006 FR-010 a Follow from the blocked actor to the user is 202 and dropped; to another user, or from another actor, it still works``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)
            Assert.Empty(events rig "follow.requested")
            Assert.Empty(events rig "follow.accepted")
            // Nothing is answered: the only thing bob's server got is the Block itself, no Accept
            do! waitUntil "the Block" (TimeSpan.FromSeconds 20.0) (fun () -> not rig.Stub.Posts.IsEmpty)
            do! settle ()
            let types = rig.Stub.Posts |> List.map (fun p -> jstr (at (json (Encoding.UTF8.GetString p.Body)) "type"))
            Assert.Equal<string list>([ "Block" ], types)

            // bob to bea: bea has not blocked him
            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "2" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! beas = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2' AND status = 'accepted'"
            Assert.Equal(1L, beas)
            do! waitForEvents rig "follow.accepted" 1
            Assert.Equal("u-2", jstr (at (events rig "follow.accepted").Head.Json "user.id"))

            // carol to alex: the block names bob alone, though both live on one server
            let! status = send rig "carol" (sharedInbox rig) (followBody rig.Stub "carol" "3" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let carol = rig.Stub.ActorId "carol"
            let! alexs = count rig $"SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{carol}'"
            Assert.Equal(1L, alexs)
        }

    [<Fact>]
    member _.``FR-006 FR-010 a reply and an edit from the blocked actor are 202 and not recorded, a reply from another actor is``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"

            // bob replied before the block
            let! status = reply rig "bob" "1" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! status = reply rig "bob" "2" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            // an edit of the earlier reply
            let edited = Signed.note rig.Stub "bob" "1" (objectIri root)
            edited["content"] <- JsonValue.Create "<p>edited</p>"
            edited["updated"] <- JsonValue.Create "2026-10-12T18:30:00Z"
            let! status = send rig "bob" (sharedInbox rig) (updateNote rig.Stub "bob" "1" "e1" edited)
            Assert.Equal(HttpStatusCode.Accepted, status)
            // a reply to bob's own reply, still in alex's thread
            let! status = reply rig "bob" "3" (noteId rig.Stub "bob" "1")
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! settle ()

            Assert.Equal(1, (events rig "reply.received").Length)
            Assert.Empty(events rig "reply.updated")
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(1L, recorded)
            let bob2, bob3 = noteId rig.Stub "bob" "2", noteId rig.Stub "bob" "3"
            let! second = count rig $"SELECT count(*) FROM replies WHERE id = '{bob2}' OR id = '{bob3}'"
            Assert.Equal(0L, second)

            // carol, on the same server, is not blocked
            let! status = reply rig "carol" "1" (objectIri root)
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 2
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(2L, recorded)
        }

    [<Fact>]
    member _.``FR-006 a reply to the blocking user's own reply is dropped when that user blocks the signer, even in someone else's thread``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            // bea (u-2) replies in alex's thread
            let! status, text = sendInternal rig HttpMethod.Post "/internal/actors/u-2/objects" (Some(let o = publishBody "public" "<p>from bea</p>" in o["object"]["inReplyTo"] <- JsonValue.Create(objectIri root); o.ToJsonString()))
            Assert.Equal(201, status)
            let beaReply = objectIri (jstr (at (json text) "id"))

            do! blockActor rig "u-2" (rig.Stub.ActorId "bob")

            let! status = reply rig "bob" "1" beaReply
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! status = reply rig "carol" "1" beaReply
            Assert.Equal(HttpStatusCode.Accepted, status)
            do! waitForEvents rig "reply.received" 1
            do! settle ()
            let bobsId, carolsId = noteId rig.Stub "bob" "1", noteId rig.Stub "carol" "1"
            let! bobs = count rig $"SELECT count(*) FROM replies WHERE id = '{bobsId}'"
            let! carols = count rig $"SELECT count(*) FROM replies WHERE id = '{carolsId}'"
            Assert.Equal(0L, bobs)
            Assert.Equal(1L, carols)
        }

    [<Fact>]
    member _.``FR-006 anything the blocked actor sends to the user's own inbox is 202 and dropped before the body is parsed``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            // Garbage is a 400 for anyone else, so a 202 shows it was dropped before it was read
            let! status = send rig "carol" (personalInbox rig "u-1") "not json at all"
            Assert.Equal(HttpStatusCode.BadRequest, status)
            let! status = send rig "bob" (personalInbox rig "u-1") "not json at all"
            Assert.Equal(HttpStatusCode.Accepted, status)

            let! status = send rig "bob" (personalInbox rig "u-1") (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(0L, rows)

            // The block is alex's: bea's own inbox still reads bob's requests
            let! status = send rig "bob" (personalInbox rig "u-2") "not json at all"
            Assert.Equal(HttpStatusCode.BadRequest, status)
            let! status = send rig "bob" (personalInbox rig "u-2") (followBody rig.Stub "bob" "2" (ourActor "u-2"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-2'"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-008 SC-004 fetches signed by the blocked actor's key are 404 on every path of the user, and only the blocked key``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! addFollower rig.Db "u-1" rig.Stub "bob" "accepted" None
            let! pub = publish rig "u-1" "public" "<p>hello</p>"
            let! friends = publish rig "u-1" "followers" "<p>for friends</p>"
            let! activity = createActivityId rig pub
            let! friendsActivity = createActivityId rig friends
            let! beas = publish rig "u-2" "public" "<p>from bea</p>"

            let paths =
                [ "/ap/actors/u-1"
                  "/ap/actors/u-1/followers"
                  "/ap/actors/u-1/following"
                  "/ap/actors/u-1/outbox"
                  objectPath pub
                  $"{objectPath pub}/replies"
                  $"/ap/contexts/{pub}"
                  $"/ap/activities/{activity}" ]

            // Before the block bob reads all of it, and the object only his follow let him see
            for path in paths @ [ objectPath friends; $"/ap/activities/{friendsActivity}" ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"before the block {path}: {response.StatusCode}")

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            for path in paths @ [ objectPath friends; $"/ap/activities/{friendsActivity}" ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.NotFound), $"after the block {path}: {response.StatusCode}")
                Assert.False(response.Headers.Contains "WWW-Authenticate", $"{path}: no hint")
                let! text = response.Content.ReadAsStringAsync()
                Assert.Equal("", text)

            // Unsigned, and signed by someone else, nothing changed
            for path in paths do
                use! response = federationGet rig path []
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"unsigned {path}: {response.StatusCode}")
                use! response = signedGet rig rig.Stub "carol" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"carol {path}: {response.StatusCode}")

            // The block is alex's: bob still reads bea's actor and posts
            for path in [ "/ap/actors/u-2"; objectPath beas ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"bea {path}: {response.StatusCode}")

            // A block lifted, bob reads again
            do! liftActor rig "u-1" (rig.Stub.ActorId "bob")
            use! response = signedGet rig rig.Stub "bob" (objectPath pub)
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        }

    [<Fact>]
    member _.``FR-011 the blocked actor's replies leave the replies and context collections, counts included, and return when the block is lifted``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! _ = reply rig "bob" "1" (objectIri root)
            let! _ = reply rig "bob" "2" (objectIri root)
            let! _ = reply rig "carol" "1" (objectIri root)
            let! _ = reply rig "bob" "3" (noteId rig.Stub "carol" "1")
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(4L, recorded)

            let bobs = [ noteId rig.Stub "bob" "1"; noteId rig.Stub "bob" "2" ]
            let carols = noteId rig.Stub "carol" "1"

            let read () =
                task {
                    let! _, whole = getJson rig $"{objectPath root}/replies"
                    let! _, doc = getJson rig (objectPath root)
                    let! _, page = getJson rig $"{objectPath root}/replies?page=1"
                    let! _, context = getJson rig $"/ap/contexts/{root}"
                    return whole, doc, page, context
                }

            let! whole, doc, page, context = read ()
            Assert.Equal(3, total whole)
            Assert.Equal(3, total doc["replies"])
            Assert.Equal(3, (items page).Length)
            Assert.Equal(5, total context)

            do! blockActor rig "u-1" (rig.Stub.ActorId "bob")

            let! whole, doc, page, context = read ()
            Assert.Equal(1, total whole)
            Assert.Equal<string list>([ carols ], items whole["first"])
            Assert.Equal(1, total doc["replies"])
            Assert.Equal<string list>([ carols ], items (doc["replies"]["first"]))
            Assert.Equal<string list>([ carols ], items page)
            // The thread: the root, carol's reply; bob's three are out (his reply to carol's is bob's too)
            Assert.Equal(2, total context)
            let! _, contextPage = getJson rig $"/ap/contexts/{root}?page=1"
            Assert.Equal<string list>([ objectIri root; carols ], items contextPage)
            // The rows stay: lifting shows them again
            let! recorded = count rig "SELECT count(*) FROM replies WHERE state = 'live'"
            Assert.Equal(4L, recorded)

            do! liftActor rig "u-1" (rig.Stub.ActorId "bob")
            let! whole, _, _, context = read ()
            Assert.Equal(3, total whole)
            Assert.Equal(5, total context)
            Assert.Contains(bobs.Head, items (whole["first"]))
        }

    // ---- instance blocks (User Story 2, T016) -------------------------------------------------------------

    [<Fact>]
    member _.``FR-006 R-43 a request signed with a key on a blocked domain is 403 before any key fetch, valid signature or not``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockDomain rig "127.0.0.1"
            do! blockDomain rig "spam.example"
            Assert.Empty rig.Stub.Gets

            // Valid, with a broken signature, to the personal inbox, and a key on a subdomain of the other block
            let follow = followBody rig.Stub "bob" "1" (ourActor "u-1")
            let! status = sendClaiming rig "bob" None (sharedInbox rig) follow id
            Assert.Equal(HttpStatusCode.Forbidden, status)
            let! status = sendClaiming rig "bob" None (sharedInbox rig) follow breakSignature
            Assert.Equal(HttpStatusCode.Forbidden, status)
            let! status = sendClaiming rig "carol" None (personalInbox rig "u-1") follow id
            Assert.Equal(HttpStatusCode.Forbidden, status)
            let! status = sendClaiming rig "bob" (Some "https://a.spam.example/users/x#main-key") (sharedInbox rig) follow id
            Assert.Equal(HttpStatusCode.Forbidden, status)
            let! status = sendClaiming rig "bob" (Some "https://spam.example/users/x/main-key") (sharedInbox rig) follow breakSignature
            Assert.Equal(HttpStatusCode.Forbidden, status)

            // The stub was never asked for a key (R-43), and nothing was recorded
            Assert.Empty rig.Stub.Gets
            let! rows = count rig "SELECT count(*) FROM follows"
            Assert.Equal(0L, rows)
            let! actors = count rig "SELECT count(*) FROM remote_actors"
            Assert.Equal(0L, actors)

            // The same request is accepted once the block is lifted
            do! liftDomain rig "127.0.0.1"
            let! status = send rig "bob" (sharedInbox rig) follow
            Assert.Equal(HttpStatusCode.Accepted, status)
        }

    [<Fact>]
    member _.``FR-006 R-43 an instance-blocked actor is 403 from its key id alone, carol on the same server is not``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockInstanceActor rig (rig.Stub.ActorId "bob")
            // Blocking by URL read the actor's document, so the requests so far are not zero
            let getsBefore = rig.Stub.Gets.Length

            // Mastodon's key id is the actor id plus a fragment: refused from the claim alone
            let follow = followBody rig.Stub "bob" "1" (ourActor "u-1")
            let! status = send rig "bob" (sharedInbox rig) follow
            Assert.Equal(HttpStatusCode.Forbidden, status)
            Assert.Equal(getsBefore, rig.Stub.Gets.Length)
            let! status = send rig "bob" (personalInbox rig "u-2") follow
            Assert.Equal(HttpStatusCode.Forbidden, status)

            // carol is on the same server and not blocked
            let! status = send rig "carol" (sharedInbox rig) (followBody rig.Stub "carol" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let carol = rig.Stub.ActorId "carol"
            let! rows = count rig $"SELECT count(*) FROM follows WHERE local_actor = 'u-1' AND remote_actor = '{carol}'"
            Assert.Equal(1L, rows)
            let bob = rig.Stub.ActorId "bob"
            let! rows = count rig $"SELECT count(*) FROM follows WHERE remote_actor = '{bob}'"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``FR-006 R-43 a key whose id does not name its owner is caught once the signer is resolved: 403 for the blocked actor only``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            // GoToSocial's key ids (`…/users/bob/main-key`) say nothing of the actor until the key is fetched
            rig.Stub.KeyStyle <- StubRemote.PathStub
            do! blockInstanceActor rig (rig.Stub.ActorId "bob")
            // Blocking by URL cached the actor with its key id, which would refuse the claim; a key Megáfono has not
            // seen (a rotation, a second key) is what only the resolved owner can refuse
            do! Db.execute rig.Db "UPDATE remote_actors SET key_id = key_id || '-old'"
            let getsBefore = rig.Stub.Gets.Length

            let! status = send rig "bob" (sharedInbox rig) (followBody rig.Stub "bob" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Forbidden, status)
            // The key was fetched to learn the owner, and the owner decided
            Assert.True(rig.Stub.Gets.Length > getsBefore)
            Assert.Contains(rig.Stub.Gets |> List.skip getsBefore, fun g -> g.Path = "/users/bob/main-key")
            let bob = rig.Stub.ActorId "bob"
            let! rows = count rig $"SELECT count(*) FROM follows WHERE remote_actor = '{bob}'"
            Assert.Equal(0L, rows)

            let! status = send rig "carol" (sharedInbox rig) (followBody rig.Stub "carol" "1" (ourActor "u-1"))
            Assert.Equal(HttpStatusCode.Accepted, status)
            let! rows = count rig "SELECT count(*) FROM follows WHERE local_actor = 'u-1'"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-008 SC-004 fetches signed by a key on a blocked domain are 404 on every local actor's documents and on the instance actor``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! pub = publish rig "u-1" "public" "<p>hello</p>"
            let! beas = publish rig "u-2" "public" "<p>from bea</p>"
            let! activity = createActivityId rig pub

            let paths =
                [ "/ap/actor"
                  "/ap/actors/u-1"
                  "/ap/actors/u-1/followers"
                  "/ap/actors/u-1/following"
                  "/ap/actors/u-1/outbox"
                  "/ap/actors/u-2"
                  objectPath pub
                  objectPath beas
                  $"{objectPath pub}/replies"
                  $"/ap/contexts/{pub}"
                  $"/ap/activities/{activity}" ]

            for path in paths do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"before the block {path}: {response.StatusCode}")

            do! blockDomain rig "127.0.0.1"

            // bob and carol both live there
            for name in [ "bob"; "carol" ] do
                for path in paths do
                    use! response = signedGet rig rig.Stub name path
                    Assert.True((response.StatusCode = HttpStatusCode.NotFound), $"{name} after the block {path}: {response.StatusCode}")
                    Assert.False(response.Headers.Contains "WWW-Authenticate", $"{path}: no hint")
                    let! text = response.Content.ReadAsStringAsync()
                    Assert.Equal("", text)

            // Unsigned fetches are unchanged, and so is a key of another server
            for path in paths do
                use! response = federationGet rig path []
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"unsigned {path}: {response.StatusCode}")

                use! response = federationGet rig path (claiming "https://other.example/users/x#main-key" (rig.Stub.Sign("bob", "GET", rig.Host.FederationUrl + path, None)))
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"other server {path}: {response.StatusCode}")

            // Lifted, bob reads again
            do! liftDomain rig "127.0.0.1"

            for path in paths do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"after the lift {path}: {response.StatusCode}")
        }

    [<Fact>]
    member _.``FR-008 an instance block of one actor refuses that actor's key everywhere and nobody else's``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! pub = publish rig "u-1" "public" "<p>hello</p>"
            do! blockInstanceActor rig (rig.Stub.ActorId "bob")

            for path in [ "/ap/actor"; "/ap/actors/u-1"; "/ap/actors/u-2/outbox"; objectPath pub; $"{objectPath pub}/replies" ] do
                use! response = signedGet rig rig.Stub "bob" path
                Assert.True((response.StatusCode = HttpStatusCode.NotFound), $"bob {path}: {response.StatusCode}")
                use! response = signedGet rig rig.Stub "carol" path
                Assert.True((response.StatusCode = HttpStatusCode.OK), $"carol {path}: {response.StatusCode}")
        }

    [<Fact>]
    member _.``FR-011 the replies of a blocked actor or domain leave the collections of every local actor's posts``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! alexs = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! beas = publish rig "u-2" "public" "<p>Episode 6</p>"
            let! _ = reply rig "bob" "1" (objectIri alexs)
            let! _ = reply rig "bob" "2" (objectIri beas)
            let! _ = reply rig "carol" "1" (objectIri alexs)
            let! _ = reply rig "carol" "2" (objectIri beas)

            let totals () =
                task {
                    let! _, a = getJson rig $"{objectPath alexs}/replies"
                    let! _, b = getJson rig $"{objectPath beas}/replies"
                    let! _, ca = getJson rig $"/ap/contexts/{alexs}"
                    let! _, cb = getJson rig $"/ap/contexts/{beas}"
                    return [ total a; total b; total ca; total cb ]
                }

            let! before = totals ()
            Assert.Equal<int list>([ 2; 2; 3; 3 ], before)

            // One actor of the server: its replies leave both users' threads, carol's stay
            do! blockInstanceActor rig (rig.Stub.ActorId "bob")
            let! afterActor = totals ()
            Assert.Equal<int list>([ 1; 1; 2; 2 ], afterActor)
            let! _, page = getJson rig $"{objectPath alexs}/replies?page=1"
            Assert.Equal<string list>([ noteId rig.Stub "carol" "1" ], items page)

            // The whole domain: carol's go too
            do! blockDomain rig "127.0.0.1"
            let! afterDomain = totals ()
            Assert.Equal<int list>([ 0; 0; 1; 1 ], afterDomain)
            // The rows stay; lifting brings the replies back
            let! recorded = count rig "SELECT count(*) FROM replies WHERE state = 'live'"
            Assert.Equal(4L, recorded)
            do! liftDomain rig "127.0.0.1"
            let! restored = totals ()
            Assert.Equal<int list>([ 1; 1; 2; 2 ], restored)
        }

    [<Fact>]
    member _.``FR-009 a reply whose object lives on a blocked domain is never fetched, though the signer is another server's``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            let! root = publish rig "u-1" "public" "<p>Episode 5</p>"
            let! carol = otherServerActor rig "carol"

            // The Note lives on the stub (bob's server) and is attributed to carol of another server: the activity
            // carries only its id, so Megáfono has to fetch it from its origin
            let create (suffix: string) =
                let note = Signed.note rig.Stub "bob" suffix (objectIri root)
                note["attributedTo"] <- JsonValue.Create carol
                rig.Stub.ServeNote("bob", suffix, note)
                let activity = JsonObject()
                activity["@context"] <- JsonValue.Create "https://www.w3.org/ns/activitystreams"
                activity["id"] <- JsonValue.Create $"{carol}/statuses/{suffix}/activity"
                activity["type"] <- JsonValue.Create "Create"
                activity["actor"] <- JsonValue.Create carol
                activity["to"] <- JsonArray(JsonValue.Create publicIri)
                activity["object"] <- JsonValue.Create(noteId rig.Stub "bob" suffix)
                activity.ToJsonString()

            let fetched (suffix: string) =
                rig.Stub.Gets |> List.exists (fun g -> g.Path = $"/users/bob/statuses/{suffix}")

            let claim = $"{carol}#main-key"

            // Before the block the note is fetched and the reply recorded
            let! status = sendClaiming rig "bob" (Some claim) (sharedInbox rig) (create "1") id
            Assert.Equal(HttpStatusCode.Accepted, status)
            Assert.True(fetched "1")
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(1L, recorded)

            do! blockDomain rig "127.0.0.1"
            let getsBefore = rig.Stub.Gets.Length

            let! status = sendClaiming rig "bob" (Some claim) (sharedInbox rig) (create "2") id
            Assert.Equal(HttpStatusCode.Accepted, status)
            Assert.False(fetched "2")
            Assert.Equal(getsBefore, rig.Stub.Gets.Length)
            let! recorded = count rig "SELECT count(*) FROM replies"
            Assert.Equal(1L, recorded)
        }

    [<Fact>]
    member _.``FR-013 SC-005 a delivery to a blocked domain that was queued after the block is skipped by the worker as blocked, with no request``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockDomain rig "127.0.0.1"

            // As if an enqueue had committed just after the block's own transaction
            let! activity = dueDelivery rig (rig.Stub.InboxUrl "bob")
            rig.Host.Wake()

            do!
                waitUntilAsync "the delivery is skipped" (TimeSpan.FromSeconds 20.0) (fun () ->
                    task {
                        let! state, _ = deliveryRow rig activity
                        return state = "skipped"
                    })

            let! _, error = deliveryRow rig activity
            Assert.Equal("blocked", error)
            // The app is told it did not go out, like any skipped delivery
            do! waitForEvents rig "delivery.failed" 1
            Assert.Equal("blocked", jstr (at (events rig "delivery.failed").Head.Json "delivery.lastError"))
            do! settle ()
            Assert.Empty rig.Stub.Posts
        }

    [<Fact>]
    member _.``FR-013 the worker skips a late delivery to a blocked actor's own inbox and still sends the others``() : Task =
        task {
            let! rig = rigWithStub pg
            use _ = disposeRig rig
            do! blockInstanceActor rig (rig.Stub.ActorId "bob")

            let! toBob = dueDelivery rig (rig.Stub.InboxUrl "bob")
            let! toCarol = dueDelivery rig (rig.Stub.InboxUrl "carol")
            rig.Host.Wake()

            do!
                waitUntilAsync "both are settled" (TimeSpan.FromSeconds 20.0) (fun () ->
                    task {
                        let! a, _ = deliveryRow rig toBob
                        let! b, _ = deliveryRow rig toCarol
                        return a = "skipped" && b = "delivered"
                    })

            let! _, error = deliveryRow rig toBob
            Assert.Equal("blocked", error)
            Assert.Equal<string list>([ "/users/carol/inbox" ], rig.Stub.Posts |> List.map (fun p -> p.Path))
        }
