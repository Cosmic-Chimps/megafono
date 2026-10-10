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

let token = "interop-token"
let webhookSecret = "interop-secret"

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

type InteropMegafono(app: WebApplication, lease: PortLease, fakeApp: FakeApp.FakeApp) =
    member _.App = app
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
            let! fakeApp = FakeApp.start token webhookSecret

            let testNetwork: SafeHttp.TestNetwork =
                { Redirect =
                    fun host ->
                        fronts
                        |> List.tryFind (fun (domain, _) -> domain = host)
                        |> Option.map (fun (_, container) -> container.Hostname, int (container.GetMappedPublicPort 443))
                  TrustedRoot = net.Pki.CaCert }

            let configure (services: IServiceCollection) =
                services.AddLogging(fun l -> l.ClearProviders() |> ignore) |> ignore
                services.AddSingleton<SafeHttp.TestNetwork>(testNetwork) |> ignore
                services.AddSingleton<IStartupFilter, AvatarFilter>() |> ignore

            let overrides =
                [ "Megafono:Domain", MegafonoDomain
                  "Megafono:MasterKey", Convert.ToBase64String(RandomNumberGenerator.GetBytes 32)
                  "Megafono:App:BaseUrl", fakeApp.Url
                  "Megafono:App:Token", token
                  "Megafono:App:WebhookSecret", webhookSecret
                  "Megafono:Ports:Federation", string lease.Port
                  "Megafono:Ports:Internal", "0"
                  // The Caddy front reaches the host through the testcontainers tunnel, not through loopback
                  "Megafono:Ports:BindAddress", "0.0.0.0"
                  "Megafono:Federation:AllowPrivateAddresses", "true"
                  "ConnectionStrings:Database", connectionString ]

            let app = Megafono.Program.buildAppWith configure [| "--environment"; "Development" |] overrides
            do! app.StartAsync()
            return new InteropMegafono(app, lease, fakeApp)
        with e ->
            (lease :> IDisposable).Dispose()
            return raise e
    }
