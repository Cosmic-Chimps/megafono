/// The reference gateway on real Kestrel in front of two stub upstreams (T073, FR-063, FR-005)
module Megafono.Tests.Integration.GatewayTests

open System
open System.Collections.Concurrent
open System.IO
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

let private withRig (test: Rig -> Task) : Task =
    task {
        let! megafono = startStub "megafono"
        let! app = startStub "app"

        let gateway =
            Megafono.Gateway.Program.buildGateway
                [||]
                [ "urls", "http://127.0.0.1:0"
                  "ReverseProxy:Clusters:megafono:Destinations:primary:Address", megafono.Address
                  "ReverseProxy:Clusters:app:Destinations:primary:Address", app.Address ]

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
