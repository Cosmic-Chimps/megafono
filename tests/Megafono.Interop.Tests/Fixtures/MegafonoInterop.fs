/// The real Megáfono, in the test process, reachable as `https://megafono.test` through the Caddy front (R-15):
/// its own Postgres database, a fake app for the actor lookup, and a test-only network override so that its
/// outbound requests to `gts.test` / `mastodon.test` reach the mapped Caddy ports while TLS still validates
/// against the test CA (`SafeHttp.TestNetwork`).
module Megafono.Interop.Tests.MegafonoInterop

open System
open System.Security.Cryptography
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Megafono.Infrastructure
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FederationContext

open Npgsql
open Megafono.Domain

let token = "interop-token"
let webhookSecret = "interop-secret"

// The fake servers (GoToSocial, Mastodon) live for the whole run and cache the keys they have seen for
// `megafono.test`. Every test starts Megáfono on a fresh database, so without this each one would invent a new
// instance actor key and the server's cached copy would reject its signed fetches. A real deployment keeps its key;
// the tests keep one per run: one master key, and the instance actor's key stored encrypted under it.
let private masterKey = RandomNumberGenerator.GetBytes 32
let private vault = KeyVault.masterKeys masterKey None
let private instanceActorKey = lazy (KeyVault.createActorKey vault (Ids.instanceActorId MegafonoDomain))

/// The per-run master key, base64, for hosts that are not started through `start` (the deployment test's container)
let masterKeyBase64 = Convert.ToBase64String masterKey

/// Stores the per-run instance actor key in a fresh database (creating the schema first)
let seedInstanceKey (connectionString: string) : Task =
    task {
        use dataSource = NpgsqlDataSource.Create connectionString
        do! Database.initializeSchema dataSource
        let key = instanceActorKey.Value

        use command =
            dataSource.CreateCommand "INSERT INTO instance_actor (id, public_key_pem, enc_private_key) VALUES (1, @pem, @enc)"

        command.Parameters.AddWithValue("pem", key.PublicKeyPem) |> ignore
        command.Parameters.AddWithValue("enc", key.EncryptedPrivateKey) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

/// A 1x1 PNG, served by the host as the avatar the fake app points to
let private avatarPng =
    Convert.FromBase64String "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg=="

/// Test-only: answers `GET /avatar.png` before Megáfono's own routes (Megáfono has no media of its own)
type private AvatarFilter() =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) : Action<IApplicationBuilder> =
            Action<IApplicationBuilder>(fun app ->
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx continuation ->
                        if ctx.Request.Path.Value = "/avatar.png" then
                            ctx.Response.ContentType <- "image/png"
                            ctx.Response.Body.WriteAsync(avatarPng, 0, avatarPng.Length)
                        else
                            continuation.Invoke())
                )
                |> ignore

                next.Invoke app)

/// Test-only: stands in for the app's profile pages. In a real deployment the gateway sends `/u/*` to the app, which
/// links its profile to the actor (R-03): `<link rel="alternate" type="application/activity+json">` in the page and
/// the same as a `Link` header. This serves `GET /u/{handle}` for the fake app's users.
type private ProfilePageFilter(fakeApp: FakeApp.FakeApp) =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) : Action<IApplicationBuilder> =
            Action<IApplicationBuilder>(fun app ->
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx continuation ->
                        let path = ctx.Request.Path.Value

                        if ctx.Request.Method = "GET" && path.StartsWith "/u/" then
                            let handle = path.Substring 3

                            match fakeApp.Users.Values |> Seq.tryFind (fun u -> u.Handle = handle) with
                            | Some user ->
                                let actor = $"https://megafono.test/ap/actors/{user.Id}"
                                ctx.Response.ContentType <- "text/html; charset=utf-8"
                                ctx.Response.Headers["Link"] <- $"<{actor}>; rel=\"alternate\"; type=\"application/activity+json\""

                                ctx.Response.WriteAsync
                                    $"""<!doctype html><html><head><title>{user.Name}</title><link rel="alternate" type="application/activity+json" href="{actor}"></head><body><h1>{user.Name}</h1></body></html>"""
                            | None ->
                                ctx.Response.StatusCode <- 404
                                Task.CompletedTask
                        else
                            continuation.Invoke())
                )
                |> ignore

                next.Invoke app)

/// Test-only: stands in for the app's episode pages. `GET /episodes/{objectId}` is an HTML page whose
/// `<link rel="alternate" type="application/activity+json">` and `Link` header point at the thread post (R-21).
type private EpisodePageFilter() =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) : Action<IApplicationBuilder> =
            Action<IApplicationBuilder>(fun app ->
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx continuation ->
                        let path = ctx.Request.Path.Value

                        if ctx.Request.Method = "GET" && path.StartsWith "/episodes/" then
                            let post = $"https://megafono.test/ap/objects/{path.Substring 10}"
                            ctx.Response.ContentType <- "text/html; charset=utf-8"
                            ctx.Response.Headers["Link"] <- $"<{post}>; rel=\"alternate\"; type=\"application/activity+json\""

                            ctx.Response.WriteAsync
                                $"""<!doctype html><html><head><title>Episode</title><link rel="alternate" type="application/activity+json" href="{post}"></head><body><h1>Episode</h1></body></html>"""
                        else
                            continuation.Invoke())
                )
                |> ignore

                next.Invoke app)

/// What one POST to an inbox looked like on the wire, and how Megáfono answered
type InboxRequest =
    { Path: string
      /// `algorithm` and `keyId` of the Signature header, `headers` too; the signature value itself is dropped
      Algorithm: string
      KeyId: string
      SignedHeaders: string
      /// The request body as sent (activities are small)
      Body: string
      Status: int }

let private signatureParam (header: string) (name: string) =
    let m = Text.RegularExpressions.Regex.Match(header, name + "=\"([^\"]*)\"")
    if m.Success then m.Groups[1].Value else ""

/// Test-only: records every POST to an inbox (path, signature parameters, status) in front of Megáfono's own routes
type private InboxLogFilter(log: Collections.Concurrent.ConcurrentQueue<InboxRequest>) =
    interface IStartupFilter with
        member _.Configure(next: Action<IApplicationBuilder>) : Action<IApplicationBuilder> =
            Action<IApplicationBuilder>(fun app ->
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx continuation ->
                        task {
                            let isInbox = ctx.Request.Method = "POST" && ctx.Request.Path.Value.EndsWith "inbox"
                            let mutable body = ""

                            if isInbox then
                                // Read the body here and hand the pipeline a copy of it
                                use buffer = new IO.MemoryStream()
                                do! ctx.Request.Body.CopyToAsync buffer
                                body <- Text.Encoding.UTF8.GetString(buffer.ToArray())
                                ctx.Request.Body <- new IO.MemoryStream(buffer.ToArray())

                            do! continuation.Invoke()

                            if isInbox then
                                let signature = string ctx.Request.Headers["Signature"]

                                log.Enqueue
                                    { Path = ctx.Request.Path.Value
                                      Algorithm = signatureParam signature "algorithm"
                                      KeyId = signatureParam signature "keyId"
                                      SignedHeaders = signatureParam signature "headers"
                                      Body = body
                                      Status = ctx.Response.StatusCode }
                        }
                        :> Task)
                )
                |> ignore

                next.Invoke app)

type InteropMegafono(app: WebApplication, lease: PortLease, fakeApp: FakeApp.FakeApp, inboxLog: Collections.Concurrent.ConcurrentQueue<InboxRequest>) =
    member _.App = app
    /// Every inbox POST that arrived, in order
    member _.InboxRequests = inboxLog |> Seq.toList
    /// The federation port on loopback (the same listener `https://megafono.test` reaches through Caddy)
    member _.FederationUrl = $"http://127.0.0.1:{lease.Port}"

    /// The internal API (never behind Caddy); needs `MegafonoInterop.token`
    member _.InternalUrl =
        let ports = PortGuard.boundPorts (app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()) { Federation = lease.Port; Internal = 0; BindAddress = "0.0.0.0" }
        $"http://127.0.0.1:{ports.Internal}"

    /// The fake host app: add users here
    member _.FakeApp = fakeApp
    member _.Services = app.Services
    /// The avatar URL the fake app can give out (served by the host through the megafono.test front)
    member _.AvatarUrl = "https://megafono.test/avatar.png"

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        do! app.StopAsync()
                        do! app.DisposeAsync()
                    finally
                        (lease :> IDisposable).Dispose()

                    do! (fakeApp :> IAsyncDisposable).DisposeAsync().AsTask()
                }
            )

/// Starts Megáfono with `Domain=megafono.test` on the leased host port, a fresh database, and the fake app.
/// `fronts` are the fake servers Megáfono must be able to call (domain and their Caddy container).
let start (net: FederationNetwork) (pg: PostgresFixture) (fronts: (string * IContainer) list) : Task<InteropMegafono> =
    task {
        let! lease = net.LeasePort()

        try
            let! connectionString = pg.CreateDatabase()
            do! seedInstanceKey connectionString
            let! fakeApp = FakeApp.start token webhookSecret

            let testNetwork: SafeHttp.TestNetwork =
                { Redirect =
                    fun host ->
                        fronts
                        |> List.tryFind (fun (domain, _) -> domain = host)
                        |> Option.map (fun (_, container) -> container.Hostname, int (container.GetMappedPublicPort 443))
                  TrustedRoot = net.Pki.CaCert }

            let inboxLog = Collections.Concurrent.ConcurrentQueue<InboxRequest>()

            let configure (services: IServiceCollection) =
                services.AddLogging(fun l -> l.ClearProviders() |> ignore) |> ignore
                services.AddSingleton<SafeHttp.TestNetwork>(testNetwork) |> ignore
                services.AddSingleton<IStartupFilter, AvatarFilter>() |> ignore
                services.AddSingleton<IStartupFilter>(ProfilePageFilter fakeApp) |> ignore
                services.AddSingleton<IStartupFilter, EpisodePageFilter>() |> ignore
                services.AddSingleton<IStartupFilter>(InboxLogFilter inboxLog) |> ignore

            let overrides =
                [ "Megafono:Domain", MegafonoDomain
                  "Megafono:MasterKey", Convert.ToBase64String masterKey
                  "Megafono:App:BaseUrl", fakeApp.Url
                  "Megafono:App:Token", token
                  "Megafono:App:WebhookSecret", webhookSecret
                  "Megafono:Ports:Federation", string lease.Port
                  "Megafono:Ports:Internal", "0"
                  // The Caddy front reaches the host through the testcontainers tunnel, not through loopback
                  "Megafono:Ports:BindAddress", "0.0.0.0"
                  "Megafono:Federation:AllowPrivateAddresses", "true"
                  "ConnectionStrings:Database", connectionString ]
                // `MEGAFONO_CAPTURE_DIR=<dir> dotnet test ...` keeps every verified inbound request as a fixture (T085)
                @ (match Environment.GetEnvironmentVariable "MEGAFONO_CAPTURE_DIR" with
                   | null
                   | "" -> []
                   | dir -> [ "Megafono:Testing:CaptureInbound", dir ])

            let app = Megafono.Program.buildAppWith configure [| "--environment"; "Development" |] overrides
            do! app.StartAsync()
            return new InteropMegafono(app, lease, fakeApp, inboxLog)
        with e ->
            (lease :> IDisposable).Dispose()
            return raise e
    }
