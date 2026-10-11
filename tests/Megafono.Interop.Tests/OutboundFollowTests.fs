/// User Story 4 against real servers (T068): Alex follows a locked account on GoToSocial (or Mastodon) by handle,
/// the remote shows the request and accepts, the remote follows Alex back and the app approves, both lists show
/// `mutual: true`, and unfollowing clears the follower on the remote side.
module Megafono.Interop.Tests.OutboundFollowTests

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

let private eventFor (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (username: string) =
    pollFor $"{eventType} for {username}" (fun () ->
        task {
            return
                megafono.FakeApp.Received
                |> List.tryFind (fun e ->
                    e.Type = eventType && (get e.Json "actor.handle").StartsWith($"@{username}@", StringComparison.OrdinalIgnoreCase))
        })

let private internalCall (megafono: MegafonoInterop.InteropMegafono) (httpMethod: HttpMethod) (path: string) (body: string option) =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        use request = new HttpRequestMessage(httpMethod, megafono.InternalUrl + path)
        body |> Option.iter (fun b -> request.Content <- new StringContent(b, Encoding.UTF8, "application/json"))
        let! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

let private listItems (body: string) =
    use json = JsonDocument.Parse body
    [ for i in json.RootElement.GetProperty("items").EnumerateArray() ->
          i.GetProperty("actor").GetProperty("id").GetString(), i.GetProperty("status").GetString(), i.GetProperty("mutual").GetBoolean() ]

let private outboundFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        let alexHandle = uniqueName "alexo"
        let alexId = $"u-{alexHandle}"
        megafono.FakeApp.AddUser { FakeApp.user alexId alexHandle with Name = "Alex Outbound"; Locked = true; ProfileUrl = $"https://megafono.test/u/{alexHandle}" }

        use! bob = server.CreateUser "bob"
        let! _ = bob.LockAccount()
        let bobHandle = $"@{bob.Username}@{server.Domain}"

        // The app resolves the remote handle: a sanitized name and profile
        let! status, body = internalCall megafono HttpMethod.Get $"/internal/remote-actors?handle={Uri.EscapeDataString bobHandle}" None
        Assert.Equal(HttpStatusCode.OK, status)
        use resolved = JsonDocument.Parse body
        let bobActorId = resolved.RootElement.GetProperty("id").GetString()
        Assert.Equal(bobHandle, resolved.RootElement.GetProperty("handle").GetString())

        // 1. Alex follows bob by handle: bob's server shows a request from Alex
        let! status, body = internalCall megafono HttpMethod.Post $"/internal/actors/{alexId}/following" (Some(JsonSerializer.Serialize {| target = bobHandle |}))
        Assert.Equal(HttpStatusCode.Accepted, status)
        Assert.Contains("pending", body)
        let! request =
            pollFor "bob sees Alex's request" (fun () ->
                task {
                    let! requests = bob.FollowRequests()
                    return requests |> List.tryFind (fun a -> a.Username = alexHandle)
                })
        Assert.Equal($"{alexHandle}@megafono.test", request.Acct)

        // 2. bob accepts: the app hears follow.accepted (outbound) and the row is accepted
        let! _ = bob.AuthorizeFollowRequest request.Id
        let! accepted = eventFor megafono "follow.accepted" bob.Username
        Assert.Equal("outbound", get accepted.Json "follow.direction")
        Assert.Equal("accepted", get accepted.Json "follow.status")
        Assert.Equal(alexId, get accepted.Json "user.id")

        // 3. bob follows Alex back; Alex's account is locked, so the app approves
        let! alexOnBobsServer = bob.SearchAccount($"{alexHandle}@megafono.test", true)
        let alexAccount = alexOnBobsServer.Value
        let! _ = bob.Follow alexAccount.Id
        let! requested = eventFor megafono "follow.requested" bob.Username
        Assert.Equal("inbound", get requested.Json "follow.direction")
        let followId = get requested.Json "follow.id"
        let! approve = internalCall megafono HttpMethod.Post $"/internal/actors/{alexId}/follow-requests/{followId}/approve" None
        Assert.Equal(HttpStatusCode.OK, fst approve)
        let! _ =
            pollFor "bob follows Alex" (fun () ->
                task {
                    let! r = bob.Relationship alexAccount.Id
                    return (if r.Following then Some r else None)
                })

        // 4. Both lists show bob as mutual
        let! _, following = internalCall megafono HttpMethod.Get $"/internal/actors/{alexId}/following?status=accepted" None
        let! _, followers = internalCall megafono HttpMethod.Get $"/internal/actors/{alexId}/followers?status=accepted" None
        Assert.Equal<(string * string * bool) list>([ bobActorId, "accepted", true ], listItems following)
        Assert.Equal<(string * string * bool) list>([ bobActorId, "accepted", true ], listItems followers)

        // 5. Alex unfollows: bob's server no longer lists Alex as a follower, and the follow-back is no longer mutual
        let! status, _ = internalCall megafono HttpMethod.Delete $"/internal/actors/{alexId}/following?actor={Uri.EscapeDataString bobActorId}" None
        Assert.Equal(HttpStatusCode.Accepted, status)
        let! _ =
            pollFor "bob no longer followed by Alex" (fun () ->
                task {
                    let! r = bob.Relationship alexAccount.Id
                    return (if not r.FollowedBy then Some r else None)
                })

        let! _, after = internalCall megafono HttpMethod.Get $"/internal/actors/{alexId}/following" None
        Assert.Empty(listItems after)
        let! _, followersAfter = internalCall megafono HttpMethod.Get $"/internal/actors/{alexId}/followers" None
        Assert.Equal<(string * string * bool) list>([ bobActorId, "accepted", false ], listItems followersAfter)

        // What the remote's answer looked like on the wire
        let answers = megafono.InboxRequests |> List.filter (fun r -> r.Body.Contains "\"Accept\"")
        Assert.NotEmpty answers
        Assert.All(answers, fun r -> Assert.Equal(202, r.Status))
    }

type OutboundFollowTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US4 Alex follows a locked GoToSocial account, is accepted, is followed back and unfollows`` () =
        task {
            let! gts = net.GoToSocial()
            do! outboundFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonOutboundFollowTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US4 Alex follows a locked Mastodon account, is accepted, is followed back and unfollows`` () =
        task {
            let! mastodon = net.Mastodon()
            do! outboundFlow mastodon mastodon.Front net pg
        }
