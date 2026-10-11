/// `POST /inbox` and `POST /ap/actors/{id}/inbox`: the verification pipeline of contracts/federation.md, in its
/// order, each step refusing before the next one runs. Nothing a refused request carries reaches a handler or the app.
///
///  1. per-address rate limit (a middleware, before this handler)     -> 429
///  2. body read as raw bytes, over 1 MB                              -> 413
///  3. signature present, headers covered, Date within ±12 h         -> 401
///  4. Digest present and equal to the raw bytes                      -> 401
///  5. key fetched (signed by the instance actor), signature checked, one refetch on failure -> 401
///  6. per-verified-host rate limit                                   -> 429
///  7. JSON parsed                                                    -> 400
///  8. activity id seen in the last 7 days                            -> 202, ignored (in the handler's transaction)
///  9. embedded ids on another host than the signer are not trusted (see InboxCommon.isOnSignerHost)
/// 10. dispatch; unknown types                                        -> 202, dropped
module Megafono.Federation.Inbox

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.Logging
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let maxBodyBytes = 1024 * 1024

let private status (ctx: HttpContext) (code: int) : Task =
    ctx.Response.StatusCode <- code
    Task.CompletedTask

/// Step 2: raw bytes, never more than 1 MB, whether the size is declared or only discovered while reading
let private readBody (ctx: HttpContext) : Task<Result<byte[], int>> =
    task {
        match ctx.Request.ContentLength |> Option.ofNullable with
        | Some declared when declared > int64 maxBodyBytes -> return Error 413
        | _ ->
            let limit = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>()
            if not (isNull limit) && not limit.IsReadOnly then limit.MaxRequestBodySize <- Nullable(int64 maxBodyBytes)

            try
                use buffer = new IO.MemoryStream()
                let chunk = Array.zeroCreate<byte> 16384
                let mutable fin = false
                let mutable tooLarge = false

                while not fin && not tooLarge do
                    let! read = ctx.Request.Body.ReadAsync(chunk, 0, chunk.Length)

                    if read = 0 then fin <- true
                    elif buffer.Length + int64 read > int64 maxBodyBytes then tooLarge <- true
                    else buffer.Write(chunk, 0, read)

                return if tooLarge then Error 413 else Ok(buffer.ToArray())
            with :? BadHttpRequestException as e when e.StatusCode = 413 ->
                return Error 413
    }

/// What the signature is computed over: the method, the target exactly as received, the headers and the bytes
let private signedRequest (ctx: HttpContext) (body: byte[]) : SignedRequest =
    { Method = ctx.Request.Method
      PathAndQuery = ctx.Features.Get<IHttpRequestFeature>().RawTarget
      Headers = [ for h in ctx.Request.Headers -> h.Key, h.Value.ToString() ]
      Body = body }

/// Steps 3 and 4: everything that can be checked without a key
let private checkWithoutKey (time: TimeProvider) (request: SignedRequest) : Result<ParsedSignature, VerifyError> =
    result {
        let! value = header "signature" request |> Option.toResult MissingSignature
        let! parsed = parseSignatureHeader value
        do! checkRequiredHeaders request parsed
        do! checkDate time request
        do! verifyDigest request
        return parsed
    }

type private SignerResult =
    | Signer of RemoteActor
    | Unauthorized
    | RateLimited of retryAfter: int

/// Step 5: the key is the cached one while fresh; on a signature mismatch it is fetched again once, in case the actor
/// rotated its key. Every fetch is signed by the instance actor and costs the claimed host a key-fetch token.
let private resolveSigner (env: Env) (limits: RateLimits.RateLimits) (request: SignedRequest) (parsed: ParsedSignature) : Task<SignerResult> =
    task {
        let verifyWith (actor: RemoteActor) =
            try
                use key = KeyVault.importPublicPem actor.PublicKeyPem
                draftCavage.Verify env.Time key request
            with _ ->
                Error BadSignature

        let claimedHost =
            match Uri.TryCreate(parsed.KeyId, UriKind.Absolute) with
            | true, uri -> Some(authority uri)
            | _ -> None

        match claimedHost with
        | None -> return Unauthorized
        | Some host ->
            let fetchAndVerify () =
                task {
                    match RateLimits.tryKeyFetch limits host with
                    | Some retryAfter -> return RateLimited retryAfter
                    | None ->
                        match! refetchKey env.Remote parsed.KeyId with
                        | Error e ->
                            env.Log.LogInformation("Key {KeyId} could not be fetched: {Error}", parsed.KeyId, e)
                            return Unauthorized
                        | Ok actor ->
                            match verifyWith actor with
                            | Ok() -> return Signer actor
                            | Error _ -> return Unauthorized
                }

            match! findCachedKey env.Remote parsed.KeyId with
            | None -> return! fetchAndVerify ()
            | Some cached ->
                match verifyWith cached with
                | Ok() -> return Signer cached
                | Error BadSignature -> return! fetchAndVerify () // the one refetch
                | Error _ -> return Unauthorized
    }

let private envOf (ctx: HttpContext) : Env =
    { DataSource = ctx.GetService<NpgsqlDataSource>()
      Time = ctx.GetService<TimeProvider>()
      Domain = ctx.GetService<Config.MegafonoOptions>().Domain
      App = ctx.GetService<AppClient.AppClient>()
      Remote = ctx.GetService<RemoteActors.Env>()
      Log = ctx.GetService<ILoggerFactory>().CreateLogger "Megafono.Inbox" }

let private dispatch (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    match str activity "type" with
    | Some "Follow" -> InboxFollows.handleFollow env signer activity
    | Some "Undo" -> InboxFollows.handleUndo env signer activity
    | Some "Update" -> InboxActors.handleUpdate env signer activity
    // Accept and Reject of our own follows arrive with outbound follows (User Story 4); everything else is not ours
    | _ -> Task.FromResult(Ignored "activity type not handled")

let private inbox (localUser: string option) : EndpointHandler =
    fun ctx ->
        task {
            let env = envOf ctx
            let limits = ctx.GetService<RateLimits.RateLimits>()

            match! readBody ctx with
            | Error code -> return! status ctx code
            | Ok body ->
                let request = signedRequest ctx body

                match checkWithoutKey env.Time request with
                | Error e ->
                    env.Log.LogInformation("Inbox request refused: {Reason}", describe e)
                    return! status ctx 401
                | Ok parsed ->
                    match! resolveSigner env limits request parsed with
                    | Unauthorized -> return! status ctx 401
                    | RateLimited retryAfter -> return! RateLimits.tooManyRequests ctx retryAfter
                    | Signer signer ->
                        match RateLimits.tryHost limits signer.Host with
                        | Some retryAfter -> return! RateLimits.tooManyRequests ctx retryAfter
                        | None ->
                            // A personal inbox of a user the app does not know
                            let! known =
                                match localUser with
                                | None -> Task.FromResult true
                                | Some id ->
                                    task {
                                        match! env.App.ById id with
                                        | AppClient.Found _
                                        | AppClient.UnavailableWithFallback _ -> return true
                                        | _ -> return false
                                    }

                            if not known then
                                return! status ctx 404
                            else
                                let activity =
                                    try
                                        match JsonNode.Parse(ReadOnlySpan<byte> body) with
                                        | :? JsonObject as o -> Some(o :> JsonNode)
                                        | _ -> None
                                    with :? JsonException ->
                                        None

                                match activity with
                                | None -> return! status ctx 400
                                | Some activity ->
                                    match! dispatch env signer activity with
                                    | Handled -> return! status ctx 202
                                    | Ignored reason ->
                                        env.Log.LogInformation("Inbox activity from {Signer} ignored: {Reason}", signer.Id, reason)
                                        return! status ctx 202
                                    | Refused code -> return! status ctx code
        }

let endpoints: Endpoint list =
    [ POST
          [ route "/inbox" (inbox None)
            routef "/ap/actors/{%s}/inbox" (fun id -> inbox (Some id)) ] ]
