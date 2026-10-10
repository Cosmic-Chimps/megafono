/// HTTP Basic protection for the sample's management pages and actions. This is the sample's own UI concern, not
/// part of the Megáfono integration.
module SampleApp.Auth

open System
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

/// The public profile `/u/{handle}` stays open; everything else under `/u/` and every non-GET request is protected
let isProtected (request: HttpRequest) =
    let path = request.Path.Value
    let segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries)
    path.StartsWith "/u/" && (segments.Length > 2 || request.Method <> "GET")

let private sha (text: string) = SHA256.HashData(Encoding.UTF8.GetBytes text)

/// Any user name, the configured password. An empty password disables the check (allowed in Development only).
let passwordMatches (password: string) (header: string) =
    match header with
    | h when h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase) ->
        try
            let decoded = Encoding.UTF8.GetString(Convert.FromBase64String(h.Substring(6).Trim()))
            let given = decoded.Substring(decoded.IndexOf ':' + 1)
            decoded.Contains ':' && CryptographicOperations.FixedTimeEquals(sha given, sha password)
        with :? FormatException ->
            false
    | _ -> false

let middleware (password: string) : Func<HttpContext, Func<Task>, Task> =
    Func<_, _, _>(fun ctx next ->
        if password = "" || not (isProtected ctx.Request) || passwordMatches password (string ctx.Request.Headers.Authorization) then
            next.Invoke()
        else
            ctx.Response.StatusCode <- 401
            ctx.Response.Headers.WWWAuthenticate <- "Basic realm=\"Sample app admin\", charset=\"UTF-8\""
            Task.CompletedTask)
