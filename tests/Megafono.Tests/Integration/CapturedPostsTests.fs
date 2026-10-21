/// Real feed traffic (T022, constitution Principle VI): the Create, Update and Delete of a followed account's posts
/// exactly as GoToSocial 0.22.2 and Mastodon 4.7.3 sent them to Megáfono (captured by the interop run, see
/// Fixtures/payloads), delivered again with their real signatures, key and headers to a local user who follows the
/// account. The replies captured for feature 002 are replayed too: for a follower who is not the thread's author
/// the same reply is a feed post.
module Megafono.Tests.Integration.CapturedPostsTests

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Publishing
open Megafono.Tests.Fixtures.PostgresFixture

type private Capture =
    { Headers: (string * string) list
      Body: byte[]
      Json: JsonNode
      ActorId: string
      KeyId: string
      Pem: string
      SignedAt: DateTimeOffset }

let private load (name: string) : Capture =
    let path = IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", "payloads", name + ".json")
    let root = JsonNode.Parse(IO.File.ReadAllText path)
    let body = Convert.FromBase64String(jstr (at root "bodyBase64"))

    let headers = [ for h in root["headers"].AsArray() -> jstr (at h "name"), jstr (at h "value") ]
    let date = headers |> List.find (fun (n, _) -> n.ToLowerInvariant() = "date") |> snd

    { Headers = headers
      Body = body
      Json = JsonNode.Parse body
      ActorId = jstr (at root "signer.actorId")
      KeyId = jstr (at root "signer.keyId")
      Pem = jstr (at root "signer.publicKeyPem")
      SignedAt = DateTimeOffset.Parse(date, Globalization.CultureInfo.InvariantCulture) }

// What HttpClient sets itself; everything else goes out as captured, so the signature still covers what it covered
let private managed = set [ "host"; "content-length"; "content-type"; "transfer-encoding"; "connection"; "accept-encoding" ]

let private replay (inbox: string) (capture: Capture) : Task<int> =
    task {
        use client = new HttpClient()
        use request = new HttpRequestMessage(HttpMethod.Post, inbox)
        let content = new ByteArrayContent(capture.Body)
        let contentType = capture.Headers |> List.find (fun (n, _) -> n.ToLowerInvariant() = "content-type") |> snd
        content.Headers.TryAddWithoutValidation("Content-Type", contentType) |> ignore
        request.Content <- content
        request.Headers.Host <- "megafono.test"

        for name, value in capture.Headers do
            if not (managed.Contains(name.ToLowerInvariant())) then
                request.Headers.TryAddWithoutValidation(name, value) |> ignore

        use! response = client.SendAsync request
        return int response.StatusCode
    }

/// The id an activity's `object` names: the Note's, the Tombstone's or the bare IRI
let private objectIdOf (capture: Capture) : string =
    match capture.Json["object"] with
    | :? JsonObject as o -> jstr o["id"]
    | other -> other.GetValue<string>()

type CapturedPostsTests(pg: PostgresFixture) =

    /// A host with two local users, `fan` who follows the captured actor (accepted) and `dana` who follows nobody, and
    /// the captured actor as Megáfono would have fetched it, fresh at the time of the capture
    let withFollower (signer: Capture) (test: MegafonoHost.MegafonoHost -> FakeApp.FakeApp -> FakeTime -> Npgsql.NpgsqlDataSource -> Task) : Task =
        task {
            let! cs = pg.CreateDatabase()
            let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
            app.AddUser(FakeApp.user "fan" "fan")
            app.AddUser(FakeApp.user "dana" "dana")
            app.AddUser { FakeApp.user "series" "series" with Type = "Group" }
            let time = FakeTime(signer.SignedAt)
            let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url time)
            use _ = MegafonoHost.disposer host
            let db = Npgsql.NpgsqlDataSource.Create cs
            use _ = db

            for id in [ "fan"; "dana"; "series" ] do
                let! _ = Seed.localActor db id id
                ()

            let authority = Uri(signer.ActorId).Authority
            use seed = db.CreateCommand "INSERT INTO remote_actors (id, host, handle, actor_type, inbox, shared_inbox, followers_url, key_id, public_key_pem, fetched_at) VALUES (@id, @host, @handle, 'Person', @inbox, @shared, @followers, @key, @pem, @at)"
            seed.Parameters.AddWithValue("id", signer.ActorId) |> ignore
            seed.Parameters.AddWithValue("host", authority) |> ignore
            seed.Parameters.AddWithValue("handle", "bob@" + authority) |> ignore
            seed.Parameters.AddWithValue("inbox", signer.ActorId + "/inbox") |> ignore
            seed.Parameters.AddWithValue("shared", $"https://{authority}/inbox") |> ignore
            seed.Parameters.AddWithValue("followers", signer.ActorId + "/followers") |> ignore
            seed.Parameters.AddWithValue("key", signer.KeyId) |> ignore
            seed.Parameters.AddWithValue("pem", signer.Pem) |> ignore
            seed.Parameters.AddWithValue("at", signer.SignedAt.UtcDateTime) |> ignore
            let! _ = seed.ExecuteNonQueryAsync()

            do!
                Db.execute
                    db
                    $"INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status) VALUES (gen_random_uuid(), 'outbound', 'fan', '{signer.ActorId}', 'https://megafono.test/ap/activities/{Guid.NewGuid()}', 'accepted')"

            do! test host app time db
            do! (app :> IAsyncDisposable).DisposeAsync().AsTask()
        }

    let postFlow (server: string) : Task =
        task {
            let create = load $"{server}-post-create"
            let followersCreate = load $"{server}-post-followers-create"
            let update = load $"{server}-post-update"
            let delete = load $"{server}-post-delete"
            let note = at create.Json "object"
            let postId = jstr (at note "id")
            let privateId = objectIdOf followersCreate
            // the Delete is of the followers-only post, the Update of the public one
            Assert.Equal(privateId, objectIdOf delete)
            Assert.Equal(postId, objectIdOf update)

            do!
                withFollower create (fun host app time _ ->
                    task {
                        let inbox = host.FederationUrl + "/inbox"
                        let events (kind: string) = app.Received |> List.filter (fun e -> e.Type = kind)
                        let wait kind count = waitUntil $"{count} {kind}" (TimeSpan.FromSeconds 20.0) (fun () -> (events kind).Length >= count)

                        // the public post
                        let! created = replay inbox create
                        Assert.Equal(202, created)
                        do! wait "post.received" 1
                        let received = (events "post.received").Head
                        Assert.True received.SignatureValid
                        Assert.Equal("fan", jstr (at received.Json "user.id"))
                        Assert.Equal(create.ActorId, jstr (at received.Json "actor.id"))
                        Assert.Equal(postId, jstr (at received.Json "object.id"))
                        Assert.Equal("Note", jstr (at received.Json "object.type"))
                        Assert.Equal("public", jstr (at received.Json "object.audience"))
                        Assert.Contains("Started Frieren S2", jstr (at received.Json "object.content"))
                        Assert.Equal(jstr (at note "published"), jstr (at received.Json "object.published"))
                        Assert.Null(at received.Json "object.inReplyTo")
                        Assert.Null(at received.Json "object.updated")

                        // the followers-only post
                        time.Set(followersCreate.SignedAt)
                        let! privateStatus = replay inbox followersCreate
                        Assert.Equal(202, privateStatus)
                        do! wait "post.received" 2
                        let followersEvent = (events "post.received") |> List.find (fun e -> jstr (at e.Json "object.id") = privateId)
                        Assert.Equal("followers", jstr (at followersEvent.Json "object.audience"))
                        Assert.Contains("only for followers", jstr (at followersEvent.Json "object.content"))

                        // the edit of the public post: new content, `updated`, the original audience and published
                        time.Set(update.SignedAt)
                        let! updated = replay inbox update
                        Assert.Equal(202, updated)
                        do! wait "post.updated" 1
                        let edit = (events "post.updated").Head
                        Assert.Equal(postId, jstr (at edit.Json "object.id"))
                        Assert.Contains("still Frieren S2", jstr (at edit.Json "object.content"))
                        Assert.Equal("public", jstr (at edit.Json "object.audience"))
                        Assert.Equal(jstr (at received.Json "object.published"), jstr (at edit.Json "object.published"))
                        Assert.NotNull(at edit.Json "object.updated")

                        // the deletion of the followers-only post
                        time.Set(delete.SignedAt)
                        let! deleted = replay inbox delete
                        Assert.Equal(202, deleted)
                        do! wait "post.deleted" 1
                        let gone = (events "post.deleted").Head
                        Assert.Equal("fan", jstr (at gone.Json "user.id"))
                        Assert.Equal(privateId, jstr (at gone.Json "object.id"))

                        // dana follows nobody: nothing reached her, and the replays changed nothing for anyone else
                        do! Task.Delay 700

                        for kind in [ "post.received"; "post.updated"; "post.deleted" ] do
                            Assert.All(events kind, fun e -> Assert.Equal("fan", jstr (at e.Json "user.id")))

                        Assert.Equal(2, (events "post.received").Length)
                    })
        }

    /// A reply captured for 002, for a follower who is not the thread's author: the thread's author hears
    /// `reply.received`, the follower `post.received` with `inReplyTo`, nobody both
    let replyFlow (server: string) : Task =
        task {
            let create = load $"{server}-reply-create"
            let note = at create.Json "object"
            let noteId = jstr (at note "id")
            let rootIri = jstr (at note "inReplyTo")
            let rootGuid = Guid.Parse(rootIri.Substring(rootIri.LastIndexOf '/' + 1))

            do!
                withFollower create (fun host app _ db ->
                    task {
                        do!
                            Db.execute
                                db
                                $"INSERT INTO objects (id, local_actor, audience, object_type, document) VALUES ('{rootGuid}', 'series', 'public', 'Note', '{{}}')"

                        let! created = replay (host.FederationUrl + "/inbox") create
                        Assert.Equal(202, created)
                        let events (kind: string) = app.Received |> List.filter (fun e -> e.Type = kind)
                        do! waitUntil "reply.received and post.received" (TimeSpan.FromSeconds 20.0) (fun () -> not (events "reply.received").IsEmpty && not (events "post.received").IsEmpty)
                        do! Task.Delay 700

                        let reply = Assert.Single(events "reply.received")
                        Assert.Equal("series", jstr (at reply.Json "user.id"))

                        let post = Assert.Single(events "post.received")
                        Assert.True post.SignatureValid
                        Assert.Equal("fan", jstr (at post.Json "user.id"))
                        Assert.Equal(noteId, jstr (at post.Json "object.id"))
                        Assert.Equal(rootIri, jstr (at post.Json "object.inReplyTo"))
                        Assert.Equal("public", jstr (at post.Json "object.audience"))
                        Assert.Null(at post.Json "object.parent")
                        Assert.Null(at post.Json "object.thread")
                    })
        }

    [<Fact>]
    member _.``005 T022 GoToSocial's Create, Update and Delete of a followed account's posts, replayed with their real signatures``() : Task = postFlow "gts"

    [<Fact>]
    member _.``005 T022 Mastodon's Create, Update and Delete of a followed account's posts, replayed with their real signatures``() : Task = postFlow "mastodon"

    [<Fact>]
    member _.``005 T022 GoToSocial's captured reply is a feed post for a follower who is not the thread's author``() : Task = replyFlow "gts"

    [<Fact>]
    member _.``005 T022 Mastodon's captured reply is a feed post for a follower who is not the thread's author``() : Task = replyFlow "mastodon"
