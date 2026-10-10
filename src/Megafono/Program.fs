module Megafono.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Oxpecker
open Megafono.Infrastructure

// Placeholder; the real health endpoint comes in T017
let private health: EndpointHandler = fun ctx -> ctx.WriteJson {| status = "ok" |}

/// Builds the app without running it, so tests can reuse it. Overrides win over every other configuration source.
let buildApp (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    builder.Configuration.AddInMemoryCollection(overrides |> List.map (fun (k, v) -> System.Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    // Startup stops here, with every problem listed, rather than failing later on a bad key or domain
    let options =
        match Config.load builder.Environment.EnvironmentName builder.Configuration with
        | Ok options -> options
        | Error errors -> raise (System.InvalidOperationException(Config.describeErrors errors))

    builder.Services.AddSingleton options |> ignore
    builder.Services.AddRouting().AddOxpecker() |> ignore

    match builder.Configuration.GetConnectionString "Database" with
    | null
    | "" -> ()
    | connectionString -> Database.register builder.Services connectionString |> ignore

    let app = builder.Build()

    if options.Federation.AllowPrivateAddresses then
        app.Logger.LogWarning
            "Megafono:Federation:AllowPrivateAddresses is ON: outbound requests may reach private addresses and plain http. This is for tests only."

    app.UseRouting().UseOxpecker [ GET [ route "/health" health ] ] |> ignore
    app

[<EntryPoint>]
let main argv =
    let app = buildApp argv []
    app.Run()
    0
