/// Everything the sample app needs to talk to Megáfono: the two lookup handlers, the webhook receiver and the
/// internal API client. The line budget for this file is checked by scripts/count-integration-lines.sh (SC-007).
module SampleApp.Megafono

open System
open System.IO
open System.Net.Http
open System.Net.Http.Headers
open System.Security.Cryptography
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Configuration
open Oxpecker
open SampleApp.Users

type Settings = { Url: string; Token: string; WebhookSecret: string; Domain: string }

let settings (c: IConfiguration) =
    { Url = c["Megafono:Url"]; Token = c["Megafono:Token"]; WebhookSecret = c["Megafono:WebhookSecret"]; Domain = c["App:Domain"] }

let private equal (a: string) (b: string) =
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes a, Encoding.UTF8.GetBytes b)

/// The actor URL that the profile page advertises so a pasted profile link works on Mastodon (FR-005)
let actorUrl (s: Settings) (u: User) = $"https://{s.Domain}/ap/actors/{u.Id}"

// ---- what the app implements -------------------------------------------------------------------------------

let private authorized (s: Settings) (ctx: HttpContext) =
    s.Token <> "" && equal (string ctx.Request.Headers.Authorization) $"Bearer {s.Token}"

/// Actor lookup, by handle or by id (FR-060): 200 with the profile, 404 when unknown, 401 without the bearer
let lookup (s: Settings) (find: HttpContext -> User option) : EndpointHandler =
    fun ctx ->
        match authorized s ctx, find ctx with
        | true, Some u ->
            ctx.Response.WriteAsJsonAsync
                {| id = u.Id; handle = u.Handle; name = u.Name; summary = u.Summary; ``type`` = "Person"; locked = u.Locked
                   profileUrl = $"https://{s.Domain}/u/{u.Handle}" |}
        | ok, _ ->
            ctx.Response.StatusCode <- if ok then 404 else 401
            Threading.Tasks.Task.CompletedTask

/// `Megafono-Signature: t=<unix>,v1=<hex hmac-sha256 of "t.body">`, at most 5 minutes old, constant-time compare
let verifySignature (secret: string) (now: DateTimeOffset) (header: string) (body: byte[]) =
    let parts = header.Split ',' |> Array.map (fun p -> p.Trim().Split('=', 2)) |> Array.filter (fun p -> p.Length = 2)
    let part name = parts |> Array.tryFind (fun p -> p[0] = name) |> Option.map (fun p -> p[1])

    match part "t", part "v1" with
    | Some t, Some v1 when secret <> "" && (match Int64.TryParse t with | true, ts -> abs (now.ToUnixTimeSeconds() - ts) <= 300L | _ -> false) ->
        let mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, Array.append (Encoding.UTF8.GetBytes $"{t}.") body)
        equal (Convert.ToHexStringLower mac) (v1.ToLowerInvariant())
    | _ -> false

/// Event receiver: any 2xx means handled. `record` gets the event id and the raw body, and must ignore repeats.
let webhook (s: Settings) (record: string -> string -> unit) : EndpointHandler =
    fun ctx ->
        task {
            use buffer = new MemoryStream()
            do! ctx.Request.Body.CopyToAsync buffer
            let body = buffer.ToArray()

            if verifySignature s.WebhookSecret DateTimeOffset.UtcNow (string ctx.Request.Headers["Megafono-Signature"]) body then
                use json = JsonDocument.Parse body
                record (json.RootElement.GetProperty("id").GetString()) (Encoding.UTF8.GetString body)
                ctx.Response.StatusCode <- 204
            else
                ctx.Response.StatusCode <- 401
        }

// ---- what the app calls ------------------------------------------------------------------------------------

let private http = new HttpClient()

/// One call to the internal API; returns the status code and the response body
let private call (s: Settings) (m: HttpMethod) (path: string) (body: obj) (idempotencyKey: string) =
    task {
        use req = new HttpRequestMessage(m, s.Url.TrimEnd '/' + path)
        req.Headers.Authorization <- AuthenticationHeaderValue("Bearer", s.Token)
        if idempotencyKey <> null then req.Headers.Add("Idempotency-Key", idempotencyKey)
        if body <> null then req.Content <- new StringContent(JsonSerializer.Serialize body, Encoding.UTF8, "application/json")
        use! res = http.SendAsync req
        let! text = res.Content.ReadAsStringAsync()
        return int res.StatusCode, text
    }

let answerFollow (s: Settings) (u: User) (followId: string) (approve: bool) =
    let verb = if approve then "approve" else "reject"
    call s HttpMethod.Post $"/internal/actors/{u.Id}/follow-requests/{followId}/{verb}" null null

/// `status` is `accepted` or `pending`; the body is `{ items: [ { followId, status, actor: { handle, name, ... } } ] }`
let followers (s: Settings) (u: User) (status: string) =
    call s HttpMethod.Get $"/internal/actors/{u.Id}/followers?status={status}" null null

/// `audience` is `public`, `unlisted` or `followers`; the same key always gives the same object
let publish (s: Settings) (u: User) (audience: string) (text: string) (key: string) =
    let note = {| ``type`` = "Note"; content = $"<p>{Net.WebUtility.HtmlEncode text}</p>"; url = $"https://{s.Domain}/u/{u.Handle}" |}
    call s HttpMethod.Post $"/internal/actors/{u.Id}/objects" {| audience = audience; ``object`` = note |} key
