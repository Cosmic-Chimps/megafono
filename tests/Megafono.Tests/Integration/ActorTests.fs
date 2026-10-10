module Megafono.Tests.Integration.ActorTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type World =
    { App: FakeApp.FakeApp
      Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret

        app.AddUser
            { FakeApp.user "u-1" "alex" with
                Locked = true
                Summary = Some "<p>Anime &amp; coffee</p>"
                IconUrl = Some "https://app.example/img/alex.png"
                ProfileUrl = "https://app.example/u/alex" }

        app.AddUser { FakeApp.user "u-2" "bot" with Type = "Service" }
        app.AddUser { FakeApp.user "u-3" "club" with Type = "Group"; Summary = None }
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        return { App = app; Host = host; Db = NpgsqlDataSource.Create cs }
    }

let private getJson (client: Net.Http.HttpClient) (path: string) =
    task {
        let! response = client.GetAsync path
        let! text = response.Content.ReadAsStringAsync()
        return response, text
    }

let private prop (e: JsonElement) (name: string) = e.GetProperty(name)

type ActorTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-003 FR-004 the actor document has the fields of R-02 and its id is the URL``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! response, text = getJson (w.Host.FederationClient()) "/ap/actors/u-1"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("application/activity+json", response.Content.Headers.ContentType.MediaType)
            use json = JsonDocument.Parse text
            let a = json.RootElement
            let id = "https://megafono.test/ap/actors/u-1"

            // @context: activitystreams, security, and the Mastodon terms
            let context = prop a "@context"
            Assert.Equal("https://www.w3.org/ns/activitystreams", context[0].GetString())
            Assert.Equal("https://w3id.org/security/v1", context[1].GetString())
            Assert.Equal("toot:discoverable", (prop context[2] "discoverable").GetString())
            Assert.Equal("as:manuallyApprovesFollowers", (prop context[2] "manuallyApprovesFollowers").GetString())

            Assert.Equal(id, (prop a "id").GetString())
            Assert.Equal("Person", (prop a "type").GetString())
            Assert.Equal("alex", (prop a "preferredUsername").GetString())
            Assert.Equal("Name of alex", (prop a "name").GetString())
            Assert.Equal("<p>Anime &amp; coffee</p>", (prop a "summary").GetString())
            // FR-005: `url` is the app's profile page
            Assert.Equal("https://app.example/u/alex", (prop a "url").GetString())
            Assert.Equal($"{id}/inbox", (prop a "inbox").GetString())
            Assert.Equal($"{id}/outbox", (prop a "outbox").GetString())
            Assert.Equal($"{id}/followers", (prop a "followers").GetString())
            Assert.Equal($"{id}/following", (prop a "following").GetString())
            Assert.Equal("https://megafono.test/inbox", (prop (prop a "endpoints") "sharedInbox").GetString())
            Assert.True((prop a "manuallyApprovesFollowers").GetBoolean())
            Assert.True((prop a "discoverable").GetBoolean())
            Assert.False((prop a "indexable").GetBoolean())
            Assert.Equal("acct:alex@megafono.test", (prop a "webfinger").GetString())
            Assert.Equal("Image", (prop (prop a "icon") "type").GetString())
            Assert.Equal("https://app.example/img/alex.png", (prop (prop a "icon") "url").GetString())
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", (prop a "published").GetString())

            // FR-006: one key, owned by the actor
            let key = prop a "publicKey"
            Assert.Equal($"{id}#main-key", (prop key "id").GetString())
            Assert.Equal(id, (prop key "owner").GetString())
            Assert.StartsWith("-----BEGIN PUBLIC KEY-----", (prop key "publicKeyPem").GetString())
        }

    [<Fact>]
    member _.``FR-003 manuallyApprovesFollowers follows locked, and Service and Group types are kept``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            let! _, bot = getJson client "/ap/actors/u-2"
            let! _, club = getJson client "/ap/actors/u-3"
            use botJson = JsonDocument.Parse bot
            use clubJson = JsonDocument.Parse club
            Assert.Equal("Service", (prop botJson.RootElement "type").GetString())
            Assert.False((prop botJson.RootElement "manuallyApprovesFollowers").GetBoolean())
            Assert.Equal("Group", (prop clubJson.RootElement "type").GetString())
            // No summary and no icon from the app: neither key is written
            Assert.False(clubJson.RootElement.TryGetProperty("summary") |> fst)
            Assert.False(clubJson.RootElement.TryGetProperty("icon") |> fst)
        }

    [<Fact>]
    member _.``FR-006 the key is created on first lookup and reused``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            let pemOf (text: string) =
                use json = JsonDocument.Parse text
                (prop (prop json.RootElement "publicKey") "publicKeyPem").GetString()

            let! _, first = getJson client "/ap/actors/u-1"
            // A rename in the app (after the 5 minute cache) does not touch the key
            w.App.AddUser { FakeApp.user "u-1" "alex" with Name = "Alex Renamed"; ProfileUrl = "https://app.example/u/alex" }
            w.Host.Time.Advance(TimeSpan.FromMinutes 10.0)
            let! _, second = getJson client "/ap/actors/u-1"
            Assert.Contains("Alex Renamed", second)
            Assert.Equal(pemOf first, pemOf second)

            let! rows = Db.count w.Db "SELECT count(*) FROM local_actors"
            let! stored = Db.scalar w.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            Assert.Equal(1L, rows)
            Assert.Equal(string stored, pemOf first)
        }

    [<Fact>]
    member _.``FR-006 no response ever contains private key material``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()

            let paths =
                [ "/ap/actors/u-1"; "/ap/actors/u-1/outbox"; "/ap/actors/u-1/followers"; "/ap/actors/u-1/following"; "/ap/actor"
                  "/.well-known/webfinger?resource=acct:alex@megafono.test"; "/.well-known/nodeinfo"; "/nodeinfo/2.1" ]

            let! bodies = Task.WhenAll [| for p in paths -> task { let! _, t = getJson client p in return t } |]

            // The decrypted private key, as DER in base64, and any PEM private key header
            let keys = Seed.keys
            let! blob = Db.scalar w.Db "SELECT enc_private_key FROM local_actors WHERE app_user_id = 'u-1'"
            use priv = KeyVault.loadPrivateKey keys "https://megafono.test/ap/actors/u-1" (blob :?> byte[])
            let der = Convert.ToBase64String(priv.ExportPkcs8PrivateKey())
            let fragment = der.Substring(40, 60)

            for body in bodies do
                Assert.DoesNotContain("PRIVATE", body)
                Assert.DoesNotContain(fragment, body)
                Assert.DoesNotContain("enc_private_key", body)
        }

    [<Fact>]
    member _.``FR-003 the collections are OrderedCollection stubs with totalItems and no items``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            let! _, _ = getJson client "/ap/actors/u-1" // creates the actor row
            do! Seed.remoteFollower w.Db "u-1" "https://r.example/users/a" "https://r.example/users/a/inbox" None "accepted"
            do! Seed.remoteFollower w.Db "u-1" "https://r.example/users/b" "https://r.example/users/b/inbox" None "pending"

            for name, total in [ "followers", 1L; "following", 0L; "outbox", 0L ] do
                let! response, text = getJson client $"/ap/actors/u-1/{name}"
                Assert.Equal(HttpStatusCode.OK, response.StatusCode)
                Assert.Equal("application/activity+json", response.Content.Headers.ContentType.MediaType)
                use json = JsonDocument.Parse text
                let c = json.RootElement
                Assert.Equal($"https://megafono.test/ap/actors/u-1/{name}", (prop c "id").GetString())
                Assert.Equal("OrderedCollection", (prop c "type").GetString())
                Assert.Equal(total, (prop c "totalItems").GetInt64())
                Assert.False(c.TryGetProperty("orderedItems") |> fst)
                Assert.False(c.TryGetProperty("first") |> fst)
        }

    [<Fact>]
    member _.``FR-022 the instance actor is an Application at /ap/actor with its own key``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! response, text = getJson (w.Host.FederationClient()) "/ap/actor"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("application/activity+json", response.Content.Headers.ContentType.MediaType)
            use json = JsonDocument.Parse text
            let a = json.RootElement
            Assert.Equal("https://megafono.test/ap/actor", (prop a "id").GetString())
            Assert.Equal("Application", (prop a "type").GetString())
            Assert.Equal("megafono.test", (prop a "preferredUsername").GetString())
            Assert.StartsWith("https://megafono.test/ap/actor/inbox", (prop a "inbox").GetString())
            let key = prop a "publicKey"
            Assert.Equal("https://megafono.test/ap/actor#main-key", (prop key "id").GetString())
            Assert.Equal("https://megafono.test/ap/actor", (prop key "owner").GetString())

            let! stored = Db.scalar w.Db "SELECT public_key_pem FROM instance_actor"
            Assert.Equal(string stored, (prop key "publicKeyPem").GetString())
        }

    [<Fact>]
    member _.``an unknown id, a malformed id and a non-canonical path are 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()

            for path in
                [ "/ap/actors/nobody"
                  "/ap/actors/bad%20id"
                  "/ap/actors/u-1/" // a trailing slash would make the id differ from the URL
                  "/AP/actors/u-1"
                  "/ap/Actors/u-1"
                  "/ap/actors/U-1" // ids are case-sensitive: another user than u-1
                  "/ap/actors/nobody/followers"
                  "/ap/actors/nobody/outbox"
                  "/ap/actors/u-1/followers/"
                  "/ap/actor/" ] do
                let! response = client.GetAsync path
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode)
        }

    [<Fact>]
    member _.``with the app down the stored actor is served for 24 hours, then 404``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let client = w.Host.FederationClient()
            let! first = client.GetAsync "/ap/actors/u-1"
            Assert.Equal(HttpStatusCode.OK, first.StatusCode)
            w.App.LookupMode <- FakeApp.Fail500
            w.Host.Time.Advance(TimeSpan.FromHours 2.0)
            let! within = client.GetAsync "/ap/actors/u-1"
            Assert.Equal(HttpStatusCode.OK, within.StatusCode)
            w.Host.Time.Advance(TimeSpan.FromHours 23.0)
            let! late = client.GetAsync "/ap/actors/u-1"
            Assert.Equal(HttpStatusCode.NotFound, late.StatusCode)
        }
