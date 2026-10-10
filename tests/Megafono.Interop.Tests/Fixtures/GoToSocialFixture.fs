/// Spike fixture for the real-server interop harness (R-15, T006): a Docker network with
/// GoToSocial behind a TLS-terminating Caddy (`gts.test`), a second Caddy (`megafono.test`) that
/// proxies to a Kestrel host running in the test process, and a user token obtained without a browser.
module Megafono.Interop.Tests.GoToSocialFixture

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Text
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Networks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Xunit

[<Literal>]
let GtsImage = "docker.io/superseriousbusiness/gotosocial:0.22.2"

[<Literal>]
let CaddyImage = "docker.io/library/caddy:2.11.7-alpine"

[<Literal>]
let CurlImage = "docker.io/curlimages/curl:8.22.0"

[<Literal>]
let GtsDomain = "gts.test"

[<Literal>]
let MegafonoDomain = "megafono.test"

/// What the in-process host saw, recorded so tests can assert on what a container sent.
type SeenRequest =
    { Method: string
      PathAndQuery: string
      Host: string
      UserAgent: string }

/// A minimal real Kestrel host on `0.0.0.0:<free port>` (stands in for Megáfono and the fake app).
type ProbeHost(app: WebApplication, port: int, seen: ConcurrentQueue<SeenRequest>) =
    member _.Port = port
    member _.Seen = seen
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(task {
                do! app.StopAsync()
                do! app.DisposeAsync()
            })

let startProbeHost () : Task<ProbeHost> =
    task {
        let builder = WebApplication.CreateSlimBuilder()
        builder.WebHost.UseKestrel(fun (o: KestrelServerOptions) -> o.Listen(System.Net.IPAddress.Any, 0)) |> ignore
        builder.Logging.ClearProviders() |> ignore
        let app = builder.Build()
        let seen = ConcurrentQueue<SeenRequest>()
        app.Run(
            RequestDelegate(fun ctx ->
                seen.Enqueue
                    { Method = ctx.Request.Method
                      PathAndQuery = ctx.Request.Path.Value + ctx.Request.QueryString.Value
                      Host = ctx.Request.Host.Value
                      UserAgent = string ctx.Request.Headers.UserAgent }
                if ctx.Request.Path.Value = "/probe.json" then
                    ctx.Response.ContentType <- "application/json"
                    ctx.Response.WriteAsync """{"ok":true}"""
                else
                    ctx.Response.StatusCode <- 404
                    Task.CompletedTask)
        )
        do! app.StartAsync()
        let address = app.Urls |> Seq.head
        return ProbeHost(app, Uri(address).Port, seen)
    }

let bytes (s: string) : byte[] = Encoding.UTF8.GetBytes s

let private caddyfile (domain: string) (upstream: string) =
    $"""{{
	auto_https off
	admin off
}}
https://{domain} {{
	tls /certs/{domain}.crt.pem /certs/{domain}.key.pem
	reverse_proxy {upstream}
}}
"""

let caddyFront (network: INetwork) (pki: TestCa.TestPki) (domain: string) (upstream: string) =
    let leaf = pki.Leaf domain
    ContainerBuilder(CaddyImage)
        .WithNetwork(network)
        .WithNetworkAliases(domain)
        .WithPortBinding(443, true)
        .WithResourceMapping(bytes (caddyfile domain upstream), "/etc/caddy/Caddyfile")
        .WithResourceMapping(bytes leaf.CertPem, $"/certs/{domain}.crt.pem")
        .WithResourceMapping(bytes leaf.KeyPem, $"/certs/{domain}.key.pem")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
        .Build()

let private gtsContainer (network: INetwork) (pki: TestCa.TestPki) =
    ContainerBuilder(GtsImage)
        .WithNetwork(network)
        .WithNetworkAliases("gotosocial")
        .WithEnvironment("GTS_HOST", GtsDomain)
        .WithEnvironment("GTS_ACCOUNT_DOMAIN", GtsDomain)
        .WithEnvironment("GTS_PROTOCOL", "https")
        .WithEnvironment("GTS_DB_TYPE", "sqlite")
        .WithEnvironment("GTS_DB_ADDRESS", "/gotosocial/storage/sqlite.db")
        .WithEnvironment("GTS_LETSENCRYPT_ENABLED", "false")
        .WithEnvironment("GTS_ADVANCED_RATE_LIMIT_REQUESTS", "0")
        .WithEnvironment("GTS_TRUSTED_PROXIES", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
        .WithEnvironment("GTS_HTTP_CLIENT_ALLOW_IPS", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
        .WithEnvironment("SSL_CERT_FILE", "/certs/ca.pem")
        .WithResourceMapping(bytes pki.CaPem, "/certs/ca.pem")
        .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("listening on"))
        .Build()

let timed (timings: ConcurrentDictionary<string, TimeSpan>) (name: string) (work: Task) =
    task {
        let sw = Diagnostics.Stopwatch.StartNew()
        do! work
        timings[name] <- sw.Elapsed
    }

/// GoToSocial logs "listening on" and answers 503 while it is still starting (migrations, WASM
/// compilation), so readiness is an actual 200 through the Caddy front.
let private waitUntilReady (client: HttpClient) (timeout: TimeSpan) =
    task {
        let deadline = DateTime.UtcNow + timeout
        let mutable ready = false
        let mutable last = ""
        while not ready do
            try
                let! resp = client.GetAsync $"https://{GtsDomain}/api/v1/instance"
                ready <- resp.IsSuccessStatusCode
                last <- string (int resp.StatusCode)
            with e -> last <- e.ToString()
            if not ready then
                if DateTime.UtcNow > deadline then failwith $"GoToSocial did not become ready; last: {last}"
                do! Task.Delay 500
    }

let private user = "alice"
let private email = "alice@gts.test"
let private password = "Sup3r-secret-pass-123"

/// Everything the spike builds. Starts the host, the network and the containers once.
type GoToSocialFixture() =
    let mutable pki: TestCa.TestPki option = None
    let mutable probe: ProbeHost option = None
    let mutable network: INetwork option = None
    let mutable gts: IContainer option = None
    let mutable gtsFront: IContainer option = None
    let mutable megafonoFront: IContainer option = None
    let mutable http: HttpClient option = None
    let mutable token = ""
    let timings = ConcurrentDictionary<string, TimeSpan>()

    member _.Pki = pki.Value
    member _.Probe = probe.Value
    member _.Network = network.Value
    member _.Gts = gts.Value
    member _.GtsFront = gtsFront.Value
    member _.MegafonoFront = megafonoFront.Value
    /// Trusts only the test CA; every request to a `*.test` host lands on the matching Caddy front.
    member _.Http = http.Value
    member _.AccessToken = token
    member _.Timings = timings

    member _.Username = user

    member _.GtsLogs() =
        task {
            let! stdout, stderr = gts.Value.GetLogsAsync()
            return stdout + stderr
        }

    /// An HTTP client for `https://gts.test` with the user's bearer token set.
    member this.AuthedClient() : HttpClient =
        let c = this.GtsClient()
        c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        c

    member this.GtsClient() : HttpClient =
        TestCa.httpClient this.Pki (Some(this.GtsFront.Hostname, int (this.GtsFront.GetMappedPublicPort 443)))

    member private this.Exec(container: IContainer, args: string list) =
        task {
            let! result = container.ExecAsync(Array.ofList args)
            if result.ExitCode <> Nullable 0L then
                failwith $"exec {String.Join(' ', args)} failed with {result.ExitCode}: {result.Stderr} {result.Stdout}"
            return result
        }

    /// Registers an OAuth app, signs in through the web form, authorizes it and exchanges the code.
    member private _.ObtainToken(client: HttpClient) =
        task {
            let baseUri = $"https://{GtsDomain}"
            let redirect = "urn:ietf:wg:oauth:2.0:oob"
            let form (pairs: (string * string) list) =
                new FormUrlEncodedContent(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)))

            let! appResp =
                client.PostAsync(
                    $"{baseUri}/api/v1/apps",
                    form [ "client_name", "megafono-harness"; "redirect_uris", redirect; "scopes", "read write follow" ]
                )
            appResp.EnsureSuccessStatusCode() |> ignore
            let! appJson = appResp.Content.ReadAsStringAsync()
            use appDoc = JsonDocument.Parse(appJson)
            let clientId = appDoc.RootElement.GetProperty("client_id").GetString()
            let clientSecret = appDoc.RootElement.GetProperty("client_secret").GetString()

            let authorizeUrl =
                $"{baseUri}/oauth/authorize?client_id={clientId}&redirect_uri={Uri.EscapeDataString redirect}&response_type=code&scope=read+write+follow"
            let! first = client.GetAsync authorizeUrl
            Assert.Equal(Net.HttpStatusCode.SeeOther, first.StatusCode)
            Assert.Equal("/auth/sign_in", string first.Headers.Location)

            let! signIn = client.PostAsync($"{baseUri}/auth/sign_in", form [ "username", email; "password", password ])
            Assert.Equal(Net.HttpStatusCode.Found, signIn.StatusCode)
            Assert.Equal("/oauth/authorize", string signIn.Headers.Location)

            // The authorize page is a single "Allow" form posting to /oauth/authorize (no fields: the
            // pending authorization lives in the session cookie).
            let! authorize = client.GetAsync authorizeUrl
            let! authorizeHtml = authorize.Content.ReadAsStringAsync()
            Assert.Equal(Net.HttpStatusCode.OK, authorize.StatusCode)
            Assert.Contains("""<form action="/oauth/authorize" method="POST">""", authorizeHtml)

            // Redirect URI `urn:ietf:wg:oauth:2.0:oob` answers 302 to /oauth/oob?code=<code>.
            let! allow = client.PostAsync($"{baseUri}/oauth/authorize", form [])
            Assert.Equal(Net.HttpStatusCode.Found, allow.StatusCode)
            let codeMatch = Regex.Match(string allow.Headers.Location, "[?&]code=([^&]+)")
            Assert.True(codeMatch.Success, $"no authorization code in Location {allow.Headers.Location}")
            let code = codeMatch.Groups[1].Value
            let! tokenResp =
                client.PostAsync(
                    $"{baseUri}/oauth/token",
                    form
                        [ "grant_type", "authorization_code"
                          "client_id", clientId
                          "client_secret", clientSecret
                          "redirect_uri", redirect
                          "code", code ]
                )
            let! tokenJson = tokenResp.Content.ReadAsStringAsync()
            Assert.True(tokenResp.IsSuccessStatusCode, tokenJson)
            use tokenDoc = JsonDocument.Parse(tokenJson)
            return tokenDoc.RootElement.GetProperty("access_token").GetString()
        }

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(
                task {
                    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "megafono-interop-" + Guid.NewGuid().ToString("N"))
                    pki <- Some(TestCa.create dir [ MegafonoDomain; GtsDomain ])

                    let! p = startProbeHost ()
                    probe <- Some p
                    // Must precede container creation: containers started afterwards can reach the host.
                    do! timed timings "expose host port" (TestcontainersSettings.ExposeHostPortsAsync(uint16 p.Port))

                    let net = NetworkBuilder().Build()
                    network <- Some net
                    do! net.CreateAsync()

                    let g = gtsContainer net pki.Value
                    gts <- Some g
                    let gf = caddyFront net pki.Value GtsDomain "gotosocial:8080"
                    gtsFront <- Some gf
                    let mf = caddyFront net pki.Value MegafonoDomain $"host.testcontainers.internal:{p.Port}"
                    megafonoFront <- Some mf

                    do! timed timings "start gts" (g.StartAsync())
                    do! timed timings "start caddy x2" (Task.WhenAll(gf.StartAsync(), mf.StartAsync()))

                    let c = this.GtsClient()
                    http <- Some c
                    do! timed timings "gts ready" (waitUntilReady c (TimeSpan.FromSeconds 90.0))
                    let! _ = this.Exec(g, [ "/gotosocial/gotosocial"; "admin"; "account"; "create"; "--username"; user; "--email"; email; "--password"; password ])
                    let sw = Diagnostics.Stopwatch.StartNew()
                    let! t = this.ObtainToken c
                    timings["token"] <- sw.Elapsed
                    token <- t
                }
            )

        member this.DisposeAsync() =
            ValueTask(
                task {
                    for c in [ megafonoFront; gtsFront; gts ] do
                        match c with
                        | Some c -> do! c.DisposeAsync().AsTask()
                        | None -> ()
                    match network with
                    | Some n -> do! n.DisposeAsync().AsTask()
                    | None -> ()
                    match probe with
                    | Some p -> do! (p :> IAsyncDisposable).DisposeAsync().AsTask()
                    | None -> ()
                    match pki with
                    | Some k -> try IO.Directory.Delete(k.Directory, true) with _ -> ()
                    | None -> ()
                }
            )
