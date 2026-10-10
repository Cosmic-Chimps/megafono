/// Smoke tests for the Mastodon side of the interop harness (R-15, T007). Slow (a minute or more),
/// so they run in their own suite: `dotnet test ... -- --filter-trait "Suite=mastodon"`.
module Megafono.Interop.Tests.MastodonHarnessTests

open System
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Megafono.Interop.Tests.GoToSocialFixture
open Megafono.Interop.Tests.MastodonFixture

[<Trait("Suite", "mastodon")>]
type MastodonHarnessTests(fx: MastodonFixture) =

    [<Fact>]
    member _.``the user token is accepted by verify_credentials`` () =
        task {
            use client = fx.AuthedClient()
            let! resp = client.GetAsync $"https://{MastodonDomain}/api/v1/accounts/verify_credentials"
            let! body = resp.Content.ReadAsStringAsync()
            Assert.True(resp.IsSuccessStatusCode, body)
            use doc = JsonDocument.Parse(body)
            Assert.Equal(fx.Username, doc.RootElement.GetProperty("username").GetString())
            Assert.Equal($"https://{MastodonDomain}/@{fx.Username}", doc.RootElement.GetProperty("url").GetString())
        }

    [<Fact>]
    member _.``Mastodon dials the in-process host through the megafono.test front`` () =
        task {
            use client = fx.AuthedClient()
            let! resp =
                client.GetAsync $"https://{MastodonDomain}/api/v2/search?q=%%40nobody%%40{MegafonoDomain}&resolve=true&type=accounts"
            let! body = resp.Content.ReadAsStringAsync()
            Assert.True(resp.IsSuccessStatusCode, body)

            let deadline = DateTime.UtcNow + TimeSpan.FromSeconds 30.0
            let find () =
                fx.Probe.Seen
                |> Seq.tryFind (fun r -> r.PathAndQuery.StartsWith "/.well-known/webfinger" && r.Host = MegafonoDomain)
            let mutable seen = find ()
            while seen.IsNone && DateTime.UtcNow < deadline do
                do! Task.Delay 250
                seen <- find ()
            if seen.IsNone then
                let! logs = fx.WebLogs()
                failwith $"Mastodon never requested the host. Web logs:\n{logs}"
            Assert.Contains($"acct:nobody@{MegafonoDomain}", Uri.UnescapeDataString seen.Value.PathAndQuery)
        }

    interface IClassFixture<MastodonFixture>
