/// The reference gateway on real Kestrel in front of two stub upstreams (T073, FR-063, FR-005)
module Megafono.Tests.Integration.GatewayTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.Extensions.DependencyInjection
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

/// What a stub upstream saw
type Seen =
    { Method: string
      /// Raw request target, path and query as received
      Target: string
      Host: string
      Headers: (string * string) list
      Body: byte[] }

type Stub(app: WebApplication, seen: ConcurrentQueue<Seen>) =
    member _.Address =
        app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

    member _.Seen = seen.ToArray()

    interface IAsyncDisposable with
        member _.DisposeAsync() = app.DisposeAsync()

let private startStub (name: string) : Task<Stub> =
    task {
        let seen = ConcurrentQueue<Seen>()
        let builder = WebApplication.CreateBuilder()
        builder.WebHost.UseUrls "http://127.0.0.1:0" |> ignore
        let app = builder.Build()

        app.Run(fun ctx ->
            task {
                use buffer = new MemoryStream()
                do! ctx.Request.Body.CopyToAsync buffer

                seen.Enqueue
                    { Method = ctx.Request.Method
                      Target = ctx.Features.Get<IHttpRequestFeature>().RawTarget
                      Host = ctx.Request.Host.Value
                      Headers = [ for h in ctx.Request.Headers -> h.Key, string h.Value ]
                      Body = buffer.ToArray() }

                ctx.Response.Headers["X-Stub"] <- name
                do! ctx.Response.WriteAsync name
            }
            :> Task)

        do! app.StartAsync()
        return Stub(app, seen)
    }

type Rig =
    { Megafono: Stub
      App: Stub
      Gateway: WebApplication
      Client: HttpClient }

/// `extra` goes on top of the routing settings: the gateway's own settings such as `Gateway:TrustedProxies`
let private withRigWith (extra: (string * string) list) (test: Rig -> Task) : Task =
    task {
        let! megafono = startStub "megafono"
        let! app = startStub "app"

        let gateway =
            Megafono.Gateway.Program.buildGateway
                [||]
                ([ "urls", "http://127.0.0.1:0"
                   "ReverseProxy:Clusters:megafono:Destinations:primary:Address", megafono.Address
                   "ReverseProxy:Clusters:app:Destinations:primary:Address", app.Address ]
                 @ extra)

        do! gateway.StartAsync()

        let gatewayAddress =
            gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

        use client = new HttpClient(BaseAddress = Uri gatewayAddress)

        try
            do! test { Megafono = megafono; App = app; Gateway = gateway; Client = client }
        finally
            gateway.DisposeAsync().AsTask().Wait()
            (megafono :> IAsyncDisposable).DisposeAsync().AsTask().Wait()
            (app :> IAsyncDisposable).DisposeAsync().AsTask().Wait()
    }
    :> Task

let private withRig (test: Rig -> Task) : Task = withRigWith [] test

let private send (client: HttpClient) (httpMethod: string) (target: string) (host: string) (body: byte[] option) =
    task {
        use request = new HttpRequestMessage(HttpMethod httpMethod, target)
        request.Headers.Host <- host
        body |> Option.iter (fun b -> request.Content <- new ByteArrayContent(b))
        let! response = client.SendAsync request
        return! response.Content.ReadAsStringAsync()
    }

let private megafonoPaths =
    [ "/.well-known/webfinger?resource=acct:alex@megafono.test"
      "/.well-known/nodeinfo"
      "/nodeinfo"
      "/nodeinfo/2.1"
      "/ap/actors/42"
      "/ap/actors/42/followers?page=true"
      "/inbox" ]

let private appPaths =
    [ "/"
      "/u/alex"
      "/.well-known/acme-challenge/x"
      "/.well-known/host-meta"
      "/.well-known/nodeinfo/extra"
      "/inboxes"
      "/api/anything?a=1" ]

[<Fact>]
let ``FR-063 Megafono routes reach Megafono and everything else reaches the app`` () =
    withRig (fun rig ->
        task {
            for path in megafonoPaths do
                let! who = send rig.Client "GET" path "megafono.test" None
                Assert.Equal("megafono", who)

            for path in appPaths do
                let! who = send rig.Client "GET" path "megafono.test" None
                Assert.Equal("app", who)

            Assert.Equal(megafonoPaths.Length, rig.Megafono.Seen.Length)
            Assert.Equal(appPaths.Length, rig.App.Seen.Length)
        })

[<Fact>]
let ``FR-063 the original Host reaches the upstream unchanged`` () =
    withRig (fun rig ->
        task {
            let! _ = send rig.Client "GET" "/ap/actors/1" "megafono.test" None
            let! _ = send rig.Client "GET" "/u/alex" "megafono.test:8443" None
            Assert.Equal("megafono.test", rig.Megafono.Seen[0].Host)
            Assert.Equal("megafono.test:8443", rig.App.Seen[0].Host)
        })

[<Fact>]
let ``FR-063 path, query and body bytes arrive byte for byte`` () =
    withRig (fun rig ->
        task {
            let body = Array.init 512 byte |> Array.append [| 0xEFuy; 0xBBuy; 0xBFuy; 0uy; 13uy; 10uy |]
            let target = "/inbox?a=b%26c&x=%E2%9C%93&sp=a%20b&plus=1+2&empty="
            let! _ = send rig.Client "POST" target "megafono.test" (Some body)
            let seen = rig.Megafono.Seen[0]
            Assert.Equal("POST", seen.Method)
            Assert.Equal(target, seen.Target)
            Assert.Equal<byte[]>(body, seen.Body)

            let encodedPath = "/ap/actors/caf%C3%A9%20x/outbox?page=2"
            let! _ = send rig.Client "GET" encodedPath "megafono.test" None
            Assert.Equal(encodedPath, rig.Megafono.Seen[1].Target)
        })

[<Fact>]
let ``FR-063 X-Forwarded-For and X-Forwarded-Proto are set`` () =
    withRig (fun rig ->
        task {
            let! _ = send rig.Client "GET" "/ap/actors/1" "megafono.test" None
            let headers = rig.Megafono.Seen[0].Headers |> dict
            Assert.Equal("127.0.0.1", headers["X-Forwarded-For"])
            Assert.Equal("http", headers["X-Forwarded-Proto"])
            Assert.True(headers.ContainsKey "X-Forwarded-Host")
        })

[<Fact>]
let ``FR-005 a draft-cavage signed request still verifies at Megafono through the gateway`` () =
    withRig (fun rig ->
        task {
            use key = RSA.Create 2048
            let now = DateTimeOffset.UtcNow
            let body = Text.Encoding.UTF8.GetBytes """{"type":"Follow","id":"https://remote.test/f/1"}"""
            let uri = Uri "http://megafono.test/inbox?x=%E2%9C%93"
            let signed = sign "https://remote.test/users/alex#main-key" key now "POST" uri (Some body)

            use request = new HttpRequestMessage(HttpMethod.Post, "/inbox?x=%E2%9C%93")
            request.Headers.Host <- uri.Authority
            request.Content <- new ByteArrayContent(body)

            for name, value in signed do
                request.Headers.TryAddWithoutValidation(name, value) |> ignore

            let! response = rig.Client.SendAsync request
            Assert.True response.IsSuccessStatusCode

            let seen = rig.Megafono.Seen[0]

            let received: SignedRequest =
                { Method = seen.Method
                  PathAndQuery = seen.Target
                  Headers = seen.Headers
                  Body = seen.Body }

            Assert.Equal(Ok(), draftCavage.Verify (FakeTime now) key received)
        })

/// What the Megáfono stub saw for one GET that carries the given `X-Forwarded-For`, through a gateway with `extra`
let private forwardedSeen (extra: (string * string) list) (forwardedFor: string) : Task<(string * string) list> =
    let mutable headers = []

    task {
        do!
            withRigWith extra (fun rig ->
                task {
                    use request = new HttpRequestMessage(HttpMethod.Get, "/ap/actors/1")
                    request.Headers.Host <- "megafono.test"
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor) |> ignore
                    request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https") |> ignore
                    let! _ = rig.Client.SendAsync request
                    headers <- rig.Megafono.Seen[0].Headers
                })

        return headers
    }

let private header (name: string) (headers: (string * string) list) =
    headers |> List.filter (fun (k, _) -> k = name) |> List.map snd |> String.concat "|"

[<Fact>]
let ``FR-063 without Gateway:TrustedProxies a client-sent X-Forwarded-For and -Proto are replaced`` () =
    task {
        let! headers = forwardedSeen [] "203.0.113.5"
        Assert.Equal("127.0.0.1", header "X-Forwarded-For" headers)
        Assert.Equal("http", header "X-Forwarded-Proto" headers)
    }

[<Fact>]
let ``FR-063 a trusted proxy's X-Forwarded-For and -Proto reach the upstream, the Host stays`` () =
    task {
        let! headers = forwardedSeen [ "Gateway:TrustedProxies", "127.0.0.1" ] "203.0.113.5"
        Assert.Equal("203.0.113.5", header "X-Forwarded-For" headers)
        Assert.Equal("https", header "X-Forwarded-Proto" headers)
        Assert.Equal("megafono.test", header "X-Forwarded-Host" headers)
        Assert.Equal("megafono.test", header "Host" headers)
    }

[<Fact>]
let ``FR-063 behind a trusted proxy the right-most untrusted entry is the client`` () =
    task {
        let! headers = forwardedSeen [ "Gateway:TrustedProxies", "127.0.0.1" ] "198.51.100.77, 203.0.113.5"
        Assert.Equal("203.0.113.5", header "X-Forwarded-For" headers)
    }

[<Fact>]
let ``FR-063 a chain of trusted proxies is walked`` () =
    task {
        let! headers = forwardedSeen [ "Gateway:TrustedProxies", "127.0.0.1, 10.0.0.0/8" ] "198.51.100.77, 203.0.113.5, 10.1.2.3"
        Assert.Equal("203.0.113.5", header "X-Forwarded-For" headers)
    }

[<Fact>]
let ``FR-063 a trusted proxy that is not the peer is ignored`` () =
    task {
        for trusted in [ "10.99.0.1"; "10.99.0.0/16" ] do
            let! headers = forwardedSeen [ "Gateway:TrustedProxies", trusted ] "203.0.113.5"
            Assert.Equal("127.0.0.1", header "X-Forwarded-For" headers)
            Assert.Equal("http", header "X-Forwarded-Proto" headers)
    }

[<Fact>]
let ``FR-063 Gateway:TrustedProxies is also accepted indexed`` () =
    task {
        let! headers =
            forwardedSeen [ "Gateway:TrustedProxies:0", "10.99.0.1"; "Gateway:TrustedProxies:1", "127.0.0.1" ] "203.0.113.5"

        Assert.Equal("203.0.113.5", header "X-Forwarded-For" headers)
    }

[<Fact>]
let ``FR-063 a Gateway:TrustedProxies entry that is not an address or range stops the gateway`` () =
    let build (value: string) =
        Megafono.Gateway.Program.buildGateway [||] [ "urls", "http://127.0.0.1:0"; "Gateway:TrustedProxies", value ]
        |> ignore

    let error = Assert.Throws<Exception>(fun () -> build "127.0.0.1, not-an-ip")
    Assert.Contains("not-an-ip", error.Message)
    Assert.Contains("Gateway:TrustedProxies", error.Message)
    Assert.Throws<Exception>(fun () -> build "10.0.0.0/99") |> ignore

type GatewayRateLimitTests(pg: PostgresFixture) =

    /// Megáfono (3 requests per address) behind the gateway; the HttpClient plays the TLS proxy at 127.0.0.1.
    /// Returns the status of an unsigned POST /inbox claiming `client` through `X-Forwarded-For`, per call
    let run (gatewayExtra: (string * string) list) (test: (string -> Task<HttpStatusCode>) -> Task) : Task =
        task {
            let! cs = pg.CreateDatabase()
            let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret

            let! host =
                MegafonoHost.start
                    { MegafonoHost.settings cs app.Url (FakeTime()) with
                        Extra = [ "Megafono:RateLimit:PerAddress", "3"; "Megafono:TrustedProxies", "127.0.0.1" ] }

            use _ = MegafonoHost.disposer host

            let gateway =
                Megafono.Gateway.Program.buildGateway
                    [||]
                    ([ "urls", "http://127.0.0.1:0"
                       "ReverseProxy:Clusters:megafono:Destinations:primary:Address", host.FederationUrl
                       "ReverseProxy:Clusters:app:Destinations:primary:Address", app.Url ]
                     @ gatewayExtra)

            do! gateway.StartAsync()

            let address =
                gateway.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

            use client = new HttpClient(BaseAddress = Uri address)

            let send (clientAddress: string) =
                task {
                    use request = new HttpRequestMessage(HttpMethod.Post, "/inbox", Content = new ByteArrayContent(Text.Encoding.UTF8.GetBytes "{}"))
                    request.Headers.Host <- "megafono.test"
                    request.Headers.TryAddWithoutValidation("X-Forwarded-For", clientAddress) |> ignore
                    request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https") |> ignore
                    let! response = client.SendAsync request
                    return response.StatusCode
                }

            try
                do! test send
            finally
                gateway.DisposeAsync().AsTask().Wait()
                (app :> IAsyncDisposable).DisposeAsync().AsTask().Wait()
        }

    [<Fact>]
    member _.``FR-016 R-12 through a gateway with Gateway:TrustedProxies each client has its own bucket``() : Task =
        run [ "Gateway:TrustedProxies", "127.0.0.1" ] (fun send ->
            task {
                for _ in 1..3 do
                    let! s = send "203.0.113.5"
                    Assert.Equal(HttpStatusCode.Unauthorized, s)

                let! limited = send "203.0.113.5"
                let! other = send "203.0.113.6"
                Assert.Equal(HttpStatusCode.TooManyRequests, limited)
                Assert.Equal(HttpStatusCode.Unauthorized, other)
            })

    [<Fact>]
    member _.``FR-016 R-12 through a gateway without Gateway:TrustedProxies all clients share the proxy's bucket``() : Task =
        run [] (fun send ->
            task {
                for _ in 1..3 do
                    let! s = send "203.0.113.5"
                    Assert.Equal(HttpStatusCode.Unauthorized, s)

                let! other = send "203.0.113.6"
                Assert.Equal(HttpStatusCode.TooManyRequests, other)
            })
