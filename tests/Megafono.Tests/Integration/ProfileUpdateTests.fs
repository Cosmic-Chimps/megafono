module Megafono.Tests.Integration.ProfileUpdateTests

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
      Db: NpgsqlDataSource
      Time: FakeTime
      One: StubRemote.StubRemote
      Two: StubRemote.StubRemote }

let private put (w: World) (id: string) =
    task {
        let! response = w.Host.InternalClient(Some MegafonoHost.token).PutAsync($"/internal/actors/{id}", null)
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser { FakeApp.user "u-1" "alex" with ProfileUrl = "https://app.example/u/alex" }
        let! one = StubRemote.start [ "bob"; "bea" ]
        let! two = StubRemote.start [ "carl" ]
        let time = FakeTime()
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url time)

        let w =
            { App = app; Host = host; Db = NpgsqlDataSource.Create cs; Time = time; One = one; Two = two }

        // The actor exists once the app has been asked about it
        let! status, _ = put w "u-1"
        Assert.Equal(HttpStatusCode.Accepted, status)
        return w
    }

let private follower (w: World) (stub: StubRemote.StubRemote) (name: string) (shared: bool) (status: string) =
    Seed.remoteFollower w.Db "u-1" (stub.ActorId name) (stub.InboxUrl name) (if shared then Some $"{stub.BaseUrl}/inbox" else None) status

let private rename (w: World) (name: string) =
    w.App.AddUser { FakeApp.user "u-1" "alex" with Name = name; ProfileUrl = "https://app.example/u/alex" }

type ProfileUpdateTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-007 a changed profile sends one signed Update{Person} to each follower's server``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! follower w w.One "bob" false "accepted"
            do! follower w w.Two "carl" false "accepted"

            rename w "Alex Renamed"
            let! status, body = put w "u-1"
            Assert.Equal(HttpStatusCode.Accepted, status)
            use result = JsonDocument.Parse body
            Assert.True(result.RootElement.GetProperty("changed").GetBoolean())
            Assert.Equal(2, result.RootElement.GetProperty("deliveries").GetInt32())

            do! waitUntil "both inboxes to receive the Update" (TimeSpan.FromSeconds 20.0) (fun () -> w.One.Posts.Length = 1 && w.Two.Posts.Length = 1)

            let! pem = Db.scalar w.Db "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use pub = KeyVault.importPublicPem (string pem)

            for stub, path in [ w.One, "/users/bob/inbox"; w.Two, "/users/carl/inbox" ] do
                let post = stub.Posts |> List.exactlyOne
                Assert.Equal(path, post.Path)

                // Signed with the user's key and a matching digest
                let request: HttpSignatures.SignedRequest =
                    { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }

                Assert.Equal(Ok "https://megafono.test/ap/actors/u-1#main-key", HttpSignatures.draftCavage.ExtractKeyId request)
                Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify w.Time pub request)

                use activity = JsonDocument.Parse post.Body
                let a = activity.RootElement
                Assert.Equal("Update", a.GetProperty("type").GetString())
                Assert.Equal("https://megafono.test/ap/actors/u-1", a.GetProperty("actor").GetString())
                Assert.StartsWith("https://megafono.test/ap/activities/", a.GetProperty("id").GetString())
                Assert.Equal("https://www.w3.org/ns/activitystreams#Public", (a.GetProperty("to")[0]).GetString())
                let actor = a.GetProperty("object")
                Assert.Equal("Person", actor.GetProperty("type").GetString())
                Assert.Equal("https://megafono.test/ap/actors/u-1", actor.GetProperty("id").GetString())
                Assert.Equal("Alex Renamed", actor.GetProperty("name").GetString())
        }

    [<Fact>]
    member _.``FR-007 no change sends nothing, and repeating the call does not repeat the Update``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! follower w w.One "bob" false "accepted"

            let! status, body = put w "u-1"
            Assert.Equal(HttpStatusCode.Accepted, status)
            use result = JsonDocument.Parse body
            Assert.False(result.RootElement.GetProperty("changed").GetBoolean())

            rename w "New Name"
            let! _, first = put w "u-1"
            let! _, second = put w "u-1"
            use f = JsonDocument.Parse first
            use s = JsonDocument.Parse second
            Assert.True(f.RootElement.GetProperty("changed").GetBoolean())
            Assert.False(s.RootElement.GetProperty("changed").GetBoolean())

            do! waitUntil "the one Update" (TimeSpan.FromSeconds 20.0) (fun () -> w.One.Posts.Length = 1)
            do! Task.Delay 800
            Assert.Equal(1, w.One.Posts.Length)
            let! activities = Db.count w.Db "SELECT count(*) FROM activities WHERE activity_type = 'Update'"
            Assert.Equal(1L, activities)
        }

    [<Fact>]
    member _.``FR-007 followers on one server share one delivery to the shared inbox, and only accepted followers get it``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! follower w w.One "bob" true "accepted"
            do! follower w w.One "bea" true "accepted"
            do! follower w w.Two "carl" false "pending" // not a follower yet

            rename w "Shared"
            let! _, body = put w "u-1"
            use result = JsonDocument.Parse body
            Assert.Equal(1, result.RootElement.GetProperty("deliveries").GetInt32())

            do! waitUntil "the shared inbox delivery" (TimeSpan.FromSeconds 20.0) (fun () -> w.One.Posts.Length = 1)
            do! Task.Delay 600
            Assert.Equal("/inbox", (List.exactlyOne w.One.Posts).Path)
            Assert.Empty w.Two.Posts
        }

    [<Fact>]
    member _.``FR-007 every field the actor document shows counts as a change``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host

            let variants =
                [ "summary", { FakeApp.user "u-1" "alex" with Summary = Some "<p>new</p>" }
                  "icon", { FakeApp.user "u-1" "alex" with IconUrl = Some "https://app.example/new.png" }
                  "locked", { FakeApp.user "u-1" "alex" with Locked = true }
                  "profile url", { FakeApp.user "u-1" "alex" with ProfileUrl = "https://app.example/u/alex2" } ]

            for label, user in variants do
                w.App.AddUser { user with ProfileUrl = (if label = "profile url" then user.ProfileUrl else "https://app.example/u/alex") }
                let! _, body = put w "u-1"
                use result = JsonDocument.Parse body
                Assert.True(result.RootElement.GetProperty("changed").GetBoolean(), label)
        }

    [<Fact>]
    member _.``contract an unknown user is 404, an app that is down is 503, and the bearer is required``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            let! unknown, text = put w "nobody"
            Assert.Equal(HttpStatusCode.NotFound, unknown)
            Assert.Contains("not_found", text)

            w.App.LookupMode <- FakeApp.Fail500
            let! down, downText = put w "u-1"
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down)
            Assert.Contains("app_unavailable", downText)

            let! anonymous = w.Host.InternalClient(None).PutAsync("/internal/actors/u-1", null)
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode)
            let! onFederation = w.Host.FederationClient().PutAsync("/internal/actors/u-1", null)
            Assert.Equal(HttpStatusCode.NotFound, onFederation.StatusCode)
        }

    [<Fact>]
    member _.``Principle V the stored profile and its Update are saved in one transaction``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! follower w w.One "bob" false "accepted"

            // Make the queueing step fail after the profile write: the whole transaction must roll back
            do! Db.execute w.Db "ALTER TABLE deliveries RENAME TO deliveries_away"
            rename w "Lost If Not Atomic"
            let! failed, _ = put w "u-1"
            Assert.Equal(HttpStatusCode.InternalServerError, failed)

            let! name = Db.scalar w.Db "SELECT name FROM local_actors WHERE app_user_id = 'u-1'"
            Assert.NotEqual<string>("Lost If Not Atomic", string name)
            let! activities = Db.count w.Db "SELECT count(*) FROM activities WHERE activity_type = 'Update'"
            Assert.Equal(0L, activities)

            // The queue works again: the same call now stores the change and announces it, since nothing was half-done
            do! Db.execute w.Db "ALTER TABLE deliveries_away RENAME TO deliveries"
            let! status, body = put w "u-1"
            Assert.Equal(HttpStatusCode.Accepted, status)
            use result = JsonDocument.Parse body
            Assert.True(result.RootElement.GetProperty("changed").GetBoolean())
            Assert.Equal(1, result.RootElement.GetProperty("deliveries").GetInt32())
            do! waitUntil "the Update" (TimeSpan.FromSeconds 20.0) (fun () -> w.One.Posts.Length = 1)
        }
