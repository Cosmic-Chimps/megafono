/// Real block traffic (specs/004-blocks T027, constitution Principle VI): what GoToSocial 0.22.2 and Mastodon 4.7.3
/// sent when their user blocked a local actor that she followed and that followed her, captured by the interop run and
/// delivered again with the real signatures. GoToSocial sends `Undo{Follow}` and `Block` within milliseconds of each
/// other; Mastodon adds a `Reject{Follow}`. They arrive at once, so the three handlers meet on the same actor and follow
/// rows: the interop run answered one Block with a 500 (a deadlock, from locking the follow row before the actor row)
/// until the lock order was fixed.
module Megafono.Tests.Integration.CapturedBlocksTests

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
    { Path: string
      Headers: (string * string) list
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

    { Path = jstr (at root "pathAndQuery")
      Headers = headers
      Body = body
      Json = JsonNode.Parse body
      ActorId = jstr (at root "signer.actorId")
      KeyId = jstr (at root "signer.keyId")
      Pem = jstr (at root "signer.publicKeyPem")
      SignedAt = DateTimeOffset.Parse(date, Globalization.CultureInfo.InvariantCulture) }

// What HttpClient sets itself; everything else goes out as captured, so the signature still covers what it covered
let private managed = set [ "host"; "content-length"; "content-type"; "transfer-encoding"; "connection"; "accept-encoding" ]

let private replay (federationUrl: string) (capture: Capture) : Task<int> =
    task {
        use client = new HttpClient()
        use request = new HttpRequestMessage(HttpMethod.Post, federationUrl + capture.Path)
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

let private userOf (actorIri: string) = actorIri.Substring(actorIri.LastIndexOf '/' + 1)

type CapturedBlocksTests(pg: PostgresFixture) =

    /// `others` arrive together with the Block
    let flow (server: string) (others: string list) : Task =
        task {
            let block = load $"{server}-block"
            let captures = [ for name in others -> load name ]
            let user = userOf (jstr (at block.Json "object"))

            let! cs = pg.CreateDatabase()
            let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
            app.AddUser(FakeApp.user user user)
            let time = FakeTime(block.SignedAt)
            let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url time)
            use _ = MegafonoHost.disposer host
            let db = Npgsql.NpgsqlDataSource.Create cs
            let! _ = Seed.localActor db user user

            // The signer as Megáfono would have fetched it: the key and the actor, fresh at the time of the capture
            let authority = Uri(block.ActorId).Authority
            use seed = db.CreateCommand "INSERT INTO remote_actors (id, host, handle, actor_type, inbox, shared_inbox, followers_url, key_id, public_key_pem, fetched_at) VALUES (@id, @host, @handle, 'Person', @inbox, @shared, @followers, @key, @pem, @at)"
            seed.Parameters.AddWithValue("id", block.ActorId) |> ignore
            seed.Parameters.AddWithValue("host", authority) |> ignore
            seed.Parameters.AddWithValue("handle", "carol@" + authority) |> ignore
            seed.Parameters.AddWithValue("inbox", block.ActorId + "/inbox") |> ignore
            seed.Parameters.AddWithValue("shared", $"https://{authority}/inbox") |> ignore
            seed.Parameters.AddWithValue("followers", block.ActorId + "/followers") |> ignore
            seed.Parameters.AddWithValue("key", block.KeyId) |> ignore
            seed.Parameters.AddWithValue("pem", block.Pem) |> ignore
            seed.Parameters.AddWithValue("at", block.SignedAt.UtcDateTime) |> ignore
            let! _ = seed.ExecuteNonQueryAsync()

            // They follow each other: hers carries the id of the Follow her server undoes, ours the one it rejects
            let inboundFollow =
                captures |> List.tryFind (fun c -> jstr (at c.Json "type") = "Undo") |> Option.map (fun c -> jstr (at c.Json "object.id"))

            let outboundFollow =
                captures |> List.tryFind (fun c -> jstr (at c.Json "type") = "Reject") |> Option.map (fun c -> jstr (at c.Json "object.id"))

            do!
                Db.execute
                    db
                    $"""INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status) VALUES
                        (gen_random_uuid(), 'inbound', '{user}', '{block.ActorId}', '{defaultArg inboundFollow "https://remote.example/follow/1"}', 'accepted'),
                        (gen_random_uuid(), 'outbound', '{user}', '{block.ActorId}', '{defaultArg outboundFollow "https://megafono.test/ap/activities/1"}', 'accepted')"""

            // All of them at once, as the server sent them
            let! statuses = Task.WhenAll [| for c in block :: captures -> replay host.FederationUrl c |]
            Assert.All(statuses, fun status -> Assert.Equal(202, status))

            let! open' = Publishing.strings db "SELECT direction FROM follows WHERE status IN ('pending', 'accepted')"
            Assert.Empty open'
            let! inbound = Publishing.strings db "SELECT status FROM follows WHERE direction = 'inbound'"
            Assert.Equal<string list>([ "removed" ], inbound)
            // `removed` by the Block (or the Undo), or `rejected` when the Reject got there first
            let! outbound = Publishing.strings db "SELECT status FROM follows WHERE direction = 'outbound'"
            Assert.Contains(List.exactlyOne outbound, [ "removed"; "rejected" ])

            let endings () = app.Received |> List.filter (fun e -> e.Type = "follow.removed" || e.Type = "follow.rejected")
            do! waitUntil "two events for the two follows" (TimeSpan.FromSeconds 20.0) (fun () -> (endings ()).Length >= 2)
            do! Task.Delay 700
            Assert.Equal<string list>([ "inbound"; "outbound" ], endings () |> List.map (fun e -> jstr (at e.Json "follow.direction")) |> List.sort)
        }

    [<Fact>]
    member _.``004 T027 GoToSocial's Undo of a Follow and its Block, replayed together with their real signatures``() : Task =
        flow "gts" [ "gts-undo-follow" ]

    [<Fact>]
    member _.``004 T027 Mastodon's Undo of a Follow, Reject of ours and Block, replayed together with their real signatures``() : Task =
        flow "mastodon" [ "mastodon-undo-follow"; "mastodon-reject" ]
