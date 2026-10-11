/// User Story 3 against real servers (T058): a follower sees a followers-only post as private, a non-follower cannot
/// fetch it, public and unlisted keep their visibility, an edit and a delete reach the follower, and a post with app
/// extension data displays normally. The follower and the stranger are real GoToSocial (or Mastodon) users; the
/// stranger's server does its own signed GET of the object URL when asked to resolve it.
module Megafono.Interop.Tests.PublishTests

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

/// A call to Megáfono's internal API
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

let private note (audience: string) (content: string) (extensions: string option) : string =
    let o = JsonObject()
    o["audience"] <- JsonValue.Create audience
    let inner = JsonObject()
    inner["type"] <- JsonValue.Create "Note"
    inner["content"] <- JsonValue.Create $"<p>{content}</p>"
    o["object"] <- inner
    extensions |> Option.iter (fun e -> o["extensions"] <- JsonNode.Parse e)
    o.ToJsonString()

type private Published = { Id: string; Url: string }

let private publish (megafono: MegafonoInterop.InteropMegafono) (userId: string) (audience: string) (content: string) (extensions: string option) =
    task {
        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{userId}/objects" (Some(note audience content extensions))
        Assert.Equal(201, status)
        let id = answer["id"].GetValue<string>()
        return { Id = id; Url = $"https://megafono.test/ap/objects/{id}" }
    }

/// What the queue says about every delivery so far: the first thing to read when a post does not arrive
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

/// The follower's home timeline entry that carries `marker`
let private inTimeline (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (follower: FediClient) (marker: string) =
    task {
        try
            return!
                pollFor $"'{marker}' in the home timeline" (fun () ->
                    task {
                        let! timeline = follower.HomeTimeline()
                        return timeline |> List.tryFind (fun s -> s.Content.Contains marker)
                    })
        with e ->
            let! summary = deliverySummary megafono
            let! gtsLines =
                match box server with
                | :? GoToSocialServer.GoToSocialServer as gts ->
                    task {
                        let! logs = gts.Logs()
                        return logs.Split('\n') |> Array.filter (fun l -> l.Contains "level=WARN" || l.Contains "level=ERROR") |> Array.truncate 12 |> String.concat "\n"
                    }
                | _ -> Task.FromResult ""

            return raise (Exception($"{e.Message} | deliveries: {summary}\n{gtsLines}"))
    }

let private plainGet (megafono: MegafonoInterop.InteropMegafono) (path: string) =
    task {
        use client = new HttpClient()
        use! response = client.GetAsync(megafono.FederationUrl + path)
        return response.StatusCode
    }

/// A fresh poster (an unlocked user of the fake app, with an id of its own: servers cache actor keys) followed by a
/// fresh follower on the server under test
let private withFollower
    (server: IFediServer)
    (front: IContainer)
    (net: FederationNetwork)
    (pg: PostgresFixture)
    (scenario: MegafonoInterop.InteropMegafono -> string -> FediClient -> Task)
    : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        let handle = uniqueName "poster"
        let userId = $"u-{handle}"
        megafono.FakeApp.AddUser { FakeApp.user userId handle with Name = "Poster"; Locked = false; ProfileUrl = $"https://megafono.test/u/{handle}" }
        use! carol = server.CreateUser "carol"
        let! account = carol.SearchAccount($"{handle}@megafono.test", true)
        let! _ = carol.Follow account.Value.Id

        let! _ =
            pollFor "carol follows the poster" (fun () ->
                task {
                    let! r = carol.Relationship account.Value.Id
                    return (if r.Following then Some r else None)
                })

        do! scenario megafono userId carol
    }

let private scenarios (server: IFediServer) : (string * (MegafonoInterop.InteropMegafono -> string -> FediClient -> Task)) list =
    let shows (audience: string) (visibility: string) =
        fun (megafono: MegafonoInterop.InteropMegafono) (userId: string) (carol: FediClient) ->
            task {
                let marker = (audience + "-" + uniqueName "m")
                let! _ = publish megafono userId audience marker None
                let! seen = inTimeline server megafono carol marker
                Assert.Equal(visibility, seen.Visibility)
            }
            :> Task

    [ "followers-only is private", shows "followers" "private"
      "public is public", shows "public" "public"
      "unlisted is unlisted", shows "unlisted" "unlisted"
      "app extension data displays normally",
      fun megafono userId carol ->
          task {
              let marker = ("ext-" + uniqueName "m")

              let extensions =
                  """{"namespace":"https://app.example.com/ns#","prefix":"app","data":{"watch":{"malId":52991,"episodes":28}}}"""

              let! _ = publish megafono userId "followers" marker (Some extensions)
              let! seen = inTimeline server megafono carol marker
              Assert.Equal("private", seen.Visibility)
              Assert.Contains(marker, seen.Content)
          }
          :> Task
      "an edit reaches the follower",
      fun megafono userId carol ->
          task {
              let marker = ("before-" + uniqueName "m")
              let! post = publish megafono userId "followers" marker None
              let! seen = inTimeline server megafono carol marker
              let edited = ("edited-" + uniqueName "m")
              let! status, _ = internalCall megafono HttpMethod.Put $"/internal/objects/{post.Id}" (Some(note "followers" edited None))
              Assert.Equal(200, status)

              let! after =
                  pollFor "the edit" (fun () ->
                      task {
                          let! s = carol.GetStatus seen.Id
                          return (if s.Content.Contains edited then Some s else None)
                      })

              Assert.DoesNotContain(marker, after.Content)
              Assert.Equal("private", after.Visibility)
              // `edited_at` is exposed by recent versions of both servers; reported, not required
              printfn "%s: edited_at = %s" server.Domain (if isNull after.EditedAt then "(not exposed)" else after.EditedAt)
          }
          :> Task
      "a delete removes it, and the URL then hides it",
      fun megafono userId carol ->
          task {
              let marker = ("doomed-" + uniqueName "m")
              let! post = publish megafono userId "followers" marker None
              let! seen = inTimeline server megafono carol marker
              let! status, _ = internalCall megafono HttpMethod.Delete $"/internal/objects/{post.Id}" None
              Assert.Equal(202, status)

              do!
                  pollForTrue "the private post is gone" (fun () ->
                      task {
                          let! timeline = carol.HomeTimeline()
                          return not (timeline |> List.exists (fun s -> s.Id = seen.Id))
                      })

              // Principle II: a deleted followers-only object is a 404 to anyone who is not a follower; a deleted
              // public one is 410
              let! hidden = plainGet megafono $"/ap/objects/{post.Id}"
              Assert.Equal(HttpStatusCode.NotFound, hidden)
              let! pub = publish megafono userId "public" "soon gone" None
              let! status, _ = internalCall megafono HttpMethod.Delete $"/internal/objects/{pub.Id}" None
              Assert.Equal(202, status)
              let! gone = plainGet megafono $"/ap/objects/{pub.Id}"
              Assert.Equal(HttpStatusCode.Gone, gone)
          }
          :> Task ]

/// A stranger's server fetches objects nobody has delivered to it, so the fetch is really made (a server that already
/// holds a delivered copy would answer from its own database). Nobody on the server follows, so nothing is delivered: a followers-only object must stay hidden (404) and a public one is fetched and
/// shown as public.
let private strangerFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        let handle = uniqueName "poster"
        let userId = $"u-{handle}"
        megafono.FakeApp.AddUser { FakeApp.user userId handle with Name = "Poster"; Locked = false; ProfileUrl = $"https://megafono.test/u/{handle}" }
        use! dave = server.CreateUser "dave"

        let! hiddenPost = publish megafono userId "followers" "hidden" None
        let! openPost = publish megafono userId "public" "open" None

        let! strangerPrivate = dave.SearchStatus hiddenPost.Url
        Assert.Empty strangerPrivate
        let! strangerPublic = dave.SearchStatus openPost.Url
        Assert.NotEmpty strangerPublic
        Assert.Equal("public", strangerPublic.Head.Visibility)
    }

/// Every scenario in turn, with one poster and one follower
let private followerFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    withFollower server front net pg (fun megafono userId carol ->
        task {
            for _, scenario in scenarios server do
                do! scenario megafono userId carol
        })

type PublishTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US3 GoToSocial: private, public and unlisted keep their visibility, extension data displays, an edit and a delete follow`` () =
        task {
            let! gts = net.GoToSocial()
            do! followerFlow gts gts.Front net pg
        }

    [<Fact>]
    member _.``US3 GoToSocial: a non-follower's server cannot fetch a followers-only object`` () =
        task {
            let! gts = net.GoToSocial()
            do! strangerFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonPublishTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US3 Mastodon: private, public and unlisted keep their visibility, extension data displays, an edit and a delete follow`` () =
        task {
            let! mastodon = net.Mastodon()
            do! followerFlow mastodon mastodon.Front net pg
        }

    [<Fact>]
    member _.``US3 Mastodon: a non-follower's server cannot fetch a followers-only object`` () =
        task {
            let! mastodon = net.Mastodon()
            do! strangerFlow mastodon mastodon.Front net pg
        }
