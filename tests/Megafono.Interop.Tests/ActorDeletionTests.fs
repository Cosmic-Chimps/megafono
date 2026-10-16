/// User Story 6 against real servers (T011, SC-001): a GoToSocial (or Mastodon) user follows an unlocked local actor
/// that published a public post; the app deletes the actor through the internal API; the remote server then drops
/// the account, its post and the follow, and Megáfono answers 410 on the actor id.
module Megafono.Interop.Tests.ActorDeletionTests

open System
open System.Net
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private internalCall (megafono: MegafonoInterop.InteropMegafono) (httpMethod: HttpMethod) (path: string) (body: string option) : Task<int * JsonNode> =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        use request = new HttpRequestMessage(httpMethod, megafono.InternalUrl + path)
        body |> Option.iter (fun b -> request.Content <- new StringContent(b, Text.Encoding.UTF8, "application/json"))
        use! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, (if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text)
    }

let private publishPublic (megafono: MegafonoInterop.InteropMegafono) (userId: string) (content: string) =
    task {
        let body = JsonObject()
        body["audience"] <- JsonValue.Create "public"
        let inner = JsonObject()
        inner["type"] <- JsonValue.Create "Note"
        inner["content"] <- JsonValue.Create $"<p>{content}</p>"
        body["object"] <- inner
        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{userId}/objects" (Some(body.ToJsonString()))
        Assert.Equal(201, status)
        return answer["id"].GetValue<string>()
    }

let private plainGet (megafono: MegafonoInterop.InteropMegafono) (path: string) =
    task {
        use client = new HttpClient()
        use request = new HttpRequestMessage(HttpMethod.Get, megafono.FederationUrl + path)
        request.Headers.Accept.ParseAdd "application/activity+json"
        use! response = client.SendAsync request
        return response.StatusCode
    }

/// The `Delete{actor}` delivery rows, to read when the remote server does not drop the account
let private deliverySummary (megafono: MegafonoInterop.InteropMegafono) : Task<string> =
    task {
        let db = megafono.Services.GetService(typeof<Npgsql.NpgsqlDataSource>) :?> Npgsql.NpgsqlDataSource
        use command = db.CreateCommand "SELECT a.activity_type, d.status, d.attempts, d.last_error FROM deliveries d JOIN activities a ON a.id = d.activity ORDER BY a.created_at"
        use! reader = command.ExecuteReaderAsync()
        let lines = ResizeArray<string>()

        while reader.Read() do
            let error = if reader.IsDBNull 3 then "-" else reader.GetString 3
            lines.Add $"{reader.GetString 0} {reader.GetString 1} attempts={reader.GetInt32 2} error={error}"

        return String.Join("; ", lines)
    }

let private deletionFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        // Unique per run: servers cache the actors and keys they have seen
        let handle = uniqueName "leaver"
        let userId = $"u-{handle}"
        let user = { FakeApp.user userId handle with Name = "Leaver"; Locked = false; ProfileUrl = $"https://megafono.test/u/{handle}" }
        megafono.FakeApp.AddUser user
        use! carol = server.CreateUser "carol"

        // 1. carol follows the unlocked actor
        let! found = carol.SearchAccount($"{handle}@megafono.test", true)
        Assert.True(found.IsSome, "the actor is found by its handle")
        let account = found.Value
        let! _ = carol.Follow account.Id

        let! _ =
            pollFor "carol follows the actor" (fun () ->
                task {
                    let! r = carol.Relationship account.Id
                    return (if r.Following then Some r else None)
                })

        // 2. its public post reaches her home timeline
        let marker = "farewell-" + uniqueName "m"
        let! postId = publishPublic megafono userId marker

        let! seen =
            pollFor $"'{marker}' in the home timeline" (fun () ->
                task {
                    let! timeline = carol.HomeTimeline()
                    return timeline |> List.tryFind (fun s -> s.Content.Contains marker)
                })

        // 3. the app deletes the actor (it stays in the fake app: the id must not come back)
        let! status, answer = internalCall megafono HttpMethod.Delete $"/internal/actors/{userId}" None
        Assert.Equal(202, status)
        Assert.Equal(1, answer["deliveries"].GetValue<int>())

        // 4. the server drops the account. The account lookup by the old id is what both servers share, but not how
        // they answer: a server may delete the record (404) or keep it hollowed out, `suspended` with no profile
        // (GoToSocial does this to a deleted remote account, so a later fetch cannot recreate it under another id).
        // Either one is "the server no longer has the account": the check accepts both, and the profile and counts
        // are asserted empty so a merely suspended account that kept its data would still fail. The follow goes with
        // the account, and so does its post.
        let gone (found: Account option) =
            match found with
            | None -> true
            | Some a -> a.Suspended && a.DisplayName = "" && a.StatusesCount = 0 && a.FollowersCount = 0

        try
            do!
                pollForTrue "the server drops the account" (fun () ->
                    task {
                        let! found = carol.GetAccount account.Id
                        return gone found
                    })
        with e ->
            let! summary = deliverySummary megafono
            let! raw = carol.AccountRaw account.Id

            let! logs =
                match box server with
                | :? GoToSocialServer.GoToSocialServer as gts ->
                    task {
                        let! l = gts.Logs()
                        return l.Split('\n') |> Array.filter (fun l -> l.Contains handle || l.Contains "level=WARN" || l.Contains "level=ERROR") |> Array.truncate 30 |> String.concat "\n"
                    }
                | _ -> Task.FromResult ""

            return raise (Exception($"{e.Message} | account: {raw} | deliveries: {summary}\n{logs}"))

        let! after = carol.GetAccount account.Id
        printfn "%s: after the Delete the account lookup is %s" server.Domain (match after with None -> "404" | Some a -> $"200 suspended={a.Suspended}")
        // Mastodon lists no relationship for an account it no longer has; GoToSocial lists one, without the follow
        let! relationships = carol.Relationships [ account.Id ]
        Assert.DoesNotContain(relationships, fun r -> r.Following || r.Requested)

        let! timeline = carol.HomeTimeline()
        Assert.DoesNotContain(timeline, fun s -> s.Id = seen.Id || s.Content.Contains marker)

        let! postGone =
            task {
                try
                    let! s = carol.GetStatus seen.Id
                    return s.Content.Contains marker |> not
                with FediClientException(_, _, 404, _) ->
                    return true
            }

        Assert.True(postGone, "the post is gone or emptied")

        // A fresh resolve must not bring the account back: the old record stays (hollowed out) or the actor is
        // refused with Megáfono's 410
        let! again = carol.SearchAccount($"{handle}@megafono.test", true)
        printfn "%s: a fresh resolve finds %s" server.Domain (match again with None -> "nothing" | Some a -> $"account {a.Id} suspended={a.Suspended}")
        Assert.True(again |> Option.forall (fun a -> a.Suspended || a.Id <> account.Id) , "a fresh resolve does not revive the account")

        // 5. Megáfono answers 410 on the actor and its post, and the app still has the user
        let! actorStatus = plainGet megafono $"/ap/actors/{userId}"
        Assert.Equal(HttpStatusCode.Gone, actorStatus)
        let! postStatus = plainGet megafono $"/ap/objects/{postId}"
        Assert.Equal(HttpStatusCode.Gone, postStatus)
        Assert.True(megafono.FakeApp.Users.ContainsKey userId, "the user stays in the fake app")
    }

type ActorDeletionTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US6 GoToSocial drops the account, its post and the follow after the app deletes the actor`` () =
        task {
            let! gts = net.GoToSocial()
            do! deletionFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonActorDeletionTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US6 Mastodon drops the account, its post and the follow after the app deletes the actor`` () =
        task {
            let! mastodon = net.Mastodon()
            do! deletionFlow mastodon mastodon.Front net pg
        }
