/// Keeps the internal API off the federation port and everything else off the internal port (research R-10).
/// The decision uses the local port of the socket, which a caller cannot spoof the way it can a Host header.
module Megafono.Infrastructure.PortGuard

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http

type Ports = { Federation: int; Internal: int }

/// The ports Kestrel actually bound: the configured ones, or, for port 0, the ones the server reports.
/// Kestrel reports addresses in the order the listeners were added: federation first, then internal.
let boundPorts (server: IServer) (configured: Config.PortOptions) : Ports =
    let reported =
        server.Features.Get<IServerAddressesFeature>().Addresses
        |> Seq.map (fun address -> Uri(address.Replace("[::]", "localhost").Replace("0.0.0.0", "localhost")).Port)
        |> Array.ofSeq

    let pick (configuredPort: int) (index: int) =
        if configuredPort <> 0 then configuredPort
        elif index < reported.Length then reported[index]
        else -1

    { Federation = pick configured.Federation 0
      Internal = pick configured.Internal 1 }

let private sha256 (text: string) = SHA256.HashData(Encoding.UTF8.GetBytes text)

/// `Authorization: Bearer <token>`, compared in constant time (the hashes have equal length)
let hasValidBearer (token: string) (ctx: HttpContext) : bool =
    let header = ctx.Request.Headers.Authorization.ToString()
    let prefix = "Bearer "

    header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
    && CryptographicOperations.FixedTimeEquals(sha256 ((header.Substring prefix.Length).Trim()), sha256 token)

let private reject (ctx: HttpContext) (status: int) (error: string) (message: string) : Task =
    ctx.Response.StatusCode <- status
    ctx.Response.ContentType <- "application/json"
    ctx.Response.WriteAsync $"{{\"error\":\"{error}\",\"message\":\"{message}\"}}"

let private notFound (ctx: HttpContext) : Task =
    ctx.Response.StatusCode <- 404
    Task.CompletedTask

/// First middleware. On the internal port only `/internal/*` (bearer required) and `/health` exist; on the
/// federation port `/internal/*` does not. Any other port fails closed with 404.
let middleware (token: string) (ports: Lazy<Ports>) : Func<HttpContext, RequestDelegate, Task> =
    Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
        let path = ctx.Request.Path
        let isInternal = path.StartsWithSegments("/internal", StringComparison.OrdinalIgnoreCase)
        let isHealth = path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        let port = ctx.Connection.LocalPort
        let ports = ports.Value

        if port = ports.Internal then
            if isInternal then
                if hasValidBearer token ctx then next.Invoke ctx else reject ctx 401 "unauthorized" "Missing or wrong bearer token"
            elif isHealth then
                next.Invoke ctx
            else
                notFound ctx
        elif port = ports.Federation then
            if isInternal then notFound ctx else next.Invoke ctx
        else
            notFound ctx)
