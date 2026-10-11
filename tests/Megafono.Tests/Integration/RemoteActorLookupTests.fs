module Megafono.Tests.Integration.RemoteActorLookupTests

open System
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private start (pg: PostgresFixture) (extra: (string * string) list) =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        let! stub = StubRemote.start [ "alice" ]
        let! host = MegafonoHost.start { MegafonoHost.settings cs app.Url (FakeTime()) with Extra = extra }
        return stub, host
    }

let private query (name: string) (value: string) = $"?{name}=" + Uri.EscapeDataString value

let private lookup (host: MegafonoHost.MegafonoHost) (query: string) =
    task {
        let! response = host.InternalClient(Some MegafonoHost.token).GetAsync("/internal/remote-actors" + query)
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

type RemoteActorLookupTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-033 a handle and a URL resolve to the same sanitized actor``() : Task =
        task {
            let! stub, host = start pg []
            use _ = MegafonoHost.disposer host
            stub.Mutate <- Some(fun _ doc -> doc["name"] <- JsonValue.Create "<b>Alice</b> &amp; <script>x()</script>co"
                                             doc["icon"] <- (let i = JsonObject() in i["type"] <- JsonValue.Create "Image"; i["url"] <- JsonValue.Create $"{stub.BaseUrl}/alice.png"; i :> JsonNode))

            let! byHandle, handleBody = lookup host (query "handle" ($"@alice@{stub.Authority}"))
            let! byUrl, urlBody = lookup host (query "url" (stub.ActorId "alice"))
            Assert.Equal(HttpStatusCode.OK, byHandle)
            Assert.Equal(HttpStatusCode.OK, byUrl)

            for body in [ handleBody; urlBody ] do
                use json = JsonDocument.Parse body
                let a = json.RootElement
                Assert.Equal(stub.ActorId "alice", a.GetProperty("id").GetString())
                Assert.Equal($"@alice@{stub.Authority}", a.GetProperty("handle").GetString())
                // FR-056: text, never markup
                Assert.Equal("Alice & co", a.GetProperty("name").GetString())
                Assert.Equal($"{stub.BaseUrl}/alice.png", a.GetProperty("iconUrl").GetString())
                Assert.Equal($"{stub.BaseUrl}/@alice", a.GetProperty("profileUrl").GetString())
                Assert.Equal("Person", a.GetProperty("type").GetString())
        }

    [<Fact>]
    member _.``FR-033 the handle form works without the leading at-sign too``() : Task =
        task {
            let! stub, host = start pg []
            use _ = MegafonoHost.disposer host
            let! status, _ = lookup host (query "handle" ($"alice@{stub.Authority}"))
            Assert.Equal(HttpStatusCode.OK, status)
        }

    [<Fact>]
    member _.``FR-033 an unknown handle or URL is 404``() : Task =
        task {
            let! stub, host = start pg []
            use _ = MegafonoHost.disposer host
            let! unknownHandle, body = lookup host (query "handle" ($"@nobody@{stub.Authority}"))
            let! unknownUrl, _ = lookup host (query "url" ($"{stub.BaseUrl}/users/nobody"))
            let! nonsense, _ = lookup host "?handle=not-a-handle"
            Assert.Equal(HttpStatusCode.NotFound, unknownHandle)
            Assert.Contains("not_found", body)
            Assert.Equal(HttpStatusCode.NotFound, unknownUrl)
            Assert.Equal(HttpStatusCode.NotFound, nonsense)
        }

    [<Fact>]
    member _.``FR-021 a target on a private address is 400``() : Task =
        task {
            // The test switch is off: loopback and plain http are refused like any private target
            let! stub, host = start pg [ "Megafono:Federation:AllowPrivateAddresses", "false" ]
            use _ = MegafonoHost.disposer host
            let! byHandle, handleBody = lookup host (query "handle" ($"@alice@{stub.Authority}"))
            let! byUrl, _ = lookup host (query "url" (stub.ActorId "alice"))
            let! loopbackUrl, _ = lookup host (query "url" ("https://127.0.0.1:9/users/alice"))
            let! mapped, _ = lookup host (query "url" ("https://[::ffff:10.0.0.1]/users/alice"))
            Assert.Equal(HttpStatusCode.BadRequest, byHandle)
            Assert.Contains("invalid_request", handleBody)
            Assert.Equal(HttpStatusCode.BadRequest, byUrl)
            Assert.Equal(HttpStatusCode.BadRequest, loopbackUrl)
            Assert.Equal(HttpStatusCode.BadRequest, mapped)
            Assert.Empty stub.Gets // nothing was fetched
        }

    [<Fact>]
    member _.``contract exactly one of handle or url is required, and the bearer too``() : Task =
        task {
            let! _, host = start pg []
            use _ = MegafonoHost.disposer host
            let! neither, _ = lookup host ""
            let! both, _ = lookup host "?handle=a@b.c&url=https://b.c/a"
            Assert.Equal(HttpStatusCode.BadRequest, neither)
            Assert.Equal(HttpStatusCode.BadRequest, both)
            let! anonymous = host.InternalClient(None).GetAsync "/internal/remote-actors?handle=a@b.c"
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode)
        }
