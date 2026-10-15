/// User Story 4 against real servers (T019, FR-030): the app's series account is a `Group` actor. GoToSocial and
/// Mastodon find it by handle, a remote user follows it and is accepted at once (unlocked), and its next public post
/// reaches the follower's home timeline. Mastodon reports `group: true` and no `bot` flag; what each server shows is
/// printed so the run records it.
module Megafono.Interop.Tests.GroupActorTests

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private publishPublic (megafono: MegafonoInterop.InteropMegafono) (userId: string) (content: string) =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        let body = JsonObject()
        body["audience"] <- JsonValue.Create "public"
        let inner = JsonObject()
        inner["type"] <- JsonValue.Create "Note"
        inner["content"] <- JsonValue.Create $"<p>{content}</p>"
        body["object"] <- inner
        use request = new HttpRequestMessage(HttpMethod.Post, $"{megafono.InternalUrl}/internal/actors/{userId}/objects")
        request.Content <- new StringContent(body.ToJsonString(), Text.Encoding.UTF8, "application/json")
        use! response = client.SendAsync request
        Assert.Equal(Net.HttpStatusCode.Created, response.StatusCode)
    }

let private groupFlow (server: IFediServer) (front: IContainer) (isMastodon: bool) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        // Unique per run: servers cache the actors and keys they have seen
        let handle = uniqueName "series"
        let groupId = $"g-{handle}"

        megafono.FakeApp.AddUser
            { FakeApp.user groupId handle with
                Name = "Series Group"
                Type = "Group"
                Locked = false
                Summary = Some "<p>A series, as a group</p>"
                ProfileUrl = $"https://megafono.test/series/{handle}" }

        use! carol = server.CreateUser "carol"

        // 1. found by handle
        let! found = carol.SearchAccount($"{handle}@megafono.test", true)
        Assert.True(found.IsSome, "the Group is found by its handle")
        let group = found.Value
        printfn "%s: Group account display_name=%s group=%b bot=%b locked=%b url=%s" server.Domain group.DisplayName group.Group group.Bot group.Locked group.Url
        Assert.False(group.Bot, "a Group is not flagged as a bot")
        if isMastodon then Assert.True(group.Group, "Mastodon reports group: true")

        // 2. followed at once
        let! _ = carol.Follow group.Id

        let! relationship =
            pollFor "carol follows the Group without approval" (fun () ->
                task {
                    let! r = carol.Relationship group.Id
                    return (if r.Following then Some r else None)
                })

        Assert.True relationship.Following
        Assert.False relationship.Requested

        // 3. its next public post reaches the follower
        let marker = "group-" + uniqueName "m"
        do! publishPublic megafono groupId marker

        let! seen =
            pollFor $"'{marker}' in the home timeline" (fun () ->
                task {
                    let! timeline = carol.HomeTimeline()
                    return timeline |> List.tryFind (fun s -> s.Content.Contains marker)
                })

        Assert.Equal("public", seen.Visibility)
        printfn "%s: Group post shown from %s (display_name=%s)" server.Domain seen.Account.Acct seen.Account.DisplayName
    }

type GroupActorTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US4 GoToSocial: a Group actor is found, followed at once, and its post reaches the follower`` () =
        task {
            let! gts = net.GoToSocial()
            do! groupFlow gts gts.Front false net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonGroupActorTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US4 Mastodon: a Group actor is found (group: true), followed at once, and its post reaches the follower`` () =
        task {
            let! mastodon = net.Mastodon()
            do! groupFlow mastodon mastodon.Front true net pg
        }
