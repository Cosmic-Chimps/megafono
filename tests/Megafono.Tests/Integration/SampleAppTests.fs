/// The sample app's side of the app contract (T074, FR-005, FR-064): lookup, webhook signatures, profile page
module Megafono.Tests.Integration.SampleAppTests

open System
open System.Net
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.Extensions.DependencyInjection
open Xunit

let private token = "app-token"
let private secret = "webhook-secret"
let private testId = "3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10"

let private withSample (test: HttpClient -> Task) : Task =
    task {
        let app =
            SampleApp.Program.buildSample
                [||]
                [ "urls", "http://127.0.0.1:0"
                  "Megafono:Url", "http://127.0.0.1:1"
                  "Megafono:Token", token
                  "Megafono:WebhookSecret", secret
                  "App:Domain", "app.test" ]

        do! app.StartAsync()
        let address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses |> Seq.head
        use client = new HttpClient(BaseAddress = Uri address)

        try
            do! test client
        finally
            app.DisposeAsync().AsTask().Wait()
    }
    :> Task

let private get (client: HttpClient) (path: string) (bearer: string option) =
    task {
        use request = new HttpRequestMessage(HttpMethod.Get, path)
        bearer |> Option.iter (fun b -> request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {b}") |> ignore)
        return! client.SendAsync request
    }

[<Fact>]
let ``FR-060 lookup answers by handle and by id with the bearer token`` () =
    withSample (fun client ->
        task {
            for path in [ "/megafono/actors?handle=test"; "/megafono/actors?handle=TEST"; $"/megafono/actors/{testId}" ] do
                let! response = get client path (Some token)
                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                let! text = response.Content.ReadAsStringAsync()
                use json = JsonDocument.Parse text
                let root = json.RootElement
                Assert.Equal(testId, root.GetProperty("id").GetString())
                Assert.Equal("test", root.GetProperty("handle").GetString())
                Assert.Equal("https://app.test/u/test", root.GetProperty("profileUrl").GetString())
                Assert.False(root.GetProperty("locked").GetBoolean())
        })

[<Fact>]
let ``FR-060 lookup rejects a missing or wrong bearer and answers 404 for an unknown user`` () =
    withSample (fun client ->
        task {
            for bearer in [ None; Some "wrong"; Some "" ] do
                let! response = get client "/megafono/actors?handle=test" bearer
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
                let! response = get client $"/megafono/actors/{testId}" bearer
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)

            for path in [ "/megafono/actors?handle=nobody"; "/megafono/actors/unknown-id"; "/megafono/actors" ] do
                let! response = get client path (Some token)
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        })

let private signed (client: HttpClient) (at: DateTimeOffset) (secret: string) (body: string) (sentBody: string) =
    task {
        let t = at.ToUnixTimeSeconds()
        let mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, Encoding.UTF8.GetBytes $"{t}.{body}")
        use request = new HttpRequestMessage(HttpMethod.Post, "/megafono/webhooks")
        request.Headers.TryAddWithoutValidation("Megafono-Signature", $"t={t},v1={Convert.ToHexStringLower mac}") |> ignore
        request.Content <- new StringContent(sentBody, Encoding.UTF8, "application/json")
        return! client.SendAsync request
    }

let private event = """{"id":"evt_1","version":1,"type":"follow.requested","seq":1}"""

[<Fact>]
let ``FR-064 a webhook with a valid signature is accepted, and a repeat too`` () =
    withSample (fun client ->
        task {
            for _ in 1..2 do
                let! response = signed client DateTimeOffset.UtcNow secret event event
                Assert.True response.IsSuccessStatusCode
        })

[<Fact>]
let ``FR-064 a webhook with a bad, tampered or old signature is rejected`` () =
    withSample (fun client ->
        task {
            let now = DateTimeOffset.UtcNow
            let! wrongSecret = signed client now "other-secret" event event
            Assert.Equal(HttpStatusCode.Unauthorized, wrongSecret.StatusCode)
            let! tampered = signed client now secret event (event.Replace("evt_1", "evt_2"))
            Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode)
            let! old = signed client (now.AddMinutes -6.0) secret event event
            Assert.Equal(HttpStatusCode.Unauthorized, old.StatusCode)
            let! fresh = signed client (now.AddMinutes -4.0) secret event event
            Assert.True fresh.IsSuccessStatusCode

            use unsigned = new HttpRequestMessage(HttpMethod.Post, "/megafono/webhooks", Content = new StringContent(event))
            let! response = client.SendAsync unsigned
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode)
        })

[<Fact>]
let ``FR-005 the profile page links to the actor, in the page and in the Link header`` () =
    withSample (fun client ->
        task {
            let! response = client.GetAsync "/u/test"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let actor = $"https://app.test/ap/actors/{testId}"
            let! html = response.Content.ReadAsStringAsync()
            Assert.Contains($"<link rel=\"alternate\" type=\"application/activity+json\" href=\"{actor}\">", html)
            let link = response.Headers.GetValues "Link" |> Seq.exactlyOne
            Assert.Equal($"<{actor}>; rel=\"alternate\"; type=\"application/activity+json\"", link)

            let! missing = client.GetAsync "/u/nobody"
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode)
        })
