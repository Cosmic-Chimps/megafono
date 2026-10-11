/// Real reply traffic (T011, constitution Principle VI): the Create, Update and Delete of a reply exactly as
/// GoToSocial 0.22.2 and Mastodon 4.7.3 sent them to Megáfono (captured by the interop run, see Fixtures/payloads),
/// delivered again with their real signatures, key and headers. Their shapes differ from what the stub sends
/// (`to` and `tag` as single values on GoToSocial, `contentMap`, `cc` with the thread owner's followers on Mastodon).
module Megafono.Tests.Integration.CapturedRepliesTests

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

type CapturedRepliesTests(pg: PostgresFixture) =

    let flow (server: string) : Task =
        task {
            let create = load $"{server}-reply-create"
            let update = load $"{server}-reply-update"
            let delete = load $"{server}-reply-delete"
            let note = at create.Json "object"
            let noteId = jstr (at note "id")
            // The thread post these replies answer: the Note's inReplyTo is our object id
            let rootIri = jstr (at note "inReplyTo")
            let rootGuid = Guid.Parse(rootIri.Substring(rootIri.LastIndexOf '/' + 1))

            let! cs = pg.CreateDatabase()
            let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
            app.AddUser { FakeApp.user "series" "series" with Type = "Group" }
            let time = FakeTime(create.SignedAt)
            let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url time)
            use _ = MegafonoHost.disposer host
            let db = Npgsql.NpgsqlDataSource.Create cs
            let! _ = Seed.localActor db "series" "series"

            do!
                Db.execute
                    db
                    $"INSERT INTO objects (id, local_actor, audience, object_type, document) VALUES ('{rootGuid}', 'series', 'public', 'Note', '{{}}')"

            // The signer as Megáfono would have fetched it: the key and the actor, fresh at the time of the capture
            let authority = Uri(create.ActorId).Authority
            use seed = db.CreateCommand "INSERT INTO remote_actors (id, host, handle, actor_type, inbox, shared_inbox, followers_url, key_id, public_key_pem, fetched_at) VALUES (@id, @host, @handle, 'Person', @inbox, @shared, @followers, @key, @pem, @at)"
            seed.Parameters.AddWithValue("id", create.ActorId) |> ignore
            seed.Parameters.AddWithValue("host", authority) |> ignore
            seed.Parameters.AddWithValue("handle", "alice@" + authority) |> ignore
            seed.Parameters.AddWithValue("inbox", create.ActorId + "/inbox") |> ignore
            seed.Parameters.AddWithValue("shared", $"https://{authority}/inbox") |> ignore
            seed.Parameters.AddWithValue("followers", create.ActorId + "/followers") |> ignore
            seed.Parameters.AddWithValue("key", create.KeyId) |> ignore
            seed.Parameters.AddWithValue("pem", create.Pem) |> ignore
            seed.Parameters.AddWithValue("at", create.SignedAt.UtcDateTime) |> ignore
            let! _ = seed.ExecuteNonQueryAsync()

            let inbox = host.FederationUrl + "/inbox"
            let events (kind: string) = app.Received |> List.filter (fun e -> e.Type = kind)
            let wait kind = waitUntil kind (TimeSpan.FromSeconds 20.0) (fun () -> not (events kind).IsEmpty)

            // Create: the reply reaches the app with the author, the content warning and the thread
            let! created = replay inbox create
            Assert.Equal(202, created)
            do! wait "reply.received"
            let received = (events "reply.received").Head
            Assert.True received.SignatureValid
            Assert.Equal("series", jstr (at received.Json "user.id"))
            Assert.Equal(create.ActorId, jstr (at received.Json "actor.id"))
            Assert.Equal(noteId, jstr (at received.Json "object.id"))
            Assert.Equal("Note", jstr (at received.Json "object.type"))
            Assert.Equal(jstr (at note "summary"), jstr (at received.Json "object.summary"))
            Assert.True(boolOf (at received.Json "object.sensitive"))
            Assert.Equal("public", jstr (at received.Json "object.audience"))
            Assert.Equal("en", jstr (at received.Json "object.language"))
            Assert.Equal(rootIri, jstr (at received.Json "object.inReplyTo"))
            Assert.Equal(rootGuid.ToString "D", jstr (at received.Json "object.thread.objectId"))
            Assert.Contains("that ending!", jstr (at received.Json "object.content"))
            Assert.DoesNotContain("target=", jstr (at received.Json "object.content"))

            // Update: the edited content and summary
            time.Set(update.SignedAt)
            let! updated = replay inbox update
            Assert.Equal(202, updated)
            do! wait "reply.updated"
            let edit = (events "reply.updated").Head
            Assert.Equal(noteId, jstr (at edit.Json "object.id"))
            Assert.Contains("still that ending", jstr (at edit.Json "object.content"))
            Assert.Equal(jstr (at update.Json "object.summary"), jstr (at edit.Json "object.summary"))
            Assert.NotNull(at edit.Json "object.updated")

            // Delete: Mastodon sends a Tombstone, GoToSocial the bare id
            time.Set(delete.SignedAt)
            let! deleted = replay inbox delete
            Assert.Equal(202, deleted)
            do! wait "object.deleted"
            Assert.Equal(noteId, jstr (at (events "object.deleted").Head.Json "object.id"))
        }

    [<Fact>]
    member _.``002 T011 GoToSocial's Create, Update and Delete of a reply, replayed with their real signatures``() : Task = flow "gts"

    [<Fact>]
    member _.``002 T011 Mastodon's Create, Update and Delete of a reply, replayed with their real signatures``() : Task = flow "mastodon"
