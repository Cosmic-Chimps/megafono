module Megafono.Program

open System
open System.Collections.Concurrent
open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.DependencyInjection.Extensions
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open Megafono.Infrastructure
open Megafono.Workers

/// The two queue loops, built once the container exists; tests reach `Wake` through here
type Workers =
    { Deliveries: Dispatcher.Dispatcher
      AppEvents: Dispatcher.Dispatcher }

let private logger (sp: IServiceProvider) = sp.GetRequiredService<ILoggerFactory>().CreateLogger "Megafono"

/// Everything the app is made of. `configure` runs first, so tests can pre-register a `TimeProvider` or a
/// `DeliveryFaults`; the registrations here only fill in what is missing.
let private register (options: Config.MegafonoOptions) (services: IServiceCollection) =
    let masterKeys =
        KeyVault.masterKeys (Config.decodeMasterKey options.MasterKey).Value (options.MasterKeyPrevious |> Option.bind Config.decodeMasterKey)

    services.TryAddSingleton<TimeProvider>(TimeProvider.System)
    // US3: the fetch rate limit defaults to "allow"; the inbound rate limiter replaces it
    services.TryAddSingleton<Federation.Objects.FetchRateLimit>(Federation.Objects.allowAll)
    services.AddSingleton options |> ignore
    services.AddSingleton masterKeys |> ignore
    Database.register services options.Database |> ignore

    services.AddSingleton<SafeHttp.SafeHttpClient>(fun sp ->
        let allow = options.Federation.AllowPrivateAddresses
        let agent = SafeHttp.userAgent options.Software.Version options.Domain
        // Registered by tests only (see SafeHttp.TestNetwork); createHandlerWith refuses it unless the private-address
        // switch is on, and that switch is refused in Production
        let testNetwork =
            match box (sp.GetService typeof<SafeHttp.TestNetwork>) with
            | null -> None
            | network -> Some(network :?> SafeHttp.TestNetwork)

        SafeHttp.create (SafeHttp.createHandlerWith allow testNetwork) allow agent (sp.GetRequiredService<TimeProvider>()))
    |> ignore

    services.AddSingleton<InstanceKey.InstanceKeyProvider>(fun sp ->
        InstanceKey.create (sp.GetRequiredService<NpgsqlDataSource>()) masterKeys options.Domain (sp.GetRequiredService<TimeProvider>()))
    |> ignore

    services.AddSingleton<IHostedService>(fun sp -> InstanceKey.hostedService (sp.GetRequiredService<InstanceKey.InstanceKeyProvider>())) |> ignore

    services.AddSingleton<AppClient.AppClient>(fun sp ->
        AppClient.create
            { DataSource = sp.GetRequiredService<NpgsqlDataSource>()
              Keys = masterKeys
              Http = AppClient.createHttpClient ()
              BaseUrl = options.App.BaseUrl
              Token = options.App.Token
              Domain = options.Domain
              Time = sp.GetRequiredService<TimeProvider>()
              AllowPrivateAddresses = options.Federation.AllowPrivateAddresses
              Log = logger sp })
    |> ignore

    services.AddSingleton<RemoteActors.Env>(fun sp ->
        let env: RemoteActors.Env =
            { DataSource = sp.GetRequiredService<NpgsqlDataSource>()
              Http = sp.GetRequiredService<SafeHttp.SafeHttpClient>()
              Instance = sp.GetRequiredService<InstanceKey.InstanceKeyProvider>()
              Time = sp.GetRequiredService<TimeProvider>()
              AllowPrivateAddresses = options.Federation.AllowPrivateAddresses
              Log = logger sp }

        env)
    |> ignore

    services.AddSingleton<RateLimits.RateLimits>(RateLimits.create options) |> ignore

    // Signed fetches of objects: the per-address layer already ran in the middleware; this adds the
    // per-verified-host layer once the signer is known (FR-016). Registered after the allow-all default, so it wins.
    services.AddSingleton<Federation.Objects.FetchRateLimit>(fun sp ->
        let limits = sp.GetRequiredService<RateLimits.RateLimits>()

        { Federation.Objects.FetchRateLimit.Allow =
            fun _ host ->
                match host with
                | None -> System.Threading.Tasks.Task.FromResult true
                | Some h -> System.Threading.Tasks.Task.FromResult((RateLimits.tryHost limits h).IsNone) })
    |> ignore

    services.AddSingleton<Workers>(fun sp ->
        let dataSource = sp.GetRequiredService<NpgsqlDataSource>()
        let time = sp.GetRequiredService<TimeProvider>()
        let log = logger sp

        let faults =
            match box (sp.GetService typeof<Deliveries.DeliveryFaults>) with
            | null -> None
            | faults -> (faults :?> Deliveries.DeliveryFaults).BeforeCompletionWrite

        let deliveries: Deliveries.Env =
            { DataSource = dataSource
              Http = sp.GetRequiredService<SafeHttp.SafeHttpClient>()
              Keys = masterKeys
              Domain = options.Domain
              Time = time
              KeyCache = ConcurrentDictionary() }

        let events: AppEvents.Env =
            { DataSource = dataSource
              Http = AppEvents.createHttpClient ()
              BaseUrl = options.App.BaseUrl
              WebhookSecret = options.App.WebhookSecret
              Time = time }

        { Deliveries =
            Dispatcher.create
                (Deliveries.spec deliveries options.Delivery.Global options.Delivery.PerHost)
                Dispatcher.defaultSettings
                dataSource
                time
                log
                faults
          AppEvents = Dispatcher.create (AppEvents.spec events) Dispatcher.defaultSettings dataSource time log None })
    |> ignore

    services.AddSingleton<IHostedService>(fun sp -> sp.GetRequiredService<Workers>().Deliveries.Service) |> ignore
    services.AddSingleton<IHostedService>(fun sp -> sp.GetRequiredService<Workers>().AppEvents.Service) |> ignore

    services.AddSingleton<IHostedService>(fun sp ->
        Housekeeping.hostedService (sp.GetRequiredService<NpgsqlDataSource>()) (sp.GetRequiredService<TimeProvider>()) (logger sp))
    |> ignore

/// Builds the app without running it, so tests can reuse it. Overrides win over every other configuration source;
/// `configure` may register services before the app's own (a fake clock, the fault hook).
let buildAppWith (configure: IServiceCollection -> unit) (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    // A key given twice: the last one wins
    let distinctOverrides = overrides |> List.rev |> List.distinctBy fst |> List.rev

    builder.Configuration.AddInMemoryCollection(distinctOverrides |> List.map (fun (k, v) -> System.Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    // Startup stops here, with every problem listed, rather than failing later on a bad key or domain
    let options =
        match Config.load builder.Environment.EnvironmentName builder.Configuration with
        | Ok options -> options
        | Error errors -> raise (InvalidOperationException(Config.describeErrors errors))

    // Federation port first, internal second: PortGuard.boundPorts relies on this order
    let address = IPAddress.Parse options.Ports.BindAddress

    builder.WebHost.ConfigureKestrel(fun (kestrel: KestrelServerOptions) ->
        kestrel.AddServerHeader <- false
        kestrel.Listen(address, options.Ports.Federation)
        kestrel.Listen(address, options.Ports.Internal))
    |> ignore

    configure builder.Services
    builder.Services.AddRouting().AddOxpecker() |> ignore
    register options builder.Services

    let app = builder.Build()

    if options.Federation.AllowPrivateAddresses then
        app.Logger.LogWarning
            "Megafono:Federation:AllowPrivateAddresses is ON: outbound requests may reach private addresses and plain http. This is for tests only."

    let ports = lazy (PortGuard.boundPorts (app.Services.GetRequiredService<IServer>()) options.Ports)
    app.Use(PortGuard.middleware options.App.Token ports) |> ignore
    app.Use(RateLimits.middleware (app.Services.GetRequiredService<RateLimits.RateLimits>())) |> ignore

    let endpoints =
        [ GET [ route "/health" (Health.handler (app.Services.GetRequiredService<NpgsqlDataSource>())) ] ]
        @ Federation.WebFinger.endpoints
        @ Federation.NodeInfo.endpoints
        @ Federation.Actors.endpoints
        @ Federation.Inbox.endpoints
        @ Internal.Actors.endpoints
        // US3: objects, activities and publishing
        @ Federation.Objects.endpoints
        @ Internal.Objects.endpoints
        @ Internal.Follows.endpoints

    app.UseRouting().UseOxpecker endpoints |> ignore

    app

let buildApp (args: string[]) (overrides: (string * string) list) : WebApplication = buildAppWith ignore args overrides

[<EntryPoint>]
let main argv =
    let app = buildApp argv []
    app.Run()
    0
