/// User Story 2 against real servers (T045): a GoToSocial (or Mastodon) user follows a locked Alex, the app approves
/// or rejects through the internal API, an unlocked user is accepted at once, unfollow reaches the app, and a
/// request whose body does not match its signature is refused. Megáfono fetches the sender's key from the real
/// server (through the `SafeHttp.TestNetwork` seam), so this also proves key lookup against GoToSocial and Mastodon.
module Megafono.Interop.Tests.InboundFollowTests

open System
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

let private events (megafono: MegafonoInterop.InteropMegafono) (eventType: string) =
    megafono.FakeApp.Received |> List.filter (fun e -> e.Type = eventType)

/// The event of `eventType` about the remote user `username`
let private eventFor (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (username: string) =
    pollFor $"{eventType} for {username}" (fun () ->
        task {
            return
                events megafono eventType
                |> List.tryFind (fun e -> (get e.Json "actor.handle").StartsWith($"@{username}@", StringComparison.OrdinalIgnoreCase))
        })

let private answer (megafono: MegafonoInterop.InteropMegafono) (user: string) (followId: string) (verb: string) =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        let! response = client.PostAsync($"{megafono.InternalUrl}/internal/actors/{user}/follow-requests/{followId}/{verb}", null)
        return response.StatusCode
    }

/// A Follow signed by a key that is not the one the remote server published for `keyId`
let private forgedFollow (megafono: MegafonoInterop.InteropMegafono) (targetId: string) (actorId: string) (keyId: string) =
    task {
        use key = RSA.Create 2048
        let activity = $"{actorId}#forged-{Guid.NewGuid():N}"

        let body =
            Encoding.UTF8.GetBytes
                $"""{{"id":"{activity}","type":"Follow","actor":"{actorId}","object":"https://megafono.test/ap/actors/{targetId}"}}"""

        let url = megafono.FederationUrl + "/inbox"
        let headers = HttpSignatures.sign keyId key DateTimeOffset.UtcNow "POST" (Uri url) (Some body)
        use request = new HttpRequestMessage(HttpMethod.Post, url, Content = new ByteArrayContent(body))

        for name, value in headers do
            request.Headers.TryAddWithoutValidation(name, value) |> ignore

        use client = new HttpClient()
        let! response = client.SendAsync request
        return response.StatusCode
    }

/// US2 scenarios 1 to 6 for one server. `keyIdOf` turns an actor id into the key id that server's actors use.
let private inboundFlow (server: IFediServer) (front: IContainer) (keyIdOf: string -> string) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        // Handles are unique per run: the fake server keeps remote accounts it has seen, whatever test made them
        let lockedHandle = uniqueName "lockd"
        let openHandle = uniqueName "opend"
        let lockedId = $"u-{lockedHandle}"
        let openId = $"u-{openHandle}"
        megafono.FakeApp.AddUser { FakeApp.user lockedId lockedHandle with Name = "Locked Alex"; Locked = true; ProfileUrl = $"https://megafono.test/u/{lockedHandle}" }
        megafono.FakeApp.AddUser { FakeApp.user openId openHandle with Name = "Open Door"; Locked = false; ProfileUrl = $"https://megafono.test/u/{openHandle}" }

        use! carol = server.CreateUser "carol"
        use! dave = server.CreateUser "dave"
        use! erin = server.CreateUser "erin"

        let find (client: FediClient) (handle: string) =
            task {
                let! found = client.SearchAccount(handle, true)
                return found.Value
            }

        // 1. A follow of a locked user waits; the app approves through the internal API
        let! alex = find carol $"{lockedHandle}@megafono.test"
        let! pending = carol.Follow alex.Id
        Assert.True(pending.Requested || pending.Following)
        let! requested = eventFor megafono "follow.requested" carol.Username
        Assert.Equal(lockedId, get requested.Json "user.id")
        Assert.Equal("pending", get requested.Json "follow.status")
        Assert.Equal("inbound", get requested.Json "follow.direction")
        let! approved = answer megafono lockedId (get requested.Json "follow.id") "approve"
        Assert.Equal(Net.HttpStatusCode.OK, approved)
        let! carolFollows =
            pollFor "carol follows alex" (fun () ->
                task {
                    let! r = carol.Relationship alex.Id
                    return (if r.Following then Some r else None)
                })
        Assert.False carolFollows.Requested
        let! acceptedEvent = eventFor megafono "follow.accepted" carol.Username
        Assert.Equal("accepted", get acceptedEvent.Json "follow.status")

        // 2. A second user is rejected: the request is cleared and never becomes a follow
        let! alexForDave = find dave $"{lockedHandle}@megafono.test"
        let! _ = dave.Follow alexForDave.Id
        let! daveRequest = eventFor megafono "follow.requested" dave.Username
        let! rejected = answer megafono lockedId (get daveRequest.Json "follow.id") "reject"
        Assert.Equal(Net.HttpStatusCode.OK, rejected)
        let! _ =
            pollFor "dave's request cleared" (fun () ->
                task {
                    let! r = dave.Relationship alexForDave.Id
                    return (if not r.Requested && not r.Following then Some r else None)
                })
        let! rejectedEvent = eventFor megafono "follow.rejected" dave.Username
        Assert.Equal("rejected", get rejectedEvent.Json "follow.status")

        // 3. An unlocked user is accepted without any approval
        let! open' = find erin $"{openHandle}@megafono.test"
        let! _ = erin.Follow open'.Id
        let! _ =
            pollFor "erin follows open" (fun () ->
                task {
                    let! r = erin.Relationship open'.Id
                    return (if r.Following then Some r else None)
                })
        let! openAccepted = eventFor megafono "follow.accepted" erin.Username
        Assert.Equal(openId, get openAccepted.Json "user.id")
        Assert.Empty(events megafono "follow.requested" |> List.filter (fun e -> get e.Json "user.id" = openId))

        // 4. Unfollow reaches the app
        let! _ = carol.Unfollow alex.Id
        let! removed = eventFor megafono "follow.removed" carol.Username
        Assert.Equal("removed", get removed.Json "follow.status")

        // 5. A body that does not match its signature is refused, even under a real user's key id
        let actorId = get requested.Json "actor.id"
        let before = (events megafono "follow.accepted").Length
        let! forged = forgedFollow megafono openId actorId (keyIdOf actorId)
        Assert.Equal(Net.HttpStatusCode.Unauthorized, forged)
        do! Task.Delay 1000
        Assert.Equal(before, (events megafono "follow.accepted").Length)

        // What the server actually sent: which inbox, which algorithm, which key id form
        let posts = megafono.InboxRequests |> List.filter (fun r -> r.KeyId.StartsWith $"https://{server.Domain}/")
        Assert.NotEmpty posts
        // The server's own deliveries are accepted, with whichever algorithm and key id form it uses; only the forgery is 401
        Assert.All(posts |> List.filter (fun r -> r.Status <> 401), fun r -> Assert.Equal(202, r.Status))
        Assert.Equal(1, posts |> List.filter (fun r -> r.Status = 401) |> List.length)
    }

type InboundFollowTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US2 GoToSocial follows a locked user with approval, rejection, unlocked, unfollow and a forged request`` () =
        task {
            let! gts = net.GoToSocial()
            // GoToSocial keys: https://gts.test/users/<name>/main-key
            do! inboundFlow gts gts.Front (fun actorId -> actorId + "/main-key") net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonInboundFollowTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US2 Mastodon follows a locked user with approval, rejection, unlocked, unfollow and a forged request`` () =
        task {
            let! mastodon = net.Mastodon()
            // Mastodon keys: https://mastodon.test/ap/users/<id>#main-key
            do! inboundFlow mastodon mastodon.Front (fun actorId -> actorId + "#main-key") net pg
        }
