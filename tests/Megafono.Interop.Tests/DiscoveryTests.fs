/// User Story 1 against real servers (T035): a fediverse user searches `@alex@megafono.test` and finds the profile
/// the fake app returned: name, note, avatar. Also the first test that runs the real Megáfono behind the
/// `megafono.test` Caddy front, so it proves the host-attachment design of R-15.
module Megafono.Interop.Tests.DiscoveryTests

open System
open System.Threading.Tasks
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

type DiscoveryTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 GoToSocial finds Alex with the name, note and avatar of the fake app`` () =
        task {
            let! gts = net.GoToSocial()
            do! discover gts net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonDiscoveryTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 Mastodon finds Alex with the name, note and avatar of the fake app`` () =
        task {
            let! mastodon = net.Mastodon()
            do! discover mastodon net pg
        }
