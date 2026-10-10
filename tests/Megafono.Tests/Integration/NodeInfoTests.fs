module Megafono.Tests.Integration.NodeInfoTests

open System.Net
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private start (pg: PostgresFixture) =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser(FakeApp.user "u-1" "alex")
        app.AddUser(FakeApp.user "u-2" "sam")

        let! host =
            MegafonoHost.start
                { MegafonoHost.settings cs app.Url (FakeTime()) with
                    Extra = [ "Megafono:Software:Name", "my-app"; "Megafono:Software:Version", "2.3.4" ] }

        return app, host
    }

type NodeInfoTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-002 discovery links both schema versions``() : Task =
        task {
            let! _, host = start pg
            use _ = MegafonoHost.disposer host
            let! response = host.FederationClient().GetAsync "/.well-known/nodeinfo"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal("application/json", response.Content.Headers.ContentType.MediaType)
            let! text = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse text

            let links = [ for l in json.RootElement.GetProperty("links").EnumerateArray() -> l.GetProperty("rel").GetString(), l.GetProperty("href").GetString() ]

            Assert.Equal<(string * string) list>(
                [ "http://nodeinfo.diaspora.software/ns/schema/2.0", "https://megafono.test/nodeinfo/2.0"
                  "http://nodeinfo.diaspora.software/ns/schema/2.1", "https://megafono.test/nodeinfo/2.1" ],
                links
            )
        }

    [<Theory>]
    [<InlineData("2.0")>]
    [<InlineData("2.1")>]
    member _.``FR-002 both documents carry the software name and version and the required keys``(version: string) : Task =
        task {
            let! app, host = start pg
            use _ = MegafonoHost.disposer host
            let client = host.FederationClient()
            let! response = client.GetAsync $"/nodeinfo/{version}"
            Assert.Equal(HttpStatusCode.OK, response.StatusCode)
            Assert.Equal($"application/json; profile=\"http://nodeinfo.diaspora.software/ns/schema/{version}#\"", string response.Content.Headers.ContentType)
            let! text = response.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse text
            let root = json.RootElement

            Assert.Equal(version, root.GetProperty("version").GetString())
            Assert.Equal("my-app", root.GetProperty("software").GetProperty("name").GetString())
            Assert.Equal("2.3.4", root.GetProperty("software").GetProperty("version").GetString())
            Assert.Equal("activitypub", (root.GetProperty("protocols")[0]).GetString())
            Assert.Equal(0, root.GetProperty("services").GetProperty("inbound").GetArrayLength())
            Assert.Equal(0, root.GetProperty("services").GetProperty("outbound").GetArrayLength())
            Assert.False(root.GetProperty("openRegistrations").GetBoolean())
            Assert.Equal(0L, root.GetProperty("usage").GetProperty("users").GetProperty("total").GetInt64())
            Assert.Equal(JsonValueKind.Object, root.GetProperty("metadata").ValueKind)

            // The user count is the number of live local actors
            let! _ = client.GetAsync "/ap/actors/u-1"
            let! _ = client.GetAsync "/ap/actors/u-2"
            let! again = client.GetStringAsync $"/nodeinfo/{version}"
            use json2 = JsonDocument.Parse again
            Assert.Equal(2L, json2.RootElement.GetProperty("usage").GetProperty("users").GetProperty("total").GetInt64())
            ignore app
        }
