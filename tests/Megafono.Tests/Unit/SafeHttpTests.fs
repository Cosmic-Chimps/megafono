module Megafono.Tests.Unit.SafeHttpTests

open System
open System.Net
open System.Security.Cryptography
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Extensions
open Microsoft.Extensions.DependencyInjection
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures

let private parse (text: string) = IPAddress.Parse text

// ---- address rules (R-13, FR-021) ------------------------------------------------------------------------

[<Theory>]
[<InlineData("10.0.0.1")>]
[<InlineData("10.255.255.255")>]
[<InlineData("172.16.0.1")>]
[<InlineData("172.31.255.255")>]
[<InlineData("192.168.0.1")>]
[<InlineData("192.168.255.255")>]
[<InlineData("127.0.0.1")>]
[<InlineData("127.255.255.254")>]
[<InlineData("0.0.0.0")>]
[<InlineData("0.1.2.3")>]
[<InlineData("169.254.0.1")>]
[<InlineData("169.254.169.254")>] // cloud metadata
[<InlineData("100.64.0.1")>]
[<InlineData("100.127.255.255")>]
[<InlineData("224.0.0.1")>]
[<InlineData("239.255.255.255")>]
[<InlineData("240.0.0.1")>]
[<InlineData("255.255.255.255")>]
[<InlineData("::1")>]
[<InlineData("::")>]
[<InlineData("fe80::1")>]
[<InlineData("febf::1")>]
[<InlineData("fc00::1")>]
[<InlineData("fd12:3456:789a::1")>]
[<InlineData("ff00::1")>]
[<InlineData("ff02::1")>]
[<InlineData("::ffff:10.0.0.1")>]
[<InlineData("::ffff:127.0.0.1")>]
[<InlineData("::ffff:192.168.1.1")>]
[<InlineData("::ffff:172.16.0.1")>]
[<InlineData("::ffff:169.254.169.254")>]
[<InlineData("::ffff:100.64.0.1")>]
[<InlineData("::ffff:0.0.0.0")>]
[<InlineData("::ffff:224.0.0.1")>]
[<InlineData("::10.0.0.1")>] // IPv4-compatible
[<InlineData("64:ff9b::a00:1")>] // NAT64 of 10.0.0.1
[<InlineData("2002:a00:1::1")>] // 6to4 of 10.0.0.1
let ``R-13 blocked addresses`` (text: string) =
    Assert.True(SafeHttp.isBlockedAddress (parse text))
    Assert.False(SafeHttp.isAllowedAddress false (parse text))

[<Theory>]
[<InlineData("8.8.8.8")>]
[<InlineData("1.1.1.1")>]
[<InlineData("93.184.216.34")>]
[<InlineData("172.15.255.255")>] // just below 172.16/12
[<InlineData("172.32.0.1")>] // just above it
[<InlineData("100.63.255.255")>] // just below CGNAT
[<InlineData("100.128.0.1")>] // just above it
[<InlineData("169.253.0.1")>]
[<InlineData("192.167.0.1")>]
[<InlineData("223.255.255.255")>]
[<InlineData("11.0.0.1")>]
[<InlineData("2606:4700:4700::1111")>]
[<InlineData("2001:4860:4860::8888")>]
[<InlineData("::ffff:8.8.8.8")>]
[<InlineData("2002:808:808::1")>] // 6to4 of 8.8.8.8
[<InlineData("64:ff9b::808:808")>] // NAT64 of 8.8.8.8
let ``R-13 public addresses are allowed`` (text: string) =
    Assert.False(SafeHttp.isBlockedAddress (parse text))
    Assert.True(SafeHttp.isAllowedAddress false (parse text))

[<Theory>]
[<InlineData("10.0.0.1")>]
[<InlineData("127.0.0.1")>]
[<InlineData("::1")>]
[<InlineData("::ffff:10.0.0.1")>]
[<InlineData("fd00::1")>]
let ``R-13 the AllowPrivateAddresses switch lets blocked addresses through`` (text: string) =
    Assert.True(SafeHttp.isAllowedAddress true (parse text))

[<Fact>]
let ``R-13 the user agent names the software and the domain`` () =
    Assert.Equal("Megafono/0.1.0 (+https://megafono.test/)", SafeHttp.userAgent "0.1.0" "megafono.test")

// ---- the client against a real local server -------------------------------------------------------------

let private keyId = "https://megafono.test/ap/actor#main-key"
let private key = lazy (RSA.Create 2048)

/// Hosts `handler` on a loopback port and gives the test its base URL; `signatureResults` collects what the
/// server made of each request's signature.
let private withServer (handler: HttpContext -> ResizeArray<Result<unit, VerifyError>> -> Task<unit>) (test: string -> Task) : Task =
    task {
        let app = WebApplication.CreateBuilder().Build()
        let results = ResizeArray<Result<unit, VerifyError>>()
        app.Urls.Add "http://127.0.0.1:0"
        app.Run(RequestDelegate(fun ctx -> handler ctx results :> Task))
        do! app.StartAsync()

        try
            let address =
                app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head

            do! test address
        finally
            app.StopAsync().GetAwaiter().GetResult()
    }

let private readBody (ctx: HttpContext) =
    task {
        use buffer = new IO.MemoryStream()
        do! ctx.Request.Body.CopyToAsync buffer
        return buffer.ToArray()
    }

/// Verifies the incoming signature against our test key and records the outcome
let private checkSignature (ctx: HttpContext) (results: ResizeArray<Result<unit, VerifyError>>) (body: byte[]) =
    let request =
        { Method = ctx.Request.Method
          PathAndQuery = ctx.Request.GetEncodedPathAndQuery()
          Headers = [ for h in ctx.Request.Headers -> h.Key, string h.Value ]
          Body = body }

    results.Add(draftCavage.Verify TimeProvider.System key.Value request)

let private client allowPrivate =
    SafeHttp.create (SafeHttp.createHandler allowPrivate) allowPrivate (SafeHttp.userAgent "0.1.0" "megafono.test") TimeProvider.System

let private asString (response: SafeHttp.FetchResponse) = Encoding.UTF8.GetString response.Body

[<Fact>]
let ``R-13 a loopback target is refused by the connection itself`` () : Task =
    task {
        let c = client false
        // https, so the scheme check passes and the address check is what stops it; nothing listens there anyway
        let! result = SafeHttp.signedGet c keyId key.Value (Uri "https://127.0.0.1:9/")

        match result with
        | Error(SafeHttp.BlockedAddress _) -> ()
        | other -> failwith $"expected a refused connection, got {other}"

        let! localhost = SafeHttp.signedGet c keyId key.Value (Uri "https://localhost:9/")
        Assert.True(localhost.IsError)
    }

[<Fact>]
let ``R-13 plain http is refused without the test switch`` () : Task =
    task {
        let! result = SafeHttp.signedGet (client false) keyId key.Value (Uri "http://example.com/")
        Assert.True(match result with Error(SafeHttp.UnsafeUrl _) -> true | _ -> false)

        let! post = SafeHttp.signedPost (client false) keyId key.Value (Uri "http://example.com/inbox") [| 1uy |]
        Assert.True(match post with Error(SafeHttp.UnsafeUrl _) -> true | _ -> false)
    }

[<Fact>]
let ``FR-020 a signed GET carries Accept and User-Agent, and its signature verifies on the server`` () : Task =
    let seen = ref (ResizeArray())

    let handler (ctx: HttpContext) results =
        task {
            seen.Value <- results
            checkSignature ctx results [||]
            ctx.Response.ContentType <- "application/activity+json"
            do! ctx.Response.WriteAsync $"{ctx.Request.Headers.Accept}|{ctx.Request.Headers.UserAgent}|{ctx.Request.Headers.Host}"
        }

    task {
        do!
            withServer handler (fun baseUrl ->
                task {
                    let! result = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/ap/actors/u1?x=1")
                    let response = result |> Result.defaultWith (fun e -> failwith $"{e}")
                    Assert.Equal(200, response.Status)
                    Assert.Equal(Some "application/activity+json", response.ContentType)

                    let body = asString response
                    Assert.Contains("application/activity+json, application/ld+json; profile=\"https://www.w3.org/ns/activitystreams\"", body)
                    Assert.Contains("Megafono/0.1.0 (+https://megafono.test/)", body)
                    Assert.Contains("127.0.0.1:", body)
                })

        Assert.Equal<Result<unit, VerifyError> list>([ Ok() ], List.ofSeq seen.Value)
    }

[<Fact>]
let ``FR-020 a signed POST sends the exact body with Digest and activity+json content type`` () : Task =
    let seenBody = ref [||]
    let seenType = ref ""
    let seen = ref (ResizeArray())

    let handler (ctx: HttpContext) results =
        task {
            let! body = readBody ctx
            seenBody.Value <- body
            seenType.Value <- ctx.Request.ContentType
            seen.Value <- results
            checkSignature ctx results body
            ctx.Response.StatusCode <- 202
        }

    task {
        let payload = Encoding.UTF8.GetBytes """{"type":"Create"}"""

        do!
            withServer handler (fun baseUrl ->
                task {
                    let! result = SafeHttp.signedPost (client true) keyId key.Value (Uri $"{baseUrl}/inbox") payload
                    Assert.Equal(202, (result |> Result.defaultWith (fun e -> failwith $"{e}")).Status)
                })

        Assert.Equal<byte[]>(payload, seenBody.Value)
        Assert.Equal("application/activity+json", seenType.Value)
        Assert.Equal<Result<unit, VerifyError> list>([ Ok() ], List.ofSeq seen.Value)
    }

[<Fact>]
let ``R-13 redirects are followed three times, each hop signed for itself`` () : Task =
    let seen = ref (ResizeArray())

    let handler (ctx: HttpContext) results =
        task {
            seen.Value <- results
            checkSignature ctx results [||]
            let n = int (ctx.Request.Path.Value.Substring "/r/".Length)

            if n > 0 then
                ctx.Response.StatusCode <- 302
                ctx.Response.Headers.Location <- $"/r/{n - 1}" // relative on purpose
            else
                do! ctx.Response.WriteAsync "end"
        }

    task {
        do!
            withServer handler (fun baseUrl ->
                task {
                    let! three = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/r/3")
                    let response = three |> Result.defaultWith (fun e -> failwith $"{e}")
                    Assert.Equal("end", asString response)
                    Assert.EndsWith("/r/0", response.Uri.AbsolutePath)

                    let! four = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/r/4")
                    Assert.Equal(Error SafeHttp.TooManyRedirects, four |> Result.map ignore)
                })

        // 4 hops for /r/3, 4 for /r/4 before giving up: every one verified, so every one was re-signed
        Assert.Equal(8, seen.Value.Count)
        Assert.All(seen.Value, fun r -> Assert.Equal(Ok(), r))
    }

[<Fact>]
let ``R-13 a POST that is redirected is returned as it is, never followed`` () : Task =
    let handler (ctx: HttpContext) _ =
        task {
            ctx.Response.StatusCode <- 307
            ctx.Response.Headers.Location <- "/elsewhere"
        }

    withServer handler (fun baseUrl ->
        task {
            let! result = SafeHttp.signedPost (client true) keyId key.Value (Uri $"{baseUrl}/inbox") [| 1uy |]
            Assert.Equal(307, (result |> Result.defaultWith (fun e -> failwith $"{e}")).Status)
        })

[<Fact>]
let ``R-13 responses are capped at 1 MB`` () : Task =
    let handler (ctx: HttpContext) _ =
        task {
            let size = if ctx.Request.Path.Value = "/exact" then SafeHttp.maxResponseBytes else SafeHttp.maxResponseBytes + 1
            // No Content-Length: chunked, so the cap has to be enforced while reading
            do! ctx.Response.Body.WriteAsync(Array.zeroCreate<byte> size, 0, size)
        }

    withServer handler (fun baseUrl ->
        task {
            let! exact = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/exact")
            Assert.Equal(SafeHttp.maxResponseBytes, (exact |> Result.defaultWith (fun e -> failwith $"{e}")).Body.Length)

            let! over = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/over")
            Assert.Equal(Error SafeHttp.ResponseTooLarge, over |> Result.map ignore)
        })

[<Fact>]
let ``R-13 non-2xx answers come back as responses`` () : Task =
    let handler (ctx: HttpContext) _ = task { ctx.Response.StatusCode <- 404 }

    withServer handler (fun baseUrl ->
        task {
            let! result = SafeHttp.signedGet (client true) keyId key.Value (Uri $"{baseUrl}/x")
            Assert.Equal(404, (result |> Result.defaultWith (fun e -> failwith $"{e}")).Status)
        })

// ---- the test-network seam ------------------------------------------------------------------------------

open System.Security.Cryptography.X509Certificates
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.AspNetCore.Server.Kestrel.Https
open Microsoft.Extensions.Logging

/// A self-signed certificate for `name`, usable by Kestrel and as the only trusted root
let private certificateFor (name: string) : X509Certificate2 =
    use rsa = RSA.Create 2048
    let request = CertificateRequest($"CN={name}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
    let san = SubjectAlternativeNameBuilder()
    san.AddDnsName name
    request.CertificateExtensions.Add(san.Build())
    use cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays -1.0, DateTimeOffset.UtcNow.AddDays 2.0)
    X509CertificateLoader.LoadPkcs12(cert.Export X509ContentType.Pfx, null)

let private withTlsServer (cert: X509Certificate2) (test: int -> Task) : Task =
    task {
        let builder = WebApplication.CreateBuilder()
        builder.Logging.ClearProviders() |> ignore
        builder.WebHost.ConfigureKestrel(fun (o: KestrelServerOptions) -> o.Listen(IPAddress.Loopback, 0, fun lo -> lo.UseHttps cert |> ignore)) |> ignore
        let app = builder.Build()
        app.Run(RequestDelegate(fun ctx -> ctx.Response.WriteAsync "secure"))
        do! app.StartAsync()

        try
            let address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head
            do! test (Uri address).Port
        finally
            app.StopAsync().GetAwaiter().GetResult()
    }

let private networkTo (port: int) (domain: string) (root: X509Certificate2) : SafeHttp.TestNetwork =
    { Redirect = (fun host -> if host = domain then Some("127.0.0.1", port) else None)
      TrustedRoot = root }

let private testClient (network: SafeHttp.TestNetwork) =
    SafeHttp.create (SafeHttp.createHandlerWith true (Some network)) true "Megafono/test" TimeProvider.System

[<Fact>]
let ``test seam: a fake domain reaches the redirected port and TLS validates against the test CA`` () : Task =
    let cert = certificateFor "fake.test"

    withTlsServer cert (fun port ->
        task {
            let client = testClient (networkTo port "fake.test" cert)
            let! result = SafeHttp.signedGet client keyId key.Value (Uri "https://fake.test/hello")
            let response = result |> Result.defaultWith (fun e -> failwith $"{e}")
            Assert.Equal("secure", asString response)
        })

[<Fact>]
let ``test seam: another CA is not trusted, and a certificate for another name is refused`` () : Task =
    let cert = certificateFor "fake.test"
    let stranger = certificateFor "fake.test"

    withTlsServer cert (fun port ->
        task {
            let! wrongCa = SafeHttp.signedGet (testClient (networkTo port "fake.test" stranger)) keyId key.Value (Uri "https://fake.test/")
            Assert.True wrongCa.IsError

            // The server's certificate says fake.test; asking for other.test (same redirect) must fail the name check
            let network = { networkTo port "other.test" cert with Redirect = (fun _ -> Some("127.0.0.1", port)) }
            let! wrongName = SafeHttp.signedGet (testClient network) keyId key.Value (Uri "https://other.test/")
            Assert.True wrongName.IsError
        })

[<Fact>]
let ``test seam: without an override the fake domain is not reachable`` () : Task =
    task {
        let client = SafeHttp.create (SafeHttp.createHandlerWith true None) true "Megafono/test" TimeProvider.System
        let! result = SafeHttp.signedGet client keyId key.Value (Uri "https://fake.test/")
        Assert.True result.IsError
    }

[<Fact>]
let ``test seam: it needs the private-address switch, which Production refuses`` () =
    let network = networkTo 1 "fake.test" (certificateFor "fake.test")
    Assert.Throws<ArgumentException>(fun () -> SafeHttp.createHandlerWith false (Some network) |> ignore) |> ignore

    // And through the app: a registered override with the switch off fails as soon as the client is built
    let configure (services: IServiceCollection) = services.AddSingleton<SafeHttp.TestNetwork>(network) |> ignore
    use app = Megafono.Program.buildAppWith configure [| "--environment"; "Development" |] Megafono.Tests.TestSupport.validOverrides
    Assert.Throws<ArgumentException>(fun () -> app.Services.GetRequiredService<SafeHttp.SafeHttpClient>() |> ignore) |> ignore
