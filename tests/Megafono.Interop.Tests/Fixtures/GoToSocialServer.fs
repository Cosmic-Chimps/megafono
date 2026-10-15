/// GoToSocial for the interop harness (R-15, T006): a pinned container (SQLite) behind a TLS Caddy
/// aliased `gts.test`, users created with `gotosocial admin account create` and tokens obtained through
/// a scripted OAuth sign-in (no browser).
module Megafono.Interop.Tests.GoToSocialServer

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

[<Literal>]
let GtsImage = "docker.io/superseriousbusiness/gotosocial:0.22.2"

let private password = "Sup3r-secret-pass-123"

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

type GoToSocialServer private (ctx: FederationContext, gts: IContainer, front: IContainer) =
    // SQLite and `admin account create` do not like concurrent writers.
    let createGate = new SemaphoreSlim(1, 1)

    member _.Container = gts
    member _.Front = front

    member _.Logs() =
        task {
            let! stdout, stderr = gts.GetLogsAsync()
            return stdout + stderr
        }

    /// Registers an OAuth app, signs in through the web form, authorizes it and exchanges the code.
    /// The `client` must keep cookies and must not follow redirects (`TestCa.httpClient` does both).
    member private _.ObtainToken(client: HttpClient, email: string) =
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
            Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode)
            Assert.Equal("/auth/sign_in", string first.Headers.Location)

            // The sign-in form posts `username` (the e-mail address) and `password`.
            let! signIn = client.PostAsync($"{baseUri}/auth/sign_in", form [ "username", email; "password", password ])
            Assert.Equal(HttpStatusCode.Found, signIn.StatusCode)
            Assert.Equal("/oauth/authorize", string signIn.Headers.Location)

            // The authorize page is a single "Allow" form posting to /oauth/authorize (no fields: the
            // pending authorization lives in the session cookie).
            let! authorize = client.GetAsync authorizeUrl
            let! authorizeHtml = authorize.Content.ReadAsStringAsync()
            Assert.Equal(HttpStatusCode.OK, authorize.StatusCode)
            Assert.Contains("""<form action="/oauth/authorize" method="POST">""", authorizeHtml)

            // Redirect URI `urn:ietf:wg:oauth:2.0:oob` answers 302 to /oauth/oob?code=<code>.
            let! allow = client.PostAsync($"{baseUri}/oauth/authorize", form [])
            Assert.Equal(HttpStatusCode.Found, allow.StatusCode)
            let codeMatch = Regex.Match(string allow.Headers.Location, "[?&]code=([^&]+)")
            Assert.True(codeMatch.Success, $"no authorization code in Location {allow.Headers.Location}")

            let! tokenResp =
                client.PostAsync(
                    $"{baseUri}/oauth/token",
                    form
                        [ "grant_type", "authorization_code"
                          "client_id", clientId
                          "client_secret", clientSecret
                          "redirect_uri", redirect
                          "code", codeMatch.Groups[1].Value ]
                )
            let! tokenJson = tokenResp.Content.ReadAsStringAsync()
            Assert.True(tokenResp.IsSuccessStatusCode, tokenJson)
            use tokenDoc = JsonDocument.Parse(tokenJson)
            return tokenDoc.RootElement.GetProperty("access_token").GetString()
        }

    member this.CreateUser(prefix: string) : Task<FediClient> =
        task {
            let name = uniqueName prefix
            let email = $"{name}@{GtsDomain}"
            do! createGate.WaitAsync()
            try
                let! result =
                    gts.ExecAsync(
                        [| "/gotosocial/gotosocial"; "admin"; "account"; "create"; "--username"; name; "--email"; email; "--password"; password |]
                    )
                if result.ExitCode <> Nullable 0L then
                    failwith $"gotosocial admin account create failed ({result.ExitCode}): {result.Stderr} {result.Stdout}"
            finally
                createGate.Release() |> ignore
            // A fresh client per user: the sign-in flow keeps a session cookie.
            let http = ctx.HttpClientFor front
            let! token = this.ObtainToken(http, email)
            return new FediClient(http, $"https://{GtsDomain}", token, name, GtsDomain)
        }

    interface IFediServer with
        member _.Domain = GtsDomain
        member this.CreateUser prefix = this.CreateUser prefix

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    do! front.DisposeAsync().AsTask()
                    do! gts.DisposeAsync().AsTask()
                }
            )

    static member StartAsync(ctx: FederationContext) : Task<GoToSocialServer> =
        task {
            let gts =
                ContainerBuilder(GtsImage)
                    .WithNetwork(ctx.Network)
                    .WithNetworkAliases("gotosocial")
                    .WithEnvironment("GTS_HOST", GtsDomain)
                    .WithEnvironment("GTS_ACCOUNT_DOMAIN", GtsDomain)
                    .WithEnvironment("GTS_PROTOCOL", "https")
                    .WithEnvironment("GTS_DB_TYPE", "sqlite")
                    .WithEnvironment("GTS_DB_ADDRESS", "/gotosocial/storage/sqlite.db")
                    .WithEnvironment("GTS_LETSENCRYPT_ENABLED", "false")
                    // 0 or less disables rate limiting entirely.
                    .WithEnvironment("GTS_ADVANCED_RATE_LIMIT_REQUESTS", "0")
                    .WithEnvironment("GTS_TRUSTED_PROXIES", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
                    // Without this GTS refuses to dial the Docker network ("dial within blocked / reserved IP range").
                    .WithEnvironment("GTS_HTTP_CLIENT_ALLOW_IPS", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
                    .WithEnvironment("SSL_CERT_FILE", "/certs/ca.pem")
                    .WithResourceMapping(bytes ctx.Pki.CaPem, "/certs/ca.pem")
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("listening on"))
                    .Build()
            let front = ctx.CaddyFront(GtsDomain, "gotosocial:8080")
            do! ctx.Time("gts: start containers", Task.WhenAll(gts.StartAsync(), front.StartAsync()))
            use probe = ctx.HttpClientFor front
            do! ctx.Time("gts: ready", waitUntilReady probe (TimeSpan.FromSeconds 90.0))
            return GoToSocialServer(ctx, gts, front)
        }
