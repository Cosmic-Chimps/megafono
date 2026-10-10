module SampleApp.Program

open System
open System.Collections.Concurrent
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Oxpecker
open SampleApp
open SampleApp.Users

let private html (page: string) : EndpointHandler =
    fun ctx ->
        ctx.Response.ContentType <- "text/html; charset=utf-8"
        ctx.Response.WriteAsync page

let private text (status: int) (message: string) : EndpointHandler =
    fun ctx ->
        ctx.Response.StatusCode <- status
        ctx.Response.WriteAsync message

/// Builds the sample without running it, so tests can host it. Overrides win over every other configuration source.
let buildSample (args: string[]) (overrides: (string * string) list) : WebApplication =
    let builder = WebApplication.CreateBuilder args

    builder.Configuration.AddInMemoryCollection(overrides |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v)))
    |> ignore

    builder.Services.AddRouting().AddOxpecker() |> ignore
    let app = builder.Build()
    let s = Megafono.settings app.Configuration
    // Received webhook events by id, in memory; a repeated id is ignored
    let events = ConcurrentDictionary<string, string>()
    let withUser (handle: string) (f: User -> EndpointHandler) : EndpointHandler =
        match byHandle handle with
        | Some u -> f u
        | None -> text 404 "unknown user"

    let manage (u: User) : EndpointHandler =
        fun ctx ->
            task {
                let! _, followers = Megafono.followers s u "accepted"
                let! _, pending = Megafono.followers s u "pending"
                let latest = events.ToArray() |> Array.sortByDescending (fun e -> e.Key) |> Array.truncate 20 |> Array.map (fun e -> e.Value) |> List.ofArray
                return! html (Pages.manage u followers pending latest) ctx
            }

    // Run an internal API call, then go back to the management page
    let thenManage (u: User) (call: Threading.Tasks.Task<int * string>) : EndpointHandler =
        fun ctx ->
            task {
                let! status, body = call
                if status >= 400 then return! text 502 body ctx else ctx.Response.Redirect $"/u/{u.Handle}/manage"
            }

    let profile (u: User) : EndpointHandler =
        fun ctx ->
            let actor = Megafono.actorUrl s u
            ctx.Response.Headers.Link <- $"<{actor}>; rel=\"alternate\"; type=\"application/activity+json\""
            html (Pages.profile u actor) ctx

    let publish (u: User) : EndpointHandler =
        fun ctx ->
            task {
                let form = ctx.Request.Form
                return! thenManage u (Megafono.publish s u (string form["audience"]) (string form["text"]) (string (Guid.NewGuid()))) ctx
            }

    app.UseRouting().UseOxpecker
        [ GET
              [ route "/" (fun ctx -> html (Pages.index ()) ctx)
                route "/megafono/actors" (Megafono.lookup s (fun ctx -> byHandle (string ctx.Request.Query["handle"])))
                routef "/megafono/actors/{%s}" (fun id -> Megafono.lookup s (fun _ -> byId id))
                routef "/u/{%s}" (fun h -> withUser h profile)
                routef "/u/{%s}/manage" (fun h -> withUser h manage) ]
          POST
              [ route "/megafono/webhooks" (Megafono.webhook s (fun id body -> events.TryAdd(id, body) |> ignore))
                routef "/u/{%s}/publish" (fun h -> withUser h publish)
                routef "/u/{%s}/follow-requests/{%s}/approve" (fun h f -> withUser h (fun u -> thenManage u (Megafono.answerFollow s u f true)))
                routef "/u/{%s}/follow-requests/{%s}/reject" (fun h f -> withUser h (fun u -> thenManage u (Megafono.answerFollow s u f false))) ] ]
    |> ignore

    app

[<EntryPoint>]
let main argv =
    (buildSample argv []).Run()
    0
