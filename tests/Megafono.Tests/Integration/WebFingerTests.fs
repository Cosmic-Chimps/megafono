module Megafono.Tests.Integration.WebFingerTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private resource (r: string) = $"/.well-known/webfinger?resource={Uri.EscapeDataString r}"

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-1" "alex" with ProfileUrl = "https://app.example/u/alex" }
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        return { App = app; Host = host }
    }

type WebFingerTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-001 a known handle answers a JRD with subject, both aliases, self and profile-page links``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! response = w.Host.FederationClient().GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("application/jrd+json", response.Content.Headers.ContentType.MediaType)
            Assert.Equal("*", Seq.head (response.Headers.GetValues "Access-Control-Allow-Origin"))

            let! text = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse text
            let root = json.RootElement
            Assert.Equal("acct:alex@megafono.test", root.GetProperty("subject").GetString())

            let aliases = [ for a in root.GetProperty("aliases").EnumerateArray() -> a.GetString() ]
            Assert.Equal<string list>([ "https://app.example/u/alex"; "https://megafono.test/ap/actors/u-1" ], aliases)

            let links = [ for l in root.GetProperty("links").EnumerateArray() -> l ]
            let find (rel: string) = links |> List.find (fun l -> l.GetProperty("rel").GetString() = rel)
            let self = find "self"
            Assert.Equal("application/activity+json", self.GetProperty("type").GetString())
            Assert.Equal("https://megafono.test/ap/actors/u-1", self.GetProperty("href").GetString())
            let page = find "http://webfinger.net/rel/profile-page"
            Assert.Equal("text/html", page.GetProperty("type").GetString())
            Assert.Equal("https://app.example/u/alex", page.GetProperty("href").GetString())
        }

    [<Fact>]
    member _.``FR-001 an unknown handle, another domain and a non-acct resource are 404, a missing resource 400``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()

            for r in [ "acct:nobody@megafono.test"; "acct:alex@other.example"; "https://megafono.test/ap/actors/u-1"; "alex@megafono.test"; "acct:@megafono.test"; "acct:alex@"; "acct:a b@megafono.test"; "acct:alex@megafono.test@x" ] do
                let! response = client.GetAsync(resource r)
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)

            let! missing = client.GetAsync "/.well-known/webfinger"
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode)
        }

    [<Fact>]
    member _.``FR-001 handle and domain are case-insensitive``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! response = w.Host.FederationClient().GetAsync(resource "ACCT:ALEX@Megafono.TEST")
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            let! text = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse text
            Assert.Equal("acct:alex@megafono.test", json.RootElement.GetProperty("subject").GetString())
        }

    [<Fact>]
    member _.``routing does not depend on the Accept header``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            client.DefaultRequestHeaders.Accept.ParseAdd "text/html"
            let! response = client.GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
        }

    [<Fact>]
    member _.``edge case: with the app down the stored profile answers for 24 hours, then 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            let! first = client.GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.OK, first.StatusCode)

            w.App.LookupMode <- FakeApp.Fail500
            w.Host.Time.Advance(TimeSpan.FromHours 1.0)
            let! within = client.GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.OK, within.StatusCode)

            w.Host.Time.Advance(TimeSpan.FromHours 24.0)
            let! late = client.GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.NotFound, late.StatusCode)
        }

    [<Fact>]
    member _.``a handle the app never confirmed is 404 while the app is down``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            w.App.LookupMode <- FakeApp.Fail500
            let! response = w.Host.FederationClient().GetAsync(resource "acct:alex@megafono.test")
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        }
