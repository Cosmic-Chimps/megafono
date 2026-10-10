/// Megáfono on real Kestrel inside the test process: both ports on 127.0.0.1 with port 0, a fake clock, and
/// the private-address switch on so it can talk to the loopback stubs.
module Megafono.Tests.Fixtures.MegafonoHost

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Megafono.Infrastructure
open Megafono.Workers
open Megafono.Tests.TestSupport

type HostSettings =
    { ConnectionString: string
      AppBaseUrl: string
      Time: FakeTime
      Faults: Deliveries.DeliveryFaults option
      /// More configuration, applied last
      Extra: (string * string) list
      Environment: string }

let token = "test-token"
let webhookSecret = "test-secret"
let domain = "megafono.test"

let settings (connectionString: string) (appBaseUrl: string) (time: FakeTime) : HostSettings =
    { ConnectionString = connectionString
      AppBaseUrl = appBaseUrl
      Time = time
      Faults = None
      Extra = []
      Environment = "Development" }

type MegafonoHost(app: WebApplication, federationUrl: string, internalUrl: string, time: FakeTime, connectionString: string) =
    let clients = ResizeArray<HttpClient>()

    member _.App = app
    member _.FederationUrl = federationUrl
    member _.InternalUrl = internalUrl
    member _.Time = time
    member _.ConnectionString = connectionString
    member _.Services = app.Services
    member _.Workers = app.Services.GetRequiredService<Megafono.Program.Workers>()
    member _.DataSource = app.Services.GetRequiredService<NpgsqlDataSource>()

    /// Asks both queues to look for due rows now (after moving the clock)
    member this.Wake() =
        this.Workers.Deliveries.Wake()
        this.Workers.AppEvents.Wake()

    /// A client for the federation port
    member _.FederationClient() : HttpClient =
        let client = new HttpClient(BaseAddress = Uri federationUrl)
        clients.Add client
        client

    /// A client for the internal port; `bearer` None sends no Authorization header
    member _.InternalClient(bearer: string option) : HttpClient =
        let client = new HttpClient(BaseAddress = Uri internalUrl)
        bearer |> Option.iter (fun t -> client.DefaultRequestHeaders.Authorization <- AuthenticationHeaderValue("Bearer", t))
        clients.Add client
        client

    /// Graceful stop: the queues drain what is in flight
    member _.Stop() : Task = app.StopAsync()

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            clients |> Seq.iter (fun c -> c.Dispose())
            app.DisposeAsync()

let private overrides (s: HostSettings) =
    [ "Megafono:Domain", domain
      "Megafono:MasterKey", masterKey
      "Megafono:App:BaseUrl", s.AppBaseUrl
      "Megafono:App:Token", token
      "Megafono:App:WebhookSecret", webhookSecret
      "Megafono:Ports:Federation", "0"
      "Megafono:Ports:Internal", "0"
      "Megafono:Ports:BindAddress", "127.0.0.1"
      "Megafono:Federation:AllowPrivateAddresses", "true"
      "ConnectionStrings:Database", s.ConnectionString ]
    @ s.Extra

/// Builds the app (throws on invalid configuration) without starting it
let build (s: HostSettings) : WebApplication =
    let configure (services: IServiceCollection) =
        services.AddLogging(fun l -> l.ClearProviders() |> ignore) |> ignore
        services.AddSingleton<TimeProvider>(s.Time) |> ignore
        s.Faults |> Option.iter (fun f -> services.AddSingleton<Deliveries.DeliveryFaults>(f) |> ignore)

    Megafono.Program.buildAppWith configure [| "--environment"; s.Environment |] (overrides s)

let start (s: HostSettings) : Task<MegafonoHost> =
    task {
        let app = build s
        do! app.StartAsync()
        let ports = PortGuard.boundPorts (app.Services.GetRequiredService<IServer>()) { Federation = 0; Internal = 0; BindAddress = "127.0.0.1" }
        return new MegafonoHost(app, $"http://127.0.0.1:{ports.Federation}", $"http://127.0.0.1:{ports.Internal}", s.Time, s.ConnectionString)
    }

/// For `use _ = MegafonoHost.disposer host` inside a `task`: stops and disposes the host at the end of the scope
let disposer (host: MegafonoHost) : IDisposable =
    { new IDisposable with
        member _.Dispose() = (host :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult() }
