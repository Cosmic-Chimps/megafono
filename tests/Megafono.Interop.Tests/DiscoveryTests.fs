/// User Story 1 against real servers (T035): a fediverse user searches `@alex@megafono.test` and finds the profile
/// the fake app returned: name, note, avatar. Also the first test that runs the real Megáfono behind the
/// `megafono.test` Caddy front, so it proves the host-attachment design of R-15.
module Megafono.Interop.Tests.DiscoveryTests

open System
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private alex (avatar: string) : FakeApp.FakeUser =
    { FakeApp.user "u-alex" "alex" with
        Name = "Alex Fake"
        Summary = Some "<p>Anime &amp; coffee</p>"
        IconUrl = Some avatar
        // On the same domain as the actor, as in a real deployment (the app sits behind the gateway). Mastodon ignores
        // an actor `url` on another host and shows the actor id instead; GoToSocial shows whatever it is given.
        ProfileUrl = "https://megafono.test/u/alex" }

/// US1 scenarios 1 and 2 for one server: the profile is found with the app's data; an unknown handle finds nothing
let private discover (server: IFediServer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg []
        megafono.FakeApp.AddUser(alex megafono.AvatarUrl)
        use! viewer = server.CreateUser "viewer"

        let! found = viewer.SearchAccount("alex@megafono.test", true)
        let account = Assert.IsType<Account>(found.Value)
        Assert.Equal("alex", account.Username)
        Assert.Equal("alex@megafono.test", account.Acct)
        Assert.Equal("Alex Fake", account.DisplayName)
        Assert.Contains("Anime", account.Note)
        Assert.Contains("coffee", account.Note)
        Assert.Equal("https://megafono.test/u/alex", account.Url)
        Assert.False account.Locked

        // The avatar is fetched by the server after the account is created: wait for the real image
        let! _avatar =
            pollUntil (TimeSpan.FromSeconds 45.0) "the avatar to be fetched" (fun () ->
                task {
                    let! again = viewer.SearchAccount("alex@megafono.test", true)
                    let a = again.Value.Avatar
                    return (if not (String.IsNullOrEmpty a) && not (a.Contains "default") then Some a else None)
                })

        // An unknown handle finds nothing, and Megáfono answered 404 rather than inventing a profile
        let! nobody = viewer.SearchAccount("nobody@megafono.test", true)
        Assert.True nobody.IsNone
    }

/// SC-001: pasting the app's profile URL. The profile page links to the actor (`<link rel=alternate>` and a `Link` header,
/// R-03); a server that follows that hop resolves the account. Mastodon does; GoToSocial is expected not to (inferred from
/// its source), and the test says so, so a change in either server is noticed.
let private discoverByProfileUrl (server: IFediServer) (expectFound: bool) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg []
        let handle = uniqueName "pasted"
        let profileUrl = $"https://megafono.test/u/{handle}"
        megafono.FakeApp.AddUser { FakeApp.user $"u-{handle}" handle with Name = "Pasted Alex"; ProfileUrl = profileUrl }
        use! viewer = server.CreateUser "viewer"

        if expectFound then
            let! account =
                pollFor "the profile URL to resolve" (fun () ->
                    task {
                        let! found = viewer.SearchQuery(profileUrl, true)
                        return found
                    })

            Assert.Equal($"{handle}@megafono.test", account.Acct)
            Assert.Equal("Pasted Alex", account.DisplayName)
        else
            // Give the server every chance: ask twice, a few seconds apart
            let! first = viewer.SearchQuery(profileUrl, true)
            do! Task.Delay 3000
            let! second = viewer.SearchQuery(profileUrl, true)
            Assert.True(first.IsNone && second.IsNone, "the server resolved a pasted profile URL; research R-03 expected it not to follow alternates")
    }

/// A handle with an inner dash (the shape Frunchy usernames take) is found and followed like any other
let private discoverDashed (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        // Unique per run, the dash kept inside: the server keeps remote accounts it has seen
        let handle = uniqueName "anime-fan"
        let userId = $"u-{handle}"
        megafono.FakeApp.AddUser { FakeApp.user userId handle with Name = "Anime Fan"; ProfileUrl = $"https://megafono.test/u/{handle}" }
        use! viewer = server.CreateUser "viewer"

        // Polled: under the load of the other interop tests the server's first lookup can come back empty
        let! account = pollFor "the dashed handle to resolve" (fun () -> viewer.SearchAccount($"{handle}@megafono.test", true))
        Assert.Equal(handle, account.Username)
        Assert.Equal($"{handle}@megafono.test", account.Acct)
        Assert.Equal("Anime Fan", account.DisplayName)

        let! _ = viewer.Follow account.Id

        let! _ =
            pollFor "the follow of the dashed handle to be accepted" (fun () ->
                task {
                    let! r = viewer.Relationship account.Id
                    return (if r.Following then Some r else None)
                })

        let accepted (e: FakeApp.ReceivedWebhook) =
            e.Type = "follow.accepted" && e.Json.["user"].["id"].GetValue<string>() = userId

        // The webhook and the Accept leave the same transaction through two workers: the server can show the follow
        // before the webhook reaches the app, so wait for it as well
        let! _ = pollFor "follow.accepted for the dashed handle" (fun () -> task { return megafono.FakeApp.Received |> List.tryFind accepted })
        ()
    }

type DiscoveryTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 GoToSocial finds Alex with the name, note and avatar of the fake app`` () =
        task {
            let! gts = net.GoToSocial()
            do! discover gts net pg
        }

    [<Fact>]
    member _.``SC-001 GoToSocial does not resolve a pasted profile URL, it needs the handle`` () =
        task {
            let! gts = net.GoToSocial()
            do! discoverByProfileUrl gts false net pg
        }

    [<Fact>]
    member _.``US1 GoToSocial finds and follows a handle with a dash`` () =
        task {
            let! gts = net.GoToSocial()
            do! discoverDashed gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonDiscoveryTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 Mastodon finds Alex with the name, note and avatar of the fake app`` () =
        task {
            let! mastodon = net.Mastodon()
            do! discover mastodon net pg
        }

    [<Fact>]
    member _.``SC-001 Mastodon finds Alex by the pasted profile URL`` () =
        task {
            let! mastodon = net.Mastodon()
            do! discoverByProfileUrl mastodon true net pg
        }

    [<Fact>]
    member _.``US1 Mastodon finds and follows a handle with a dash`` () =
        task {
            let! mastodon = net.Mastodon()
            do! discoverDashed mastodon mastodon.Front net pg
        }
