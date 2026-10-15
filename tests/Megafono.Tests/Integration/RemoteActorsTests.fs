module Megafono.Tests.Integration.RemoteActorsTests

open System
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.Extensions.Logging.Abstractions
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type Setup =
    { Stub: StubRemote.StubRemote
      DataSource: NpgsqlDataSource
      Time: FakeTime
      Instance: InstanceKey.InstanceKeyProvider
      Env: Env }

let private envFor (dataSource: NpgsqlDataSource) (time: FakeTime) (instance: InstanceKey.InstanceKeyProvider) (allowPrivate: bool) : Env =
    { DataSource = dataSource
      Http = SafeHttp.create (SafeHttp.createHandler allowPrivate) allowPrivate "Megafono/test" time
      Instance = instance
      Time = time
      AllowPrivateAddresses = allowPrivate
      Log = NullLogger.Instance }

let private setup (pg: PostgresFixture) : Task<Setup> =
    task {
        let! connectionString = pg.CreateDatabase()
        let dataSource = NpgsqlDataSource.Create connectionString
        do! Database.initializeSchema dataSource
        let time = FakeTime()
        let instance = InstanceKey.create dataSource Seed.keys Seed.domain time
        let! stub = StubRemote.start [ "alice"; "mallory" ]

        return
            { Stub = stub
              DataSource = dataSource
              Time = time
              Instance = instance
              Env = envFor dataSource time instance true }
    }

let private ok (result: Result<RemoteActor, RemoteError>) : RemoteActor =
    match result with
    | Ok actor -> actor
    | Error e -> failwith $"expected an actor, got {e}"

let private isError (predicate: RemoteError -> bool) (result: Result<RemoteActor, RemoteError>) =
    match result with
    | Error e -> Assert.True(predicate e, $"unexpected error {e}")
    | Ok _ -> failwith "expected an error"

let private invalid (e: RemoteError) = match e with Invalid _ -> true | _ -> false

let private getCount (stub: StubRemote.StubRemote) (path: string) =
    stub.Gets |> List.filter (fun g -> g.Path = path) |> List.length

type RemoteActorsTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-011 getActor fetches, validates and stores the actor``() : Task =
        task {
            let! s = setup pg
            s.Stub.Mutate <- Some(fun _ doc -> doc["name"] <- JsonValue.Create "<b>Alice</b> &amp; co<script>x()</script>")
            let! result = getActor s.Env (s.Stub.ActorId "alice")
            let actor = ok result

            Assert.Equal(s.Stub.ActorId "alice", actor.Id)
            Assert.Equal(s.Stub.Authority, actor.Host)
            Assert.Equal(Some $"alice@{s.Stub.Authority}", actor.Handle)
            Assert.Equal("Person", actor.ActorType)
            Assert.Equal(s.Stub.InboxUrl "alice", actor.Inbox)
            let aliceId = s.Stub.ActorId "alice"
            Assert.Equal(Some $"{aliceId}/followers", actor.FollowersUrl)
            Assert.Equal(Some $"{s.Stub.BaseUrl}/@alice", actor.ProfileUrl)
            Assert.Equal(s.Stub.KeyId "alice", actor.KeyId)
            Assert.Equal(s.Stub.PublicKeyPem, actor.PublicKeyPem)
            // FR-056: the name is text, not markup
            Assert.Equal(Some "Alice & co", actor.Name)

            let! rows = Db.count s.DataSource "SELECT count(*) FROM remote_actors"
            Assert.Equal(1L, rows)
        }

    [<Fact>]
    member _.``FR-020 the fetch is signed by the instance actor and the signature verifies``() : Task =
        task {
            let! s = setup pg
            let! _ = getActor s.Env (s.Stub.ActorId "alice")
            let get = s.Stub.Gets |> List.find (fun g -> g.Path = "/users/alice")
            let! instance = s.Instance.Get()

            let request: HttpSignatures.SignedRequest =
                { Method = "GET"; PathAndQuery = get.Path; Headers = get.Headers; Body = [||] }

            Assert.Equal(Ok instance.KeyId, HttpSignatures.draftCavage.ExtractKeyId request)
            Assert.Equal("https://megafono.test/ap/actor#main-key", instance.KeyId)
            use pub = KeyVault.importPublicPem instance.PublicKeyPem
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify s.Time pub request)
            Assert.Contains(get.Headers, fun (n, v) -> n = "Accept" && v.StartsWith "application/activity+json")
        }

    [<Fact>]
    member _.``R-02 a stored actor is used for 24 hours, then fetched again``() : Task =
        task {
            let! s = setup pg
            let id = s.Stub.ActorId "alice"
            let! _ = getActor s.Env id
            s.Time.Advance(TimeSpan.FromHours 23.0)
            let! _ = getActor s.Env id
            Assert.Equal(1, getCount s.Stub "/users/alice")

            s.Time.Advance(TimeSpan.FromHours 2.0)
            let! _ = getActor s.Env id
            Assert.Equal(2, getCount s.Stub "/users/alice")

            let! _ = refreshActor s.Env id
            Assert.Equal(3, getCount s.Stub "/users/alice")
        }

    [<Fact>]
    member _.``data-model a shared inbox is kept only when it is on the actor's own host``() : Task =
        task {
            let! s = setup pg
            s.Stub.SharedInbox <- Some $"{s.Stub.BaseUrl}/inbox"
            let! own = getActor s.Env (s.Stub.ActorId "alice")
            Assert.Equal(Some $"{s.Stub.BaseUrl}/inbox", (ok own).SharedInbox)

            s.Stub.SharedInbox <- Some "https://evil.example/inbox"
            let! foreign = refreshActor s.Env (s.Stub.ActorId "alice")
            Assert.Equal(None, (ok foreign).SharedInbox)
        }

    [<Fact>]
    member _.``FR-011 a document that breaks the rules is refused and not stored``() : Task =
        task {
            let! s = setup pg

            let cases: (string * (JsonObject -> unit)) list =
                [ "id on another host", (fun d -> d["id"] <- JsonValue.Create "http://127.0.0.2:1/users/alice")
                  "owner is someone else", (fun d -> d["publicKey"]["owner"] <- JsonValue.Create "http://127.0.0.1:1/users/other")
                  "no inbox", (fun d -> d.Remove "inbox" |> ignore)
                  "not an actor type", (fun d -> d["type"] <- JsonValue.Create "Note")
                  "no public key", (fun d -> d.Remove "publicKey" |> ignore)
                  "key id on another host", (fun d -> d["publicKey"]["id"] <- JsonValue.Create "http://127.0.0.2:1/users/alice#main-key")
                  "not a key", (fun d -> d["publicKey"]["publicKeyPem"] <- JsonValue.Create "-----BEGIN PUBLIC KEY-----\nAAAA\n-----END PUBLIC KEY-----") ]

            for label, change in cases do
                s.Stub.Mutate <- Some(fun _ doc -> change doc)
                let! result = getActor s.Env (s.Stub.ActorId "alice")
                isError invalid result |> ignore

            let! rows = Db.count s.DataSource "SELECT count(*) FROM remote_actors"
            Assert.Equal(0L, rows)
        }

    [<Fact>]
    member _.``a 404 is NotFound and a server that is down is Unreachable``() : Task =
        task {
            let! s = setup pg
            let! missing = getActor s.Env $"{s.Stub.BaseUrl}/users/nobody"
            isError (fun e -> e = NotFound) missing
            let! down = getActor s.Env "http://127.0.0.1:1/users/alice"
            isError (fun e -> match e with Unreachable _ -> true | _ -> false) down
        }

    [<Fact>]
    member _.``FR-021 private targets are refused without the test switch``() : Task =
        task {
            let! s = setup pg
            let strict = envFor s.DataSource s.Time s.Instance false
            let unsafe (e: RemoteError) = match e with UnsafeTarget _ -> true | _ -> false

            let! http = getActor strict (s.Stub.ActorId "alice")
            isError unsafe http
            let! loopback = getActor strict "https://127.0.0.1:1/users/alice"
            isError unsafe loopback
            let! relative = getActor strict "/users/alice"
            isError unsafe relative
            let! webfinger = resolveHandle strict "alice@127.0.0.1:1"
            isError unsafe webfinger
            Assert.Empty s.Stub.Gets
        }

    [<Fact>]
    member _.``FR-012 findKey takes #main-key from the actor document and caches it``() : Task =
        task {
            let! s = setup pg
            let keyId = s.Stub.KeyId "alice"
            Assert.EndsWith("#main-key", keyId)
            let! first = findKey s.Env keyId
            let! second = findKey s.Env keyId
            Assert.Equal(s.Stub.ActorId "alice", (ok first).Id)
            Assert.Equal((ok first).PublicKeyPem, (ok second).PublicKeyPem)
            Assert.Equal(1, getCount s.Stub "/users/alice") // the fragment is stripped for fetching
        }

    [<Fact>]
    member _.``R-01 findKey follows the owner of a GoToSocial style /main-key stub``() : Task =
        task {
            let! s = setup pg
            s.Stub.KeyStyle <- StubRemote.PathStub
            let keyId = s.Stub.KeyId "alice"
            Assert.EndsWith("/main-key", keyId)
            let! result = findKey s.Env keyId
            let actor = ok result
            Assert.Equal(s.Stub.ActorId "alice", actor.Id)
            Assert.Equal(keyId, actor.KeyId)
            Assert.Equal(1, getCount s.Stub "/users/alice/main-key")
            Assert.Equal(1, getCount s.Stub "/users/alice")
        }

    [<Fact>]
    member _.``R-01 findKey follows the owner of a bare key object``() : Task =
        task {
            let! s = setup pg
            s.Stub.KeyStyle <- StubRemote.PathKeyObject
            let! result = findKey s.Env (s.Stub.KeyId "alice")
            Assert.Equal(s.Stub.ActorId "alice", (ok result).Id)
        }

    [<Fact>]
    member _.``FR-012 a key id the actor does not declare is refused``() : Task =
        task {
            let! s = setup pg
            let aliceId = s.Stub.ActorId "alice"
            let! result = findKey s.Env $"{aliceId}#other-key"
            isError invalid result
        }

    [<Fact>]
    member _.``FR-012 refetchKey sees a rotated key that the cache does not``() : Task =
        task {
            let! s = setup pg
            let keyId = s.Stub.KeyId "alice"
            let! before = findKey s.Env keyId
            let oldPem = (ok before).PublicKeyPem

            s.Stub.RotateKey()
            let! cached = findKey s.Env keyId
            Assert.Equal(oldPem, (ok cached).PublicKeyPem)

            let! refetched = refetchKey s.Env keyId
            Assert.Equal(s.Stub.PublicKeyPem, (ok refetched).PublicKeyPem)
            Assert.NotEqual<string>(oldPem, (ok refetched).PublicKeyPem)

            let! afterwards = findKey s.Env keyId
            Assert.Equal(s.Stub.PublicKeyPem, (ok afterwards).PublicKeyPem)
        }

    [<Fact>]
    member _.``an actor cannot claim a key id that already belongs to another actor``() : Task =
        task {
            let! s = setup pg
            let! _ = getActor s.Env (s.Stub.ActorId "alice")

            // mallory publishes alice's key id (same host, so the host rule alone would not stop it)
            s.Stub.Mutate <-
                Some(fun name doc -> if name = "mallory" then doc["publicKey"]["id"] <- JsonValue.Create(s.Stub.KeyId "alice"))

            let! result = getActor s.Env (s.Stub.ActorId "mallory")
            isError invalid result
        }

    [<Fact>]
    member _.``R-03 resolveHandle follows the WebFinger self link, with or without the leading at-sign``() : Task =
        task {
            let! s = setup pg
            let! withAt = resolveHandle s.Env $"@alice@{s.Stub.Authority}"
            let! without = resolveHandle s.Env $"ALICE@{s.Stub.Authority.ToUpperInvariant()}"
            Assert.Equal(s.Stub.ActorId "alice", (ok withAt).Id)
            Assert.Equal(s.Stub.ActorId "alice", (ok without).Id)
            Assert.Equal(Some $"alice@{s.Stub.Authority}", (ok withAt).Handle)

            let webfinger = s.Stub.Gets |> List.find (fun g -> g.Path.StartsWith "/.well-known/webfinger")
            Assert.Contains("acct:alice%40", webfinger.Path.Replace("alice@", "alice%40"))
            Assert.Contains(webfinger.Headers, fun (n, _) -> n = "Signature")
        }

    [<Fact>]
    member _.``R-03 resolveHandle answers NotFound for an unknown user and for nonsense``() : Task =
        task {
            let! s = setup pg
            let! unknown = resolveHandle s.Env $"bob@{s.Stub.Authority}"
            isError (fun e -> e = NotFound) unknown

            for nonsense in [ ""; "alice"; "@"; "alice@"; "@@host"; "a b@host.example"; "alice@host/path"; "a@b@c" ] do
                let! result = resolveHandle s.Env nonsense
                isError (fun e -> e = NotFound) result
        }

    [<Fact>]
    member _.``resolveTarget takes a handle or an actor URL``() : Task =
        task {
            let! s = setup pg
            let! byUrl = resolveTarget s.Env (s.Stub.ActorId "alice")
            let! byHandle = resolveTarget s.Env $"alice@{s.Stub.Authority}"
            Assert.Equal((ok byUrl).Id, (ok byHandle).Id)
        }

    [<Fact>]
    member _.``parseHandle splits user and host``() =
        Assert.Equal(Some("alice", "mastodon.social"), parseHandle "@alice@mastodon.social")
        Assert.Equal(Some("alice", "mastodon.social"), parseHandle " alice@Mastodon.Social ")
        Assert.Equal(Some("a.b-c_d", "127.0.0.1:8080"), parseHandle "a.b-c_d@127.0.0.1:8080")
        Assert.Equal(None, parseHandle null)
        Assert.Equal(None, parseHandle "alice@")
        Assert.Equal(None, parseHandle "alice@bad host")
        Assert.Equal(None, parseHandle "alice@host.example/x")

    [<Fact>]
    member _.``isSameOrSubdomain only accepts the host itself or a real subdomain``() =
        Assert.True(isSameOrSubdomain "example.com" "example.com")
        Assert.True(isSameOrSubdomain "example.com" "shared.example.com")
        Assert.True(isSameOrSubdomain "Example.com" "SHARED.example.COM")
        Assert.False(isSameOrSubdomain "example.com" "evilexample.com")
        Assert.False(isSameOrSubdomain "example.com" "example.com.evil.net")
        Assert.False(isSameOrSubdomain "shared.example.com" "example.com")
