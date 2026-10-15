/// Smoke tests for the Mastodon side of the interop harness (R-15, T007/T008). Mastodon takes about
/// 30 s to start (lazily, on first use), so these carry the `mastodon` suite trait and can be filtered:
/// `dotnet test --project tests/Megafono.Interop.Tests -- --filter-not-trait "Suite=mastodon"`.
module Megafono.Interop.Tests.MastodonHarnessTests

open System
open Xunit
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

[<Trait("Suite", "mastodon")>]
type MastodonHarnessTests(net: FederationNetwork) =

    [<Fact>]
    member _.``the user token is accepted by verify_credentials`` () =
        task {
            let! mastodon = net.Mastodon()
            use! alice = mastodon.CreateUser "alice"
            let! me = alice.Me()
            Assert.Equal(alice.Username, me.Username)
            Assert.Equal($"https://{MastodonDomain}/@{alice.Username}", me.Url)
        }

    [<Fact>]
    member _.``Mastodon dials the in-process host through the megafono.test front`` () =
        task {
            let! mastodon = net.Mastodon()
            use! alice = mastodon.CreateUser "alice"
            use! probe = net.AttachProbe()
            let! _ = alice.SearchAccount($"nobody@{MegafonoDomain}", true)
            let! seen =
                pollUntil (TimeSpan.FromSeconds 30.0) "Mastodon to request webfinger on the host" (fun () ->
                    task {
                        return
                            probe.Seen
                            |> Seq.tryFind (fun r -> r.PathAndQuery.StartsWith "/.well-known/webfinger" && r.Host = MegafonoDomain)
                    })
            Assert.Contains($"acct:nobody@{MegafonoDomain}", Uri.UnescapeDataString seen.PathAndQuery)
        }

    [<Fact>]
    member _.``the FediClient flows work between two local Mastodon users`` () =
        task {
            let! mastodon = net.Mastodon()
            do! HarnessTests.localFollowFlow mastodon
        }
