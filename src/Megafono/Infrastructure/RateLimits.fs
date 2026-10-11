/// Two layers of rate limiting for inbox traffic (research R-12, FR-016):
/// 1. per client address, before anything is read or verified;
/// 2. per verified signer host, after the signature checked out.
/// Remote key fetches have a budget of their own, counted against the host the key id claims.
/// Token buckets refill once a minute: the configured number is the burst and the per-minute rate.
module Megafono.Infrastructure.RateLimits

open System
open System.Net
open System.Threading
open System.Threading.RateLimiting
open System.Threading.Tasks
open Microsoft.AspNetCore.Http

type RateLimits =
    { Address: PartitionedRateLimiter<string>
      Host: PartitionedRateLimiter<string>
      KeyFetch: PartitionedRateLimiter<string>
      /// Peers whose `X-Forwarded-For` is believed (the gateway)
      TrustedProxies: IPNetwork list }

let private bucket (perMinute: int) : PartitionedRateLimiter<string> =
    PartitionedRateLimiter.Create<string, string>(fun key ->
        RateLimitPartition.GetTokenBucketLimiter(
            key,
            fun _ ->
                TokenBucketRateLimiterOptions(
                    TokenLimit = perMinute,
                    TokensPerPeriod = perMinute,
                    ReplenishmentPeriod = TimeSpan.FromMinutes 1.0,
                    AutoReplenishment = true,
                    QueueLimit = 0
                )
        ))

let private parseNetwork (entry: string) : IPNetwork =
    match IPNetwork.TryParse entry with
    | true, network -> network
    | _ ->
        // A single address is a /32 or /128 network
        let address = IPAddress.Parse entry
        IPNetwork(address, (if address.AddressFamily = Net.Sockets.AddressFamily.InterNetwork then 32 else 128))

let create (options: Config.MegafonoOptions) : RateLimits =
    { Address = bucket options.RateLimit.PerAddress
      Host = bucket options.RateLimit.PerHost
      KeyFetch = bucket options.RateLimit.PerHost
      TrustedProxies = options.TrustedProxies |> List.map parseNetwork }

let private normalize (address: IPAddress) = if address.IsIPv4MappedToIPv6 then address.MapToIPv4() else address

/// The client's address. `X-Forwarded-For` counts only when the direct peer is a trusted proxy, and then the
/// right-most entry that is not itself a trusted proxy is taken (the one our own proxy saw; anything to its left
/// is whatever the client claimed). Otherwise it is the peer: a client cannot pick its own bucket.
let clientAddress (trusted: IPNetwork list) (peer: IPAddress) (forwardedFor: string) : string =
    let isTrusted (a: IPAddress) = trusted |> List.exists (fun n -> n.Contains(normalize a))
    let peer = if isNull peer then IPAddress.None else normalize peer

    if trusted.IsEmpty || not (isTrusted peer) || String.IsNullOrWhiteSpace forwardedFor then
        string peer
    else
        forwardedFor.Split(',')
        |> Array.map (fun part -> part.Trim())
        |> Array.rev
        |> Array.tryPick (fun part ->
            match IPAddress.TryParse part with
            | true, address when not (isTrusted address) -> Some(string (normalize address))
            | _ -> None)
        |> Option.defaultValue (string peer)

let private retryAfterSeconds (lease: RateLimitLease) : int =
    match lease.TryGetMetadata MetadataName.RetryAfter with
    | true, delay -> max 1 (int (Math.Ceiling delay.TotalSeconds))
    | _ -> 60

/// None when the request may go on, else the seconds to wait
let private acquire (limiter: PartitionedRateLimiter<string>) (key: string) : int option =
    use lease = limiter.AttemptAcquire(key, 1)
    if lease.IsAcquired then None else Some(retryAfterSeconds lease)

let tryAddress (limits: RateLimits) (ctx: HttpContext) : int option =
    let forwarded = ctx.Request.Headers["X-Forwarded-For"].ToString()
    acquire limits.Address (clientAddress limits.TrustedProxies ctx.Connection.RemoteIpAddress forwarded)

/// After verification: one request from `host` (the signer's authority)
let tryHost (limits: RateLimits) (host: string) : int option = acquire limits.Host host

/// Before fetching a key: one fetch for the host the key id claims
let tryKeyFetch (limits: RateLimits) (claimedHost: string) : int option = acquire limits.KeyFetch claimedHost

let tooManyRequests (ctx: HttpContext) (retryAfter: int) : Task =
    ctx.Response.StatusCode <- 429
    ctx.Response.Headers["Retry-After"] <- string retryAfter
    Task.CompletedTask

/// Which requests pay the per-address cost: POSTs to any inbox, and signed GETs of objects and activities
/// (a signed GET makes us fetch a remote key, so it is as expensive as an inbox POST)
let isLimited (ctx: HttpContext) : bool =
    let path = ctx.Request.Path

    if HttpMethods.IsPost ctx.Request.Method then
        path.Equals("/inbox", StringComparison.OrdinalIgnoreCase)
        || (path.StartsWithSegments("/ap/actors", StringComparison.OrdinalIgnoreCase)
            && path.Value.EndsWith("/inbox", StringComparison.OrdinalIgnoreCase))
        || path.Equals("/ap/actor/inbox", StringComparison.OrdinalIgnoreCase)
    elif HttpMethods.IsGet ctx.Request.Method || HttpMethods.IsHead ctx.Request.Method then
        ctx.Request.Headers.ContainsKey "Signature"
        && (path.StartsWithSegments("/ap/objects", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/ap/activities", StringComparison.OrdinalIgnoreCase))
    else
        false

/// Layer 1, before anything is read: 429 with `Retry-After` past the per-address budget
let middleware (limits: RateLimits) : Func<HttpContext, RequestDelegate, Task> =
    Func<HttpContext, RequestDelegate, Task>(fun ctx next ->
        if isLimited ctx then
            match tryAddress limits ctx with
            | Some retryAfter -> tooManyRequests ctx retryAfter
            | None -> next.Invoke ctx
        else
            next.Invoke ctx)
