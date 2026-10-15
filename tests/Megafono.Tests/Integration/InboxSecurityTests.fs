module Megafono.Tests.Integration.InboxSecurityTests

open System
open System.Globalization
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.Signed
open Megafono.Tests.Fixtures.PostgresFixture

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      Stub: StubRemote.StubRemote
      Client: HttpClient }

let private start (pg: PostgresFixture) (extra: (string * string) list) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-lock" "lock" with Locked = true }
        app.AddUser { FakeApp.user "u-open" "open" with Locked = false }
        let! stub = StubRemote.start [ "alice" ]
        let! host = MegafonoHost.start { MegafonoHost.settings cs app.Url (FakeTime()) with Extra = extra }

        return
            { App = app
              Host = host
              Db = NpgsqlDataSource.Create cs
              Stub = stub
              Client = new HttpClient() }
    }

let private stop (w: World) =
    w.Client.Dispose()
    (w.Host :> IAsyncDisposable).DisposeAsync().AsTask().GetAwaiter().GetResult()

let private inbox (w: World) = w.Host.FederationUrl + "/inbox"

/// An activity that would reach the app if it were accepted: a Follow of a locked user
let private follow (w: World) (suffix: string) = followBody w.Stub "alice" suffix (ourActor "u-lock")

let private like (w: World) (suffix: string) =
    $"""{{"id":"{w.Stub.ActorId "alice"}/likes/{suffix}","type":"Like","actor":"{w.Stub.ActorId "alice"}","object":"https://elsewhere.example/post/1"}}"""

let private status (response: HttpResponseMessage) = response.StatusCode

/// Headers for a POST signed by hand, to break one thing at a time
let private manual (url: string) (body: byte[]) (keyId: string) (key: RSA) (algorithm: string) (declared: string list) (date: DateTimeOffset) =
    let uri = Uri url
    let dateText = date.UtcDateTime.ToString("r", CultureInfo.InvariantCulture)
    let digest = digestValue body

    let request: SignedRequest =
        { Method = "POST"
          PathAndQuery = uri.PathAndQuery
          Headers = [ "host", uri.Authority; "date", dateText; "digest", digest ]
          Body = body }

    let text =
        match signingString declared request with
        | Ok t -> t
        | Error e -> failwith (describe e)

    let signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes text, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))

    [ "Date", dateText
      "Digest", digest
      "Signature", $"keyId=\"{keyId}\",algorithm=\"{algorithm}\",headers=\"{String.Join(' ', declared)}\",signature=\"{signature}\"" ]

let private nothingReachedTheApp (w: World) =
    task {
        do! Task.Delay 400
        let! events = Db.count w.Db "SELECT count(*) FROM app_events"
        let! follows = Db.count w.Db "SELECT count(*) FROM follows"
        Assert.Equal(0L, events)
        Assert.Equal(0L, follows)
        Assert.Empty w.App.Received
    }

/// The request must be answered with `expected`
let private expectStatus (expected: HttpStatusCode) (label: string) (response: HttpResponseMessage) =
    Assert.True((response.StatusCode = expected), $"{label}: expected {expected}, got {response.StatusCode}")

type InboxSecurityTests(pg: PostgresFixture) =

    // ---- step 1: per-address limit ------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-016 past the per-address limit the answer is 429 with Retry-After``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerAddress", "3" ]
            use _ = MegafonoHost.disposer w.Host
            let body = Encoding.UTF8.GetBytes "{}"

            for _ in 1..3 do
                let! r = postRaw w.Client (inbox w) body []
                Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode)

            let! limited = postRaw w.Client (inbox w) body []
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
            let retryAfter = limited.Headers.RetryAfter.Delta.Value.TotalSeconds
            Assert.InRange(retryAfter, 1.0, 61.0)

            // The personal inbox shares the budget
            let! personal = postRaw w.Client (w.Host.FederationUrl + "/ap/actors/u-lock/inbox") body []
            Assert.Equal(HttpStatusCode.TooManyRequests, personal.StatusCode)
            // And the limiter only guards the inboxes
            let! health = w.Client.GetAsync(w.Host.FederationUrl + "/health")
            Assert.Equal(HttpStatusCode.OK, health.StatusCode)
        }

    [<Fact>]
    member _.``R-12 an untrusted peer cannot get a fresh bucket by sending X-Forwarded-For``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerAddress", "3" ]
            use _ = MegafonoHost.disposer w.Host
            let body = Encoding.UTF8.GetBytes "{}"

            let send (forwarded: string) =
                task {
                    use request = new HttpRequestMessage(HttpMethod.Post, inbox w, Content = new ByteArrayContent(body))
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwarded) |> ignore
                    let! r = w.Client.SendAsync request
                    return r.StatusCode
                }

            for i in 1..3 do
                let! s = send $"10.0.0.{i}"
                Assert.Equal(HttpStatusCode.Unauthorized, s)

            // Every new claimed address still lands in the peer's own, empty bucket
            for i in 4..8 do
                let! s = send $"10.0.0.{i}"
                Assert.Equal(HttpStatusCode.TooManyRequests, s)
        }

    [<Fact>]
    member _.``R-12 behind a trusted proxy X-Forwarded-For gives each client its own bucket``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerAddress", "3"; "Megafono:TrustedProxies", "127.0.0.1" ]
            use _ = MegafonoHost.disposer w.Host
            let body = Encoding.UTF8.GetBytes "{}"

            let send (forwarded: string) =
                task {
                    use request = new HttpRequestMessage(HttpMethod.Post, inbox w, Content = new ByteArrayContent(body))
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwarded) |> ignore
                    let! r = w.Client.SendAsync request
                    return r.StatusCode
                }

            for _ in 1..3 do
                let! s = send "203.0.113.5"
                Assert.Equal(HttpStatusCode.Unauthorized, s)

            let! limited = send "203.0.113.5"
            let! other = send "203.0.113.6"
            Assert.Equal(HttpStatusCode.TooManyRequests, limited)
            Assert.Equal(HttpStatusCode.Unauthorized, other)

            // A client cannot prepend a fake address to dodge its limit: the right-most untrusted entry counts
            let! spoofed = send "198.51.100.77, 203.0.113.5"
            Assert.Equal(HttpStatusCode.TooManyRequests, spoofed)
        }

    // ---- step 2: size --------------------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-010 a body over 1 MB is 413, declared or discovered while reading``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let big = Array.zeroCreate<byte> (1024 * 1024 + 1)

            let! declared = postRaw w.Client (inbox w) big []
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, declared.StatusCode)

            // No Content-Length: chunked, so the size is only known by reading
            use request = new HttpRequestMessage(HttpMethod.Post, inbox w, Content = new StreamContent(new IO.MemoryStream(big)))
            request.Headers.TransferEncodingChunked <- true
            let! chunked = w.Client.SendAsync request
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, chunked.StatusCode)

            // Exactly 1 MB is not too large (it is simply unsigned)
            let! exact = postRaw w.Client (inbox w) (Array.zeroCreate<byte> (1024 * 1024)) []
            Assert.Equal(HttpStatusCode.Unauthorized, exact.StatusCode)
        }

    // ---- steps 3 to 5: signature ---------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-011 a request without a usable signature is 401 and never reaches the app``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let body = Encoding.UTF8.GetBytes(follow w "1")
            let url = inbox w
            let keyId = w.Stub.KeyId "alice"
            let now = DateTimeOffset.UtcNow
            let key = w.Stub.PrivateKey
            let all = [ "(request-target)"; "host"; "date"; "digest" ]

            let refuse (label: string) (bodyToSend: byte[]) (headers: (string * string) list) =
                task {
                    let! r = postRaw w.Client url bodyToSend headers
                    expectStatus HttpStatusCode.Unauthorized label r
                }

            do! refuse "unsigned" body []
            do! refuse "stale date" body (w.Stub.SignAt("alice", "POST", url, Some body, now.AddHours -13.0))
            do! refuse "future date" body (w.Stub.SignAt("alice", "POST", url, Some body, now.AddHours 13.0))
            do! refuse "digest not signed" body (manual url body keyId key "rsa-sha256" [ "(request-target)"; "host"; "date" ] now)
            do! refuse "host not signed" body (manual url body keyId key "rsa-sha256" [ "(request-target)"; "date"; "digest" ] now)
            // the digest does not match the body that arrives
            do! refuse "digest mismatch" (Encoding.UTF8.GetBytes(follow w "2")) (w.Stub.Sign("alice", "POST", url, Some body))
            // another key signs under alice's key id
            use stranger = RSA.Create 2048
            do! refuse "wrong key" body (manual url body keyId stranger "rsa-sha256" all now)
            // a key id nobody serves, and one that is not a URL
            do! refuse "unknown key" body (manual url body "http://127.0.0.1:1/users/ghost#main-key" key "rsa-sha256" all now)
            do! refuse "key id that is not a URL" body (manual url body "alice" key "rsa-sha256" all now)
            do! refuse "algorithm rsa-sha1" body (manual url body keyId key "rsa-sha1" all now)

            // SC-008: none of them produced an event, a follow or a processed marker
            do! nothingReachedTheApp w
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(0L, processed)
        }

    [<Fact>]
    member _.``FR-011 hs2019 and the edges of the date window are accepted``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let body = Encoding.UTF8.GetBytes(like w "1")
            let url = inbox w
            let keyId = w.Stub.KeyId "alice"
            let now = DateTimeOffset.UtcNow
            let all = [ "(request-target)"; "host"; "date"; "digest" ]

            let! hs2019 = postRaw w.Client url body (manual url body keyId w.Stub.PrivateKey "hs2019" all now)
            expectStatus HttpStatusCode.Accepted "hs2019" hs2019
            let! early = postRaw w.Client url body (w.Stub.SignAt("alice", "POST", url, Some body, now.AddHours(-11.9)))
            expectStatus HttpStatusCode.Accepted "11.9 hours ago" early
            let! late = postRaw w.Client url body (w.Stub.SignAt("alice", "POST", url, Some body, now.AddHours 11.9))
            expectStatus HttpStatusCode.Accepted "11.9 hours ahead" late
        }

    [<Fact>]
    member _.``FR-012 a rotated key is accepted after one refetch, and a wrong key costs exactly one refetch``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let url = inbox w
            let actorGets () = w.Stub.Gets |> List.filter (fun g -> g.Path = "/users/alice") |> List.length

            let! first = Signed.postText w.Client w.Stub "alice" url (like w "1")
            expectStatus HttpStatusCode.Accepted "first" first
            Assert.Equal(1, actorGets ())

            // Cached key, valid signature: no fetch
            let! second = Signed.postText w.Client w.Stub "alice" url (like w "2")
            expectStatus HttpStatusCode.Accepted "cached" second
            Assert.Equal(1, actorGets ())

            // The actor rotates its key: the cached one no longer verifies, one refetch fixes it
            w.Stub.RotateKey()
            let! rotated = Signed.postText w.Client w.Stub "alice" url (like w "3")
            expectStatus HttpStatusCode.Accepted "rotated" rotated
            Assert.Equal(2, actorGets ())

            // A signature that is wrong for the new key too: one refetch, then 401
            use stranger = RSA.Create 2048
            let body = Encoding.UTF8.GetBytes(like w "4")
            let! wrong = postRaw w.Client url body (manual url body (w.Stub.KeyId "alice") stranger "rsa-sha256" [ "(request-target)"; "host"; "date"; "digest" ] DateTimeOffset.UtcNow)
            expectStatus HttpStatusCode.Unauthorized "wrong key" wrong
            Assert.Equal(3, actorGets ())
        }

    // ---- step 6: per-verified-host limit --------------------------------------------------------------------

    [<Fact>]
    member _.``FR-016 past the per-verified-host limit the answer is 429 with Retry-After``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerHost", "3" ]
            use _ = MegafonoHost.disposer w.Host

            for i in 1..3 do
                let! r = Signed.postText w.Client w.Stub "alice" (inbox w) (like w $"{i}")
                expectStatus HttpStatusCode.Accepted $"request {i}" r

            let! limited = Signed.postText w.Client w.Stub "alice" (inbox w) (like w "4")
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
            Assert.InRange(limited.Headers.RetryAfter.Delta.Value.TotalSeconds, 1.0, 61.0)
        }

    [<Fact>]
    member _.``R-12 remote key fetches have their own budget per claimed host``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerHost", "3" ]
            use _ = MegafonoHost.disposer w.Host
            let url = inbox w
            let keyId = w.Stub.KeyId "alice"
            use stranger = RSA.Create 2048
            let actorGets () = w.Stub.Gets |> List.filter (fun g -> g.Path = "/users/alice") |> List.length

            // The first request fetches the key (1 of 3), then three wrong signatures each try a refetch
            let! valid = Signed.postText w.Client w.Stub "alice" url (like w "1")
            expectStatus HttpStatusCode.Accepted "valid" valid

            let wrong (i: int) =
                task {
                    let body = Encoding.UTF8.GetBytes(like w $"w{i}")
                    return! postRaw w.Client url body (manual url body keyId stranger "rsa-sha256" [ "(request-target)"; "host"; "date"; "digest" ] DateTimeOffset.UtcNow)
                }

            let! w1 = wrong 1
            let! w2 = wrong 2
            let! w3 = wrong 3
            expectStatus HttpStatusCode.Unauthorized "wrong 1" w1
            expectStatus HttpStatusCode.Unauthorized "wrong 2" w2
            expectStatus HttpStatusCode.TooManyRequests "wrong 3" w3
            Assert.Equal(3, actorGets ())
        }

    // ---- steps 7 to 10 ---------------------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-010 a signed body that is not a JSON object is 400``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host

            for body in [ "not json {"; "[1,2]"; "\"text\""; "" ] do
                let! r = Signed.postText w.Client w.Stub "alice" (inbox w) body
                expectStatus HttpStatusCode.BadRequest $"body '{body}'" r
        }

    [<Fact>]
    member _.``FR-014 a repeated activity id is 202 and handled once``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let body = followBody w.Stub "alice" "1" (ourActor "u-open")

            for _ in 1..3 do
                let! r = Signed.postText w.Client w.Stub "alice" (inbox w) body
                expectStatus HttpStatusCode.Accepted "repeat" r

            do! waitUntil "the Accept" (TimeSpan.FromSeconds 20.0) (fun () -> w.Stub.Posts.Length >= 1)
            do! Task.Delay 600
            Assert.Equal(1, w.Stub.Posts.Length)
            let! events = Db.count w.Db "SELECT count(*) FROM app_events"
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(1L, events)
            Assert.Equal(1L, processed)
        }

    [<Fact>]
    member _.``FR-013 an unknown activity type is 202 and dropped``() : Task =
        task {
            let! w = start pg []
            use _ = MegafonoHost.disposer w.Host
            let actor = w.Stub.ActorId "alice"

            for kind in [ "Like"; "Announce"; "Create"; "Delete"; "Block"; "Flag"; "Accept"; "Reject"; "Whatever" ] do
                let body = $"""{{"id":"{actor}/x/{kind}","type":"{kind}","actor":"{actor}","object":"https://elsewhere.example/1"}}"""
                let! r = Signed.postText w.Client w.Stub "alice" (inbox w) body
                expectStatus HttpStatusCode.Accepted kind r

            // An object without a type is not an activity at all, but it is not an error either
            let! untyped = Signed.postText w.Client w.Stub "alice" (inbox w) "{}"
            expectStatus HttpStatusCode.Accepted "no type" untyped

            do! nothingReachedTheApp w
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            Assert.Equal(0L, processed)
        }

    [<Fact>]
    member _.``SC-008 requests that fail any step never produce an app event``() : Task =
        task {
            let! w = start pg [ "Megafono:RateLimit:PerAddress", "12" ]
            use _ = MegafonoHost.disposer w.Host
            let url = inbox w
            let body = Encoding.UTF8.GetBytes(follow w "1")
            use stranger = RSA.Create 2048

            // Each of these carries a Follow that, if accepted, would reach the app as follow.requested
            let! _ = postRaw w.Client url body []
            let! _ = postRaw w.Client url body (w.Stub.SignAt("alice", "POST", url, Some body, DateTimeOffset.UtcNow.AddDays -3.0))
            let! _ = postRaw w.Client url (Encoding.UTF8.GetBytes(follow w "2")) (w.Stub.Sign("alice", "POST", url, Some body))
            let! _ = postRaw w.Client url body (manual url body (w.Stub.KeyId "alice") stranger "rsa-sha256" [ "(request-target)"; "host"; "date"; "digest" ] DateTimeOffset.UtcNow)
            let! _ = postRaw w.Client url (Array.zeroCreate<byte> (1024 * 1024 + 1)) []

            // The limit: past 12 requests from this address everything is 429, signed or not
            for _ in 1..8 do
                let! _ = Signed.post w.Client w.Stub "alice" url body
                ()

            let! limited = Signed.post w.Client w.Stub "alice" url body
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)

            // What did get through (valid signatures before the limit) is the real thing; the refused ones left nothing behind
            let! processed = Db.count w.Db "SELECT count(*) FROM processed_activities"
            let! events = Db.count w.Db "SELECT count(*) FROM app_events"
            Assert.Equal(1L, processed) // the one valid Follow, handled once, the other copies were duplicates
            Assert.Equal(1L, events)
        }

// ---- the address rules, without HTTP -------------------------------------------------------------------------

[<Fact>]
let ``R-12 clientAddress trusts X-Forwarded-For only from a trusted peer``() =
    let trusted = [ Net.IPNetwork.Parse "172.30.0.10/32"; Net.IPNetwork.Parse "10.1.0.0/16" ]
    let peer (s: string) = IPAddress.Parse s
    let address = RateLimits.clientAddress trusted

    // an untrusted peer: the header is ignored
    Assert.Equal("203.0.113.9", address (peer "203.0.113.9") "1.2.3.4")
    // the gateway: the client it saw
    Assert.Equal("1.2.3.4", address (peer "172.30.0.10") "1.2.3.4")
    // a chain: the right-most entry that is not a trusted proxy; anything to its left is claimed by the client
    Assert.Equal("1.2.3.4", address (peer "172.30.0.10") "9.9.9.9, 1.2.3.4, 10.1.2.3")
    // trusted CIDR range
    Assert.Equal("1.2.3.4", address (peer "10.1.7.7") "1.2.3.4")
    // no header, or only garbage: the peer
    Assert.Equal("172.30.0.10", address (peer "172.30.0.10") "")
    Assert.Equal("172.30.0.10", address (peer "172.30.0.10") "not-an-ip")
    // IPv4-mapped IPv6 peers are the same peer
    Assert.Equal("1.2.3.4", address (peer "::ffff:172.30.0.10") "1.2.3.4")
    // with no trusted proxies configured, the header never counts
    Assert.Equal("172.30.0.10", RateLimits.clientAddress [] (peer "172.30.0.10") "1.2.3.4")

[<Fact>]
let ``R-12 only inbox POSTs and signed object GETs pay the per-address cost``() =
    let ctx (httpMethod: string) (path: string) (signed: bool) =
        let c = Microsoft.AspNetCore.Http.DefaultHttpContext()
        c.Request.Method <- httpMethod
        c.Request.Path <- Microsoft.AspNetCore.Http.PathString path
        if signed then c.Request.Headers["Signature"] <- "keyId=\"k\""
        c

    for path in [ "/inbox"; "/ap/actors/u1/inbox"; "/ap/actor/inbox"; "/INBOX" ] do
        Assert.True(RateLimits.isLimited (ctx "POST" path false), path)

    Assert.True(RateLimits.isLimited (ctx "GET" "/ap/objects/abc" true))
    Assert.True(RateLimits.isLimited (ctx "GET" "/ap/activities/abc" true))
    // an unsigned GET of an object is cheap (no key fetch), the rest of the site is not limited here
    Assert.False(RateLimits.isLimited (ctx "GET" "/ap/objects/abc" false))
    Assert.False(RateLimits.isLimited (ctx "GET" "/ap/actors/u1" true))
    Assert.False(RateLimits.isLimited (ctx "GET" "/health" false))
    Assert.False(RateLimits.isLimited (ctx "POST" "/internal/actors/u1" false))
