module Megafono.Tests.Integration.AppClientTests

open System
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open Xunit
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type Setup =
    { App: FakeApp.FakeApp
      DataSource: NpgsqlDataSource
      Time: FakeTime
      Client: AppClient }

let private setup (pg: PostgresFixture) (allowPrivate: bool) : Task<Setup> =
    task {
        let! connectionString = pg.CreateDatabase()
        let dataSource = NpgsqlDataSource.Create connectionString
        do! Database.initializeSchema dataSource
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        let time = FakeTime()

        let env: Env =
            { DataSource = dataSource
              Keys = Seed.keys
              Http = AppClient.createHttpClient ()
              BaseUrl = app.Url
              Token = MegafonoHost.token
              Domain = Seed.domain
              Time = time
              AllowPrivateAddresses = allowPrivate
              Log = NullLogger.Instance }

        return
            { App = app
              DataSource = dataSource
              Time = time
              Client = AppClient.create env }
    }

let private found (result: LookupResult) =
    match result with
    | Found actor -> actor
    | other -> failwith $"expected Found, got {other}"

let private foundOf (result: Task<LookupResult>) : Task<LocalActor> =
    task {
        let! r = result
        return found r
    }

type AppClientTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-001 a lookup by handle stores the actor with a key made in the same insert``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-1" "alex")
            let! result = s.Client.ByHandle "alex"
            let actor = found result

            Assert.Equal("u-1", actor.AppUserId)
            Assert.Equal("alex", actor.Handle)
            Assert.Equal("Person", actor.ActorType)
            Assert.False actor.Locked
            Assert.Equal(Some "<p>hello</p>", actor.Summary)

            // FR-006: the key exists, belongs to this actor id and works
            use priv = KeyVault.loadPrivateKey Seed.keys (Ids.actorId Seed.domain "u-1") actor.EncPrivateKey
            use pub = KeyVault.importPublicPem actor.PublicKeyPem
            let data = "x"B
            let signature = priv.SignData(data, Security.Cryptography.HashAlgorithmName.SHA256, Security.Cryptography.RSASignaturePadding.Pkcs1)
            Assert.True(pub.VerifyData(data, signature, Security.Cryptography.HashAlgorithmName.SHA256, Security.Cryptography.RSASignaturePadding.Pkcs1))

            Assert.Equal<string list>([ "/megafono/actors?handle=alex" ], s.App.LookupPaths)
            Assert.Equal(0, s.App.UnauthorizedLookups) // the bearer token was sent
        }

    [<Fact>]
    member _.``FR-060 a lookup by id gives the same actor, and the handle is matched case-insensitively``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser { FakeApp.user "u-2" "sam" with Type = "Group"; Locked = true }
            let! byId = foundOf (s.Client.ById "u-2")
            let! byHandle = s.Client.ByHandle "SAM"
            Assert.Equal("sam", byId.Handle)
            Assert.Equal("Group", byId.ActorType)
            Assert.True byId.Locked
            Assert.Equal(byId.AppUserId, (found byHandle).AppUserId)
        }

    [<Fact>]
    member _.``FR-001 a fresh stored profile answers without calling the app, a stale one asks again``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-3" "kim")
            let! _ = s.Client.ById "u-3"
            s.Time.Advance(TimeSpan.FromMinutes 4.0)
            let! _ = s.Client.ById "u-3"
            let! _ = s.Client.ByHandle "kim"
            Assert.Equal(1, s.App.LookupPaths.Length)

            s.App.AddUser { FakeApp.user "u-3" "kim" with Name = "Kim Renamed" }
            s.Time.Advance(TimeSpan.FromMinutes 2.0)
            let! updated = foundOf (s.Client.ById "u-3")
            Assert.Equal(2, s.App.LookupPaths.Length)
            Assert.Equal("Kim Renamed", updated.Name)
        }

    [<Fact>]
    member _.``the app's 404 is NotFound, and an unknown or malformed key never reaches the app``() : Task =
        task {
            let! s = setup pg true
            let! unknown = s.Client.ById "nobody"
            let! byHandle = s.Client.ByHandle "nobody"
            Assert.Equal(NotFound, unknown)
            Assert.Equal(NotFound, byHandle)

            let before = s.App.LookupPaths.Length
            let! badId = s.Client.ById "../../etc"
            let! badHandle = s.Client.ByHandle "no spaces"
            Assert.Equal(NotFound, badId)
            Assert.Equal(NotFound, badHandle)
            Assert.Equal(before, s.App.LookupPaths.Length)
        }

    [<Fact>]
    member _.``FR-001 an app that fails falls back to the stored profile for 24 hours, then to nothing``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-4" "lee")
            let! _ = s.Client.ById "u-4"
            s.App.LookupMode <- FakeApp.Fail500

            s.Time.Advance(TimeSpan.FromMinutes 10.0)
            let! within = s.Client.ById "u-4"
            Assert.True(match within with UnavailableWithFallback a -> a.AppUserId = "u-4" | _ -> false)

            s.Time.Advance(TimeSpan.FromHours 24.0)
            let! late = s.Client.ById "u-4"
            Assert.Equal(UnavailableNoFallback, late)

            // Never seen: nothing to fall back to
            let! never = s.Client.ById "u-never"
            Assert.Equal(UnavailableNoFallback, never)
        }

    [<Fact>]
    member _.``FR-001 an app that does not answer within 5 seconds counts as unavailable``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-5" "pat")
            let! _ = s.Client.ById "u-5"
            s.App.LookupMode <- FakeApp.Hang
            s.Time.Advance(TimeSpan.FromMinutes 10.0)
            let started = DateTime.UtcNow
            let! result = s.Client.ById "u-5"
            Assert.True(match result with UnavailableWithFallback _ -> true | _ -> false)
            Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 4.5, 9.0)
        }

    [<Fact>]
    member _.``data-model an answer that breaks the validation rules is treated as unavailable and stores nothing``() : Task =
        task {
            let! s = setup pg true
            let bad =
                [ { FakeApp.user "u-6" "ok" with Handle = "Bad Handle" }
                  { FakeApp.user "u-7" "ok3" with Name = "" }
                  { FakeApp.user "u-8" "ok4" with Type = "Application" }
                  { FakeApp.user "u-9" "ok5" with ProfileUrl = "/relative" } ]

            for u in bad do
                s.App.AddUser u

            for u in bad do
                let! result = s.Client.ById u.Id
                Assert.Equal(UnavailableNoFallback, result)

            let! rows = Db.count s.DataSource "SELECT count(*) FROM local_actors"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``an http profile URL is refused unless the test switch is on``() : Task =
        task {
            let! s = setup pg false
            s.App.AddUser { FakeApp.user "u-10" "web" with ProfileUrl = "http://app.example/u/web" }
            let! result = s.Client.ById "u-10"
            Assert.Equal(UnavailableNoFallback, result)
        }

    [<Fact>]
    member _.``an answer for a different id than asked is refused``() : Task =
        task {
            let! s = setup pg true
            // The app returns user u-11's profile for the id u-12 (a buggy or hostile app)
            s.App.Users["u-12"] <- FakeApp.user "u-11" "mix"
            let! result = s.Client.ById "u-12"
            Assert.Equal(UnavailableNoFallback, result)
        }

    [<Fact>]
    member _.``data-model a changed handle updates the handle and keeps the key``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-13" "old")
            let! first = foundOf (s.Client.ById "u-13")

            s.App.AddUser(FakeApp.user "u-13" "renamed")
            s.Time.Advance(TimeSpan.FromMinutes 6.0)
            let! second = foundOf (s.Client.ById "u-13")

            Assert.Equal("renamed", second.Handle)
            Assert.Equal(first.PublicKeyPem, second.PublicKeyPem)
            Assert.Equal<byte[]>(first.EncPrivateKey, second.EncPrivateKey)
            Assert.Equal(first.CreatedAt, second.CreatedAt)
            let! rows = Db.count s.DataSource "SELECT count(*) FROM local_actors"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``a new handle that another live user holds is unavailable, not a crash``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-14" "first")
            s.App.AddUser(FakeApp.user "u-15" "second")
            let! _ = s.Client.ById "u-14"
            let! _ = s.Client.ById "u-15"

            // u-15 takes u-14's handle in the app before u-14 renamed
            s.App.AddUser(FakeApp.user "u-15" "first")
            s.Time.Advance(TimeSpan.FromMinutes 6.0)
            let! result = s.Client.ById "u-15"
            Assert.True(match result with UnavailableWithFallback a -> a.Handle = "second" | _ -> false)
        }

    [<Fact>]
    member _.``FR-006 concurrent first lookups end with one row and one key``() : Task =
        task {
            let! s = setup pg true
            s.App.AddUser(FakeApp.user "u-16" "race")
            let! results = Task.WhenAll [| for _ in 1..8 -> s.Client.ById "u-16" |]
            let pems = results |> Array.map (fun r -> (found r).PublicKeyPem) |> Array.distinct
            let! rows = Db.count s.DataSource "SELECT count(*) FROM local_actors"
            let! stored = Db.scalar s.DataSource "SELECT public_key_pem FROM local_actors"
            Assert.Equal(1L, rows)
            // Every caller got a row, and the stored key is the one that won; later calls all see it
            let! again = s.Client.ById "u-16"
            Assert.Equal(string stored, (found again).PublicKeyPem)
            Assert.Contains(string stored, pems)
        }

    [<Fact>]
    member _.``parseProfile applies the rules of the app contract``() =
        let parse = AppClient.parseProfile false

        let ok = parse """{"id":"u1","handle":"Alex","name":"Alex","profileUrl":"https://a.example/u/alex","locked":false}"""
        Assert.Equal("alex", (Result.defaultWith (fun e -> failwith e) ok).Handle)
        Assert.Equal("Person", (Result.defaultWith (fun e -> failwith e) ok).Type)

        for bad in
            [ "not json"
              "[]"
              """{"handle":"a","name":"A","profileUrl":"https://a.example","locked":false}"""
              """{"id":"u1","handle":"a","name":"A","profileUrl":"https://a.example"}"""
              """{"id":"u1","handle":"a","name":"A","profileUrl":"https://a.example","locked":"yes"}"""
              """{"id":"u1","handle":"a","name":"A","profileUrl":"https://a.example","locked":false,"iconUrl":"nope"}"""
              """{"id":"u1","handle":"a","name":"A","profileUrl":"https://a.example","locked":false,"summary":5}""" ] do
            Assert.True((parse bad).IsError, bad)
