/// The reference gateway (FR-063, research R-14): the only public service. It splits traffic between Megáfono and
/// the app by path and touches nothing else: the original `Host`, path, query and body reach Megáfono unchanged,
/// because HTTP signatures cover them. Routes and destinations live in the `ReverseProxy` section of appsettings.json.
module Megafono.Gateway.Program

open System.Net
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection

/// Builds the gateway without running it, so tests can host it. Overrides win over every other configuration source.
let buildGateway (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    builder.Configuration.AddInMemoryCollection(overrides |> List.map (fun (k, v) -> System.Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    // With a certificate configured the gateway terminates TLS on 443; otherwise the listen addresses come from
    // the usual `urls` setting (tests use plain http on port 0)
    match builder.Configuration["Kestrel:Certificates:Default:Path"] with
    | null
    | "" -> ()
    | _ ->
        builder.WebHost.ConfigureKestrel(fun (kestrel: KestrelServerOptions) ->
            kestrel.Listen(IPAddress.Any, 443, fun listen -> listen.UseHttps() |> ignore))
        |> ignore

    builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection "ReverseProxy") |> ignore

    let app = builder.Build()
    app.MapReverseProxy() |> ignore
    app

[<EntryPoint>]
let main argv =
    let app = buildGateway argv []
    app.Run()
    0
