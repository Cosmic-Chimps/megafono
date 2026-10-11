/// A fake fediverse server on http://127.0.0.1:<port> with its own RSA key: serves actor documents and
/// WebFinger, records what its inboxes receive, and can sign requests to Megáfono.
module Megafono.Tests.Fixtures.StubRemote

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Megafono.Infrastructure

type InboxMode =
    | Accept
    | Fail500
    /// Answers 202 after this long (also used to hold requests open)
    | Slow of TimeSpan

/// Where a key id points, which differs between servers (research R-01)
type KeyStyle =
    /// `…/users/x#main-key`: the actor document itself (Mastodon)
    | Fragment
    /// `…/users/x/main-key`: a partial actor stub that names its owner (GoToSocial)
    | PathStub
    /// `…/users/x/main-key`: a bare key object with an `owner`
    | PathKeyObject

type ReceivedPost =
    { Path: string
      Headers: (string * string) list
      Body: byte[]
      At: DateTimeOffset }

    member p.Header(name: string) =
        p.Headers |> List.tryFind (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase)) |> Option.map snd

type ReceivedGet =
    { Path: string
      Headers: (string * string) list }

type StubRemote() =
    let rsa = RSA.Create 2048
    let posts = ConcurrentQueue<ReceivedPost>()
    let gets = ConcurrentQueue<ReceivedGet>()
    let mutable inFlight = 0
    let mutable maxConcurrent = 0
    let mutable inboxMode = Accept
    let mutable keyStyle = Fragment
    let mutable publicKey = rsa.ExportSubjectPublicKeyInfoPem()
    let mutable sharedInbox: string option = None
    let mutable mutate: (string -> JsonObject -> unit) option = None
    let users = ConcurrentDictionary<string, unit>()
    let notes = ConcurrentDictionary<string, JsonObject>()
    let mutable app: WebApplication = null

    let baseUrl () =
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

    let actorId (name: string) = $"{baseUrl ()}/users/{name}"
    let keyIdOf (name: string) = match keyStyle with Fragment -> $"{actorId name}#main-key" | _ -> $"{actorId name}/main-key"

    let keyNode (name: string) =
        let k = JsonObject()
        k["id"] <- JsonValue.Create(keyIdOf name)
        k["owner"] <- JsonValue.Create(actorId name)
        k["publicKeyPem"] <- JsonValue.Create publicKey
        k

    let actorDocument (name: string) : JsonObject =
        let doc = JsonObject()
        doc["@context"] <- JsonArray(JsonValue.Create "https://www.w3.org/ns/activitystreams", JsonValue.Create "https://w3id.org/security/v1")
        doc["id"] <- JsonValue.Create(actorId name)
        doc["type"] <- JsonValue.Create "Person"
        doc["preferredUsername"] <- JsonValue.Create name
        doc["name"] <- JsonValue.Create $"{name} (stub)"
        doc["inbox"] <- JsonValue.Create $"{actorId name}/inbox"
        doc["outbox"] <- JsonValue.Create $"{actorId name}/outbox"
        doc["followers"] <- JsonValue.Create $"{actorId name}/followers"
        doc["url"] <- JsonValue.Create $"{baseUrl ()}/@{name}"
        doc["publicKey"] <- keyNode name

        match sharedInbox with
        | Some url ->
            let endpoints = JsonObject()
            endpoints["sharedInbox"] <- JsonValue.Create url
            doc["endpoints"] <- endpoints
        | None -> ()

        mutate |> Option.iter (fun f -> f name doc)
        doc

    let headersOf (ctx: HttpContext) = [ for h in ctx.Request.Headers -> h.Key, h.Value.ToString() ]

    let write (ctx: HttpContext) (status: int) (contentType: string) (body: string) =
        ctx.Response.StatusCode <- status
        ctx.Response.ContentType <- contentType
        ctx.Response.WriteAsync body

    let handleInbox (ctx: HttpContext) : Task =
        task {
            use buffer = new IO.MemoryStream()
            do! ctx.Request.Body.CopyToAsync buffer

            let now = Interlocked.Increment(&inFlight)
            let mutable seen = maxConcurrent

            while now > seen && Interlocked.CompareExchange(&maxConcurrent, now, seen) <> seen do
                seen <- maxConcurrent

            try
                posts.Enqueue
                    { Path = ctx.Request.Path.Value
                      Headers = headersOf ctx
                      Body = buffer.ToArray()
                      At = DateTimeOffset.UtcNow }

                match inboxMode with
                | Slow delay ->
                    do! Task.Delay delay
                    ctx.Response.StatusCode <- 202
                | Fail500 -> ctx.Response.StatusCode <- 500
                | Accept -> ctx.Response.StatusCode <- 202
            finally
                Interlocked.Decrement(&inFlight) |> ignore
        }

    let route (ctx: HttpContext) : Task =
        let path = ctx.Request.Path.Value

        if ctx.Request.Method = "POST" && (path.EndsWith "/inbox") then
            handleInbox ctx
        else
            gets.Enqueue { Path = ctx.Request.GetEncodedPathAndQuery(); Headers = headersOf ctx }

            if path = "/.well-known/webfinger" then
                let resource = ctx.Request.Query["resource"].ToString()
                let name = (resource.Replace("acct:", "").Split('@')[0]).ToLowerInvariant() // WebFinger user names are case-insensitive

                if users.ContainsKey name then
                    let jrd = JsonObject()
                    jrd["subject"] <- JsonValue.Create resource
                    let self = JsonObject()
                    self["rel"] <- JsonValue.Create "self"
                    self["type"] <- JsonValue.Create "application/activity+json"
                    self["href"] <- JsonValue.Create(actorId name)
                    jrd["links"] <- JsonArray(self)
                    write ctx 200 "application/jrd+json" (jrd.ToJsonString())
                else
                    write ctx 404 "application/json" "{}"
            elif path.StartsWith "/users/" then
                let parts = path.Substring("/users/".Length).Split('/')
                let name = parts[0]

                if not (users.ContainsKey name) then
                    write ctx 404 "application/json" "{}"
                elif parts.Length = 1 then
                    write ctx 200 "application/activity+json" ((actorDocument name).ToJsonString())
                elif parts.Length = 2 && parts[1] = "main-key" then
                    match keyStyle with
                    | PathStub ->
                        let stub = JsonObject()
                        stub["id"] <- JsonValue.Create(actorId name)
                        stub["type"] <- JsonValue.Create "Person"
                        stub["publicKey"] <- keyNode name
                        write ctx 200 "application/activity+json" (stub.ToJsonString())
                    | PathKeyObject ->
                        let key = keyNode name
                        key["type"] <- JsonValue.Create "Key"
                        write ctx 200 "application/activity+json" (key.ToJsonString())
                    | Fragment -> write ctx 404 "application/json" "{}"
                elif parts.Length = 3 && parts[1] = "statuses" && notes.ContainsKey path then
                    write ctx 200 "application/activity+json" (notes[path].ToJsonString())
                else
                    write ctx 404 "application/json" "{}"
            else
                write ctx 404 "application/json" "{}"

    member _.Posts = posts |> Seq.toList
    member _.Gets = gets |> Seq.toList
    member _.MaxConcurrent = maxConcurrent
    member _.InFlight = inFlight
    member _.BaseUrl = baseUrl ()
    /// host:port, what Megáfono calls the host of this server
    member _.Authority = Uri(baseUrl ()).Authority
    member _.PublicKeyPem = publicKey

    member _.InboxMode
        with get () = inboxMode
        and set v = inboxMode <- v

    member _.KeyStyle
        with get () = keyStyle
        and set v = keyStyle <- v

    member _.SharedInbox
        with get () = sharedInbox
        and set v = sharedInbox <- v

    /// Lets a test break the actor document (name, document)
    member _.Mutate
        with get () = mutate
        and set v = mutate <- v

    member _.AddUser(name: string) = users[name] <- ()

    /// Serves a Note of `name` at `/users/{name}/statuses/{suffix}` (what a server does for a reply it sent)
    member _.ServeNote(name: string, suffix: string, note: JsonObject) = notes[$"/users/{name}/statuses/{suffix}"] <- note
    member _.RemoveNote(name: string, suffix: string) = notes.TryRemove $"/users/{name}/statuses/{suffix}" |> ignore
    member _.ActorId(name: string) = actorId name
    member _.KeyId(name: string) = keyIdOf name
    member _.InboxUrl(name: string) = $"{actorId name}/inbox"

    /// Replaces the key the stub signs with and publishes (a key rotation)
    member _.RotateKey() =
        rsa.ImportParameters((RSA.Create 2048).ExportParameters true)
        publicKey <- rsa.ExportSubjectPublicKeyInfoPem()

    /// Headers (Date, Digest, Signature) for a request this server makes to `url`, signed as `name`
    member _.Sign(name: string, httpMethod: string, url: string, body: byte[] option) : (string * string) list =
        HttpSignatures.sign (keyIdOf name) rsa DateTimeOffset.UtcNow httpMethod (Uri url) body

    /// Same, with the clock the signature carries (for stale-date tests)
    member _.SignAt(name: string, httpMethod: string, url: string, body: byte[] option, at: DateTimeOffset) : (string * string) list =
        HttpSignatures.sign (keyIdOf name) rsa at httpMethod (Uri url) body

    /// The key this server signs with: tests use it to build signatures by hand
    member _.PrivateKey = rsa

    member _.Start() : Task =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.ClearProviders() |> ignore
            app <- builder.Build()
            app.Urls.Add "http://127.0.0.1:0"
            app.Run(RequestDelegate route)
            do! app.StartAsync()
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            if isNull app then ValueTask() else ValueTask(app.DisposeAsync().AsTask())

let start (names: string list) : Task<StubRemote> =
    task {
        let stub = new StubRemote()
        names |> List.iter stub.AddUser
        do! stub.Start()
        return stub
    }
