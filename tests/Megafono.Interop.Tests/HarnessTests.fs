/// Smoke tests for the interop harness (R-15, T006): they prove the TLS network, the GoToSocial
/// account and token, and the host-port exposure work before any federation test relies on them.
module Megafono.Interop.Tests.HarnessTests

open System
open System.Linq
open System.Text.Json
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Interop.Tests.GoToSocialFixture

/// Polls until `check` returns Some, or fails with `describe ()` after `timeout`.
let private pollUntil (timeout: TimeSpan) (describe: unit -> Task<string>) (check: unit -> 'a option) =
    task {
        let deadline = DateTime.UtcNow + timeout
        let mutable result = check ()
        while result.IsNone && DateTime.UtcNow < deadline do
            do! Task.Delay 250
            result <- check ()
        match result with
        | Some r -> return r
        | None ->
            let! detail = describe ()
            return failwith $"timed out after {timeout}: {detail}"
    }

type HarnessTests(fx: GoToSocialFixture) =

    [<Fact>]
    member _.``the user token is accepted by verify_credentials`` () =
        task {
            use client = fx.AuthedClient()
            let! resp = client.GetAsync $"https://{GtsDomain}/api/v1/accounts/verify_credentials"
            let! body = resp.Content.ReadAsStringAsync()
            Assert.True(resp.IsSuccessStatusCode, body)
            use doc = JsonDocument.Parse(body)
            Assert.Equal(fx.Username, doc.RootElement.GetProperty("username").GetString())
            Assert.Equal($"https://{GtsDomain}/@{fx.Username}", doc.RootElement.GetProperty("url").GetString())
        }

    [<Fact>]
    member _.``a container on the network reaches the in-process host over verified TLS`` () =
        task {
            // A plain curl container that trusts the test CA stands in for "a peer on the network".
            let curl =
                ContainerBuilder(CurlImage)
                    .WithNetwork(fx.Network)
                    .WithEntrypoint("sleep", "infinity")
                    .WithResourceMapping(Text.Encoding.UTF8.GetBytes fx.Pki.CaPem, "/certs/ca.pem")
                    .Build()
            do! curl.StartAsync()
            try
                let before = fx.Probe.Seen.Count
                let! result =
                    curl.ExecAsync [| "curl"; "-sS"; "--fail"; "--cacert"; "/certs/ca.pem"; $"https://{MegafonoDomain}/probe.json" |]
                Assert.True((result.ExitCode = Nullable 0L), $"curl failed: {result.Stderr}")
                Assert.Equal("""{"ok":true}""", result.Stdout.Trim())
                let seen = fx.Probe.Seen.Skip(before).Single(fun r -> r.PathAndQuery = "/probe.json")
                Assert.Equal(MegafonoDomain, seen.Host)

                // And it does not trust anything else: without the CA the handshake must fail.
                let! untrusted = curl.ExecAsync [| "curl"; "-sS"; "--fail"; $"https://{MegafonoDomain}/probe.json" |]
                Assert.NotEqual(Nullable 0L, untrusted.ExitCode)
            finally
                curl.DisposeAsync().AsTask().Wait()
        }

    [<Fact>]
    member _.``GoToSocial dials the in-process host through the megafono.test front`` () =
        task {
            use client = fx.AuthedClient()
            let! resp =
                client.GetAsync $"https://{GtsDomain}/api/v2/search?q=%%40nobody%%40{MegafonoDomain}&resolve=true&type=accounts"
            let! body = resp.Content.ReadAsStringAsync()
            Assert.True(resp.IsSuccessStatusCode, body)

            let! seen =
                pollUntil
                    (TimeSpan.FromSeconds 30.0)
                    (fun () -> task { let! logs = fx.GtsLogs() in return logs })
                    (fun () ->
                        fx.Probe.Seen
                        |> Seq.tryFind (fun r -> r.PathAndQuery.StartsWith "/.well-known/webfinger" && r.Host = MegafonoDomain))
            Assert.Contains($"acct:nobody@{MegafonoDomain}", Uri.UnescapeDataString seen.PathAndQuery)
            Assert.Contains("gotosocial", seen.UserAgent, StringComparison.OrdinalIgnoreCase)
        }

    interface IClassFixture<GoToSocialFixture>
