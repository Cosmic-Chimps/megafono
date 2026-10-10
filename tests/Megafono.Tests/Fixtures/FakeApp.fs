/// A fake host app (contracts/app-contract.md): serves the actor lookup from an in-memory list and records the
/// webhooks it receives, verifying their HMAC. Switches make either side answer 500 or hang.
module Megafono.Tests.Fixtures.FakeApp

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Megafono.Domain

type FakeUser =
    { Id: string
      Handle: string
      Name: string
      Summary: string option
      IconUrl: string option
      ProfileUrl: string
      Type: string
      Locked: bool }

let user (id: string) (handle: string) : FakeUser =
    { Id = id
      Handle = handle
      Name = $"Name of {handle}"
      Summary = Some "<p>hello</p>"
      IconUrl = None
      ProfileUrl = $"https://app.example/u/{handle}"
      Type = "Person"
      Locked = false }

type Mode =
    | Normal
    | Fail500
    /// Never answers (until the caller gives up)
    | Hang

type ReceivedWebhook =
    { Json: JsonNode
      Raw: byte[]
      SignatureHeader: string
      /// The HMAC matches the secret for the timestamp and exact bytes
      SignatureValid: bool
      /// What we answered (200 or 500)
      Answered: int }

    member w.EventId = w.Json["id"].GetValue<string>()
    member w.Type = w.Json["type"].GetValue<string>()

type FakeApp(token: string, webhookSecret: string) =
    let users = ConcurrentDictionary<string, FakeUser>()
    let received = ConcurrentQueue<ReceivedWebhook>()
    let lookups = ConcurrentQueue<string>()
    let mutable lookupMode = Normal
    let mutable webhookMode = Normal
    let mutable unauthorizedLookups = 0
    let mutable app: WebApplication = null

    let json (status: int) (body: string) (ctx: HttpContext) =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- "application/json"
        ctx.Response.WriteAsync body

    let hang (ctx: HttpContext) : Task =
        task {
            try
                do! Task.Delay(Timeout.Infinite, ctx.RequestAborted)
            with :? OperationCanceledException ->
                ()
        }

    let profile (u: FakeUser) =
        let node = JsonObject()
        node["id"] <- JsonValue.Create u.Id
        node["handle"] <- JsonValue.Create u.Handle
        node["name"] <- JsonValue.Create u.Name
        node["summary"] <- (match u.Summary with Some s -> JsonValue.Create s :> JsonNode | None -> null)
        node["iconUrl"] <- (match u.IconUrl with Some s -> JsonValue.Create s :> JsonNode | None -> null)
        node["profileUrl"] <- JsonValue.Create u.ProfileUrl
        node["type"] <- JsonValue.Create u.Type
        node["locked"] <- JsonValue.Create u.Locked
        node.ToJsonString()

    let handleLookup (ctx: HttpContext) : Task =
        task {
            lookups.Enqueue(ctx.Request.Path.Value + ctx.Request.QueryString.Value)

            if ctx.Request.Headers.Authorization.ToString() <> $"Bearer {token}" then
                Interlocked.Increment(&unauthorizedLookups) |> ignore
                do! json 401 """{"error":"unauthorized"}""" ctx
            else
                match lookupMode with
                | Hang -> do! hang ctx
                | Fail500 -> do! json 500 """{"error":"boom"}""" ctx
                | Normal ->
                    let found =
                        match ctx.Request.Path.Value.Substring("/megafono/actors".Length).TrimStart('/') with
                        | "" ->
                            let handle = ctx.Request.Query["handle"].ToString()
                            users.Values |> Seq.tryFind (fun u -> u.Handle = handle)
                        | id ->
                            match users.TryGetValue id with
                            | true, u -> Some u
                            | _ -> None

                    match found with
                    | Some u -> do! json 200 (profile u) ctx
                    | None -> do! json 404 """{"error":"not_found"}""" ctx
        }

    let handleWebhook (ctx: HttpContext) : Task =
        task {
            use buffer = new IO.MemoryStream()
            do! ctx.Request.Body.CopyToAsync buffer
            let raw = buffer.ToArray()
            let header = ctx.Request.Headers["Megafono-Signature"].ToString()

            let valid =
                try
                    let parts = header.Split(',') |> Array.map (fun p -> p.Split('=', 2)) |> Array.map (fun p -> p[0], p[1]) |> dict
                    let t = int64 parts["t"]
                    Events.signature webhookSecret t raw = header
                with _ ->
                    false

            let status = if webhookMode = Fail500 then 500 else 200

            if webhookMode <> Hang then
                received.Enqueue
                    { Json = JsonNode.Parse raw
                      Raw = raw
                      SignatureHeader = header
                      SignatureValid = valid
                      Answered = status }

            match webhookMode with
            | Hang -> do! hang ctx
            | _ -> do! json status "{}" ctx
        }

    member _.Users = users
    member _.Received = received |> Seq.toList
    member _.LookupPaths = lookups |> Seq.toList
    member _.UnauthorizedLookups = unauthorizedLookups

    member _.LookupMode
        with get () = lookupMode
        and set v = lookupMode <- v

    member _.WebhookMode
        with get () = webhookMode
        and set v = webhookMode <- v

    member _.AddUser(u: FakeUser) = users[u.Id] <- u

    member _.Url: string =
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

    member this.Start() : Task =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.ClearProviders() |> ignore
            app <- builder.Build()
            app.Urls.Add "http://127.0.0.1:0"

            app.Run(
                RequestDelegate(fun ctx ->
                    if ctx.Request.Path.StartsWithSegments "/megafono/actors" then handleLookup ctx
                    elif ctx.Request.Path.StartsWithSegments "/megafono/webhooks" && ctx.Request.Method = "POST" then handleWebhook ctx
                    else json 404 "{}" ctx)
            )

            do! app.StartAsync()
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            if isNull app then ValueTask() else ValueTask(app.DisposeAsync().AsTask())

let start (token: string) (webhookSecret: string) : Task<FakeApp> =
    task {
        let fake = new FakeApp(token, webhookSecret)
        do! fake.Start()
        return fake
    }
