module Megafono.Program

open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Oxpecker

// Placeholder; the real health endpoint comes in T017
let private health: EndpointHandler = fun ctx -> ctx.WriteJson {| status = "ok" |}

/// Builds the app without running it, so tests can reuse it. Overrides win over every other configuration source.
let buildApp (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    builder.Configuration.AddInMemoryCollection(overrides |> List.map (fun (k, v) -> System.Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    builder.Services.AddRouting().AddOxpecker() |> ignore

    let app = builder.Build()
    app.UseRouting().UseOxpecker [ GET [ route "/health" health ] ] |> ignore
    app

[<EntryPoint>]
let main argv =
    let app = buildApp argv []
    app.Run()
    0
