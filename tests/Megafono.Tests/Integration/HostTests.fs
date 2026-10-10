module Megafono.Tests.Integration.HostTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private newHost (pg: PostgresFixture) =
    task {
        let! connectionString = pg.CreateDatabase()
        let! host = MegafonoHost.start (MegafonoHost.settings connectionString "http://127.0.0.1:9" (FakeTime()))
        return host, connectionString
    }

let private status (client: Net.Http.HttpClient) (path: string) =
    task {
        let! response = client.GetAsync path
        return response.StatusCode
    }

type HostTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-062 startup refuses a bad master key``() : Task =
        task {
            let! connectionString = pg.CreateDatabase()

            let bad = { MegafonoHost.settings connectionString "http://127.0.0.1:9" (FakeTime()) with Extra = [ "Megafono:MasterKey", "not-a-key" ] }
            let error = Assert.Throws<InvalidOperationException>(fun () -> MegafonoHost.build bad |> ignore)
            Assert.Contains("MasterKey", error.Message)
        }

    [<Fact>]
    member _.``R-13 startup refuses the private-address switch in Production``() : Task =
        task {
            let! connectionString = pg.CreateDatabase()
            let prod = { MegafonoHost.settings connectionString "http://127.0.0.1:9" (FakeTime()) with Environment = "Production" }
            let error = Assert.Throws<InvalidOperationException>(fun () -> MegafonoHost.build prod |> ignore)
            Assert.Contains("AllowPrivateAddresses", error.Message)
        }

    [<Fact>]
    member _.``FR-061 /internal/* answers 404 on the federation port and 401 without the bearer on the internal port``() : Task =
        task {
            let! host, _ = newHost pg
            use _ = MegafonoHost.disposer host

            // Federation port: the internal API does not exist, with or without the token, in any letter case
            let fed = host.FederationClient()
            fed.DefaultRequestHeaders.Authorization <- Net.Http.Headers.AuthenticationHeaderValue("Bearer", MegafonoHost.token)

            for path in [ "/internal/actors/u1"; "/Internal/actors/u1"; "/INTERNAL"; "/internal/" ] do
                let! s = status fed path
                Assert.Equal(HttpStatusCode.NotFound, s)

            // Internal port: a token is required
            let! none = status (host.InternalClient None) "/internal/actors/u1"
            let! wrong = status (host.InternalClient(Some "wrong-token")) "/internal/actors/u1"
            let! empty = status (host.InternalClient(Some "")) "/internal/actors/u1"
            Assert.Equal(HttpStatusCode.Unauthorized, none)
            Assert.Equal(HttpStatusCode.Unauthorized, wrong)
            Assert.Equal(HttpStatusCode.Unauthorized, empty)

            let! body = (host.InternalClient None).GetStringAsync "/health" // unauthenticated
            Assert.Contains("\"ok\"", body)

            // The right token passes the guard (nothing is routed there yet, so the router answers 404, not 401)
            let! right = status (host.InternalClient(Some MegafonoHost.token)) "/internal/actors/u1"
            Assert.Equal(HttpStatusCode.NotFound, right)

            let! response = (host.InternalClient None).GetAsync "/internal/x"
            let! text = response.Content.ReadAsStringAsync()
            Assert.Contains("unauthorized", text)
        }

    [<Fact>]
    member _.``FR-061 only /internal/* and /health answer on the internal port``() : Task =
        task {
            let! host, _ = newHost pg
            use _ = MegafonoHost.disposer host
            let client = host.InternalClient(Some MegafonoHost.token)

            for path in [ "/"; "/ap/actor"; "/inbox"; "/.well-known/webfinger"; "/nodeinfo/2.1"; "/megafono/actors" ] do
                let! s = status client path
                Assert.Equal(HttpStatusCode.NotFound, s)

            for path in [ "/health"; "/HEALTH" ] do
                let! s = status client path
                Assert.Equal(HttpStatusCode.OK, s)
        }

    [<Fact>]
    member _.``FR-062 /health reports the database and answers 503 when it is gone``() : Task =
        task {
            let! host, connectionString = newHost pg
            use _ = MegafonoHost.disposer host
            let client = host.FederationClient()

            let! ok = client.GetAsync "/health"
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode)
            let! okText = ok.Content.ReadAsStringAsync()
            use json = JsonDocument.Parse okText
            Assert.Equal("ok", json.RootElement.GetProperty("status").GetString())
            Assert.Equal("ok", json.RootElement.GetProperty("database").GetString())

            do! pg.DropDatabase connectionString
            let! down = client.GetAsync "/health"
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode)
            let! downText = down.Content.ReadAsStringAsync()
            use downJson = JsonDocument.Parse downText
            Assert.Equal("unreachable", downJson.RootElement.GetProperty("database").GetString())
        }

    [<Fact>]
    member _.``FR-062 the schema bootstrap and the instance actor are idempotent across two starts``() : Task =
        task {
            let! connectionString = pg.CreateDatabase()
            let time = FakeTime()
            let settings = MegafonoHost.settings connectionString "http://127.0.0.1:9" time

            let keyOf (host: MegafonoHost.MegafonoHost) =
                task {
                    let! key = host.Services.GetRequiredService<InstanceKey.InstanceKeyProvider>().Get()
                    return key.PublicKeyPem, key.KeyId
                }

            let! first = MegafonoHost.start settings
            let! firstKey = keyOf first
            do! first.Stop()
            do! (first :> IAsyncDisposable).DisposeAsync()

            let! second = MegafonoHost.start settings
            let! secondKey = keyOf second
            use dataSource = NpgsqlDataSource.Create connectionString
            let! rows = Db.count dataSource "SELECT count(*) FROM instance_actor"
            let! tables = Db.count dataSource "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'"
            do! (second :> IAsyncDisposable).DisposeAsync()

            Assert.Equal(firstKey, secondKey)
            Assert.Equal("https://megafono.test/ap/actor#main-key", snd firstKey)
            Assert.Equal(1L, rows)
            Assert.Equal(10L, tables)
        }

    [<Fact>]
    member _.``FR-022 two hosts starting at once on a fresh database end up with one instance key``() : Task =
        task {
            let! connectionString = pg.CreateDatabase()
            let settings = MegafonoHost.settings connectionString "http://127.0.0.1:9" (FakeTime())

            let! hosts = Task.WhenAll [| for _ in 1..4 -> MegafonoHost.start settings |]

            let! keys =
                Task.WhenAll
                    [| for host in hosts -> task { let! k = host.Services.GetRequiredService<InstanceKey.InstanceKeyProvider>().Get() in return k.PublicKeyPem } |]

            use dataSource = NpgsqlDataSource.Create connectionString
            let! rows = Db.count dataSource "SELECT count(*) FROM instance_actor"
            let! stored = Db.scalar dataSource "SELECT public_key_pem FROM instance_actor"

            for host in hosts do
                do! (host :> IAsyncDisposable).DisposeAsync()

            Assert.Equal(1L, rows)
            Assert.All(keys, fun k -> Assert.Equal(string stored, k))
        }

    [<Fact>]
    member _.``FR-022 the instance key is stored encrypted and can sign``() : Task =
        task {
            let! host, connectionString = newHost pg
            use _ = MegafonoHost.disposer host
            use dataSource = NpgsqlDataSource.Create connectionString
            let! encrypted = Db.scalar dataSource "SELECT enc_private_key FROM instance_actor"
            let blob = encrypted :?> byte[]
            Assert.Equal(1uy, blob[0])

            let! key = host.Services.GetRequiredService<InstanceKey.InstanceKeyProvider>().Get()
            use rsa = KeyVault.importPublicPem key.PublicKeyPem
            let data = "hello"B
            let signature = key.PrivateKey.SignData(data, Security.Cryptography.HashAlgorithmName.SHA256, Security.Cryptography.RSASignaturePadding.Pkcs1)
            Assert.True(rsa.VerifyData(data, signature, Security.Cryptography.HashAlgorithmName.SHA256, Security.Cryptography.RSASignaturePadding.Pkcs1))
        }
