/// SSRF-safe, signed outbound HTTP (research R-13). Every remote URL is attacker-influenced, so the
/// connection itself is checked: DNS is resolved here and only validated addresses are dialed.
module Megafono.Infrastructure.SafeHttp

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Net.Sockets
open System.Security.Cryptography
open System.Security.Cryptography.X509Certificates
open System.Threading
open System.Threading.Tasks

/// Thrown by the connect callback; surfaces as `BlockedAddress`
exception BlockedAddressException of string

let maxResponseBytes = 1024 * 1024
let private timeout = TimeSpan.FromSeconds 10.0
let private maxRedirects = 3

// ---- address rules ---------------------------------------------------------------------------------------

let private isBlockedV4 (b: byte[]) =
    match int b[0], int b[1] with
    | 0, _ -> true // 0.0.0.0/8
    | 10, _ -> true // private
    | 100, second when second >= 64 && second <= 127 -> true // 100.64.0.0/10 CGNAT
    | 127, _ -> true // loopback
    | 169, 254 -> true // link-local
    | 172, second when second >= 16 && second <= 31 -> true // private
    | 192, 168 -> true // private
    | first, _ when first >= 224 -> true // multicast, reserved and broadcast
    | _ -> false

let rec private isBlockedBytes (ip: IPAddress) : bool =
    let b = ip.GetAddressBytes()

    if b.Length = 4 then
        isBlockedV4 b
    else
        let embeddedV4 (offset: int) = IPAddress(b[offset .. offset + 3])
        let first96BitsZero = b[0..11] |> Array.forall ((=) 0uy)
        let nat64 = b[0..3] = [| 0x00uy; 0x64uy; 0xffuy; 0x9buy |] && b[4..11] |> Array.forall ((=) 0uy)
        let mapped = b[0..9] |> Array.forall ((=) 0uy) && b[10] = 0xffuy && b[11] = 0xffuy

        if mapped || nat64 then isBlockedBytes (embeddedV4 12)
        elif first96BitsZero then true // ::, ::1 and the deprecated IPv4-compatible ::a.b.c.d
        elif b[0] = 0x20uy && b[1] = 0x02uy then isBlockedBytes (embeddedV4 2) // 6to4 2002::/16
        elif b[0] = 0xfeuy && (b[1] &&& 0xc0uy) = 0x80uy then true // fe80::/10 link-local
        elif b[0] = 0xfeuy && (b[1] &&& 0xc0uy) = 0xc0uy then true // fec0::/10 site-local, deprecated
        elif (b[0] &&& 0xfeuy) = 0xfcuy then true // fc00::/7 unique local
        elif b[0] = 0xffuy then true // ff00::/8 multicast
        else false

/// True for loopback, unspecified, private, link-local, CGNAT, ULA, multicast and reserved addresses, and
/// for IPv4-mapped (and similar embedded) forms of those
let isBlockedAddress (ip: IPAddress) : bool = isBlockedBytes ip

/// `allowPrivate` is the tests-only switch (Megafono:Federation:AllowPrivateAddresses)
let isAllowedAddress (allowPrivate: bool) (ip: IPAddress) : bool =
    allowPrivate || not (isBlockedAddress ip)

// ---- handler ---------------------------------------------------------------------------------------------

/// Test-only network override: a fake domain (`gts.test`) really lives at another host and port (a container's
/// mapped port), and its certificate comes from a private CA. Requires the AllowPrivateAddresses switch, which is
/// refused in Production, so this can never be active there.
type TestNetwork =
    {
        /// The host name being dialed -> where to connect instead (host, port); None leaves it alone
        Redirect: string -> (string * int) option
        /// The only root certificate trusted for TLS while this override is set
        TrustedRoot: X509Certificate2
    }

let private connect
    (allowPrivate: bool)
    (testNetwork: TestNetwork option)
    (context: SocketsHttpConnectionContext)
    (token: CancellationToken)
    : ValueTask<Stream> =
    ValueTask<Stream>(
        task {
            let requested = context.DnsEndPoint.Host

            let host, port =
                match testNetwork |> Option.bind (fun t -> t.Redirect requested) with
                | Some(realHost, realPort) -> realHost, realPort
                | None -> requested, context.DnsEndPoint.Port

            let! addresses = Dns.GetHostAddressesAsync(host, token)
            let allowed = addresses |> Array.filter (isAllowedAddress allowPrivate)

            if allowed.Length = 0 then
                raise (BlockedAddressException $"Refusing to connect to {requested}: it resolves to no public address")

            // Connect to the validated address itself, so a second DNS answer cannot swap it (rebinding)
            let mutable connected: Stream = null
            let mutable lastError: exn = null

            for address in allowed do
                if isNull connected then
                    let socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp, NoDelay = true)

                    try
                        do! socket.ConnectAsync(IPEndPoint(address, port), token)
                        connected <- new NetworkStream(socket, ownsSocket = true)
                    with e ->
                        socket.Dispose()
                        lastError <- e

            if isNull connected then
                raise (HttpRequestException($"Could not connect to {requested}", lastError))

            return connected
        }
    )

/// Trust only `root`, and still require the certificate to match the host that was asked for
let private trustOnly (root: X509Certificate2) =
    RemoteCertificateValidationCallback(fun _ cert _ errors ->
        if isNull cert
           || errors.HasFlag SslPolicyErrors.RemoteCertificateNameMismatch
           || errors.HasFlag SslPolicyErrors.RemoteCertificateNotAvailable then
            false
        else
            use chain = new X509Chain()
            chain.ChainPolicy.TrustMode <- X509ChainTrustMode.CustomRootTrust
            chain.ChainPolicy.CustomTrustStore.Add root |> ignore
            chain.ChainPolicy.RevocationMode <- X509RevocationMode.NoCheck
            chain.Build(new X509Certificate2(cert)))

/// The handler behind every outbound request: validated dialing, no automatic redirects. TLS still checks the
/// original host name. With a `TestNetwork` the dialed address and the trusted CA are replaced (tests only).
let createHandlerWith (allowPrivate: bool) (testNetwork: TestNetwork option) : SocketsHttpHandler =
    if testNetwork.IsSome && not allowPrivate then
        invalidArg (nameof testNetwork) "A test network override needs the AllowPrivateAddresses switch, which Production refuses"

    let handler = new SocketsHttpHandler()
    handler.AllowAutoRedirect <- false
    handler.UseCookies <- false
    handler.ConnectTimeout <- timeout
    handler.ConnectCallback <- fun context token -> connect allowPrivate testNetwork context token
    testNetwork |> Option.iter (fun t -> handler.SslOptions.RemoteCertificateValidationCallback <- trustOnly t.TrustedRoot)
    handler

/// `createHandlerWith` without an override
let createHandler (allowPrivate: bool) : SocketsHttpHandler = createHandlerWith allowPrivate None

// ---- client ----------------------------------------------------------------------------------------------

type SafeHttpClient =
    { Http: HttpClient
      AllowPrivateAddresses: bool
      UserAgent: string
      Time: TimeProvider }

type FetchError =
    | UnsafeUrl of string
    /// The host resolves only to private, loopback or otherwise blocked addresses (FR-021)
    | BlockedAddress of string
    | TooManyRedirects
    | ResponseTooLarge
    | TimedOut
    | Network of string

type FetchResponse =
    { Status: int
      ContentType: string option
      Body: byte[]
      /// The URL that answered, after redirects
      Uri: Uri }

let userAgent (version: string) (domain: string) = $"Megafono/{version} (+https://{domain}/)"

let create (handler: HttpMessageHandler) (allowPrivate: bool) (userAgent: string) (time: TimeProvider) : SafeHttpClient =
    // Our own per-request deadline covers the body too, so the client's is switched off
    let http = new HttpClient(handler, disposeHandler = true, Timeout = Timeout.InfiniteTimeSpan)

    { Http = http
      AllowPrivateAddresses = allowPrivate
      UserAgent = userAgent
      Time = time }

let private activityAccept =
    "application/activity+json, application/ld+json; profile=\"https://www.w3.org/ns/activitystreams\""

let private checkScheme (client: SafeHttpClient) (uri: Uri) : Result<unit, FetchError> =
    if uri.Scheme = Uri.UriSchemeHttps || (client.AllowPrivateAddresses && uri.Scheme = Uri.UriSchemeHttp) then
        Ok()
    else
        Error(UnsafeUrl $"{uri.Scheme} is not allowed, https only")

/// Reads at most `maxResponseBytes`; a longer body is an error, not a truncation
let private readCapped (response: HttpResponseMessage) (token: CancellationToken) : Task<Result<byte[], FetchError>> =
    task {
        match response.Content.Headers.ContentLength |> Option.ofNullable with
        | Some length when length > int64 maxResponseBytes -> return Error ResponseTooLarge
        | _ ->
            use! stream = response.Content.ReadAsStreamAsync token
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 16384
            let mutable fin = false
            let mutable tooLarge = false

            while not fin && not tooLarge do
                let! read = stream.ReadAsync(chunk, 0, chunk.Length, token)

                if read = 0 then fin <- true
                elif buffer.Length + int64 read > int64 maxResponseBytes then tooLarge <- true
                else buffer.Write(chunk, 0, read)

            return if tooLarge then Error ResponseTooLarge else Ok(buffer.ToArray())
    }

let private sendOnce (client: SafeHttpClient) (request: HttpRequestMessage) : Task<Result<HttpStatusCode * Uri option * string option * byte[], FetchError>> =
    task {
        use cts = new CancellationTokenSource(timeout)

        try
            use! response = client.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token)
            let location = response.Headers.Location |> Option.ofObj
            let contentType = response.Content.Headers.ContentType |> Option.ofObj |> Option.map (fun c -> c.MediaType)

            match response.StatusCode with
            | HttpStatusCode.MovedPermanently
            | HttpStatusCode.Found
            | HttpStatusCode.SeeOther
            | HttpStatusCode.TemporaryRedirect
            | HttpStatusCode.PermanentRedirect when location.IsSome -> return Ok(response.StatusCode, location, contentType, [||])
            | _ ->
                let! body = readCapped response cts.Token
                return body |> Result.map (fun b -> response.StatusCode, None, contentType, b)
        with
        | :? OperationCanceledException -> return Error TimedOut
        | :? HttpRequestException as e when (match e.InnerException with BlockedAddressException _ -> true | _ -> false) ->
            return Error(BlockedAddress e.InnerException.Message)
        | :? HttpRequestException as e ->
            let detail = if isNull e.InnerException then e.Message else $"{e.Message}: {e.InnerException.Message}"
            return Error(Network detail)
        | :? IOException as e -> return Error(Network e.Message) // the connection dropped while reading the body
    }

let private signRequest (client: SafeHttpClient) (keyId: string) (key: RSA) (httpMethod: HttpMethod) (uri: Uri) (body: byte[] option) (accept: string) =
    let request = new HttpRequestMessage(httpMethod, uri)
    request.Headers.TryAddWithoutValidation("User-Agent", client.UserAgent) |> ignore

    for name, value in HttpSignatures.sign keyId key (client.Time.GetUtcNow()) httpMethod.Method uri body do
        request.Headers.TryAddWithoutValidation(name, value) |> ignore

    match body with
    | Some bytes ->
        let content = new ByteArrayContent(bytes)
        content.Headers.TryAddWithoutValidation("Content-Type", "application/activity+json") |> ignore
        request.Content <- content
    | None -> request.Headers.TryAddWithoutValidation("Accept", accept) |> ignore

    request

/// GET signed with `keyId`/`key`. Redirects are followed manually, at most 3 times, each hop re-checked
/// and re-signed for its own host. Non-2xx answers are returned, not turned into errors.
let signedGetAccepting (accept: string) (client: SafeHttpClient) (keyId: string) (key: RSA) (uri: Uri) : Task<Result<FetchResponse, FetchError>> =
    let rec hop (current: Uri) (redirects: int) =
        task {
            match checkScheme client current with
            | Error e -> return Error e
            | Ok() ->
                use request = signRequest client keyId key HttpMethod.Get current None accept

                match! sendOnce client request with
                | Error e -> return Error e
                | Ok(_, Some location, _, _) ->
                    if redirects >= maxRedirects then
                        return Error TooManyRedirects
                    else
                        let next = if location.IsAbsoluteUri then location else Uri(current, location)
                        return! hop next (redirects + 1)
                | Ok(status, None, contentType, body) ->
                    return
                        Ok
                            { Status = int status
                              ContentType = contentType
                              Body = body
                              Uri = current }
        }

    hop uri 0

/// GET asking for ActivityStreams JSON, signed with `keyId`/`key`
let signedGet (client: SafeHttpClient) (keyId: string) (key: RSA) (uri: Uri) : Task<Result<FetchResponse, FetchError>> =
    signedGetAccepting activityAccept client keyId key uri

/// POST of `body` (the exact bytes, so a retry is identical) with `Digest` and
/// `Content-Type: application/activity+json`. Never follows redirects: a 3xx is the caller's failure to record.
let signedPost (client: SafeHttpClient) (keyId: string) (key: RSA) (uri: Uri) (body: byte[]) : Task<Result<FetchResponse, FetchError>> =
    task {
        match checkScheme client uri with
        | Error e -> return Error e
        | Ok() ->
            use request = signRequest client keyId key HttpMethod.Post uri (Some body) activityAccept

            match! sendOnce client request with
            | Error e -> return Error e
            | Ok(status, _, contentType, responseBody) ->
                return
                    Ok
                        { Status = int status
                          ContentType = contentType
                          Body = responseBody
                          Uri = uri }
    }

/// One line for `last_error` columns and logs
let describeError (error: FetchError) : string =
    match error with
    | UnsafeUrl reason -> $"unsafe URL: {reason}"
    | BlockedAddress reason -> $"blocked address: {reason}"
    | TooManyRedirects -> "too many redirects"
    | ResponseTooLarge -> "response larger than 1 MB"
    | TimedOut -> "timed out"
    | Network reason -> $"network error: {reason}"
