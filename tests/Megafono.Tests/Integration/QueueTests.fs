module Megafono.Tests.Integration.QueueTests

open System
open System.Text.Json.Nodes
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Npgsql
open Xunit
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Workers
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type World =
    { ConnectionString: string
      Db: NpgsqlDataSource
      Time: FakeTime
      App: FakeApp.FakeApp
      Stub: StubRemote.StubRemote }

let private world (pg: PostgresFixture) : Task<World> =
    task {
        let! connectionString = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        let! stub = StubRemote.start [ "alice"; "bob" ]

        return
            { ConnectionString = connectionString
              Db = NpgsqlDataSource.Create connectionString
              Time = FakeTime()
              App = app
              Stub = stub }
    }

let private startHost (w: World) (extra: (string * string) list) (faults: Deliveries.DeliveryFaults option) =
    MegafonoHost.start
        { MegafonoHost.settings w.ConnectionString w.App.Url w.Time with
            Extra = extra
            Faults = faults }

let private stop (host: MegafonoHost.MegafonoHost) : Task =
    task {
        do! host.Stop()
        do! (host :> IAsyncDisposable).DisposeAsync()
    }

type DeliveryRow =
    { Status: string
      Attempts: int
      LastError: string option
      NextAttemptAt: DateTimeOffset
      DeliveredAt: DateTimeOffset option }

let private deliveryRow (db: NpgsqlDataSource) (activity: Guid) (inbox: string) : Task<DeliveryRow> =
    task {
        use command =
            db.CreateCommand "SELECT status, attempts, last_error, next_attempt_at, delivered_at FROM deliveries WHERE activity = @a AND inbox_url = @i"

        command.Parameters.AddWithValue("a", activity) |> ignore
        command.Parameters.AddWithValue("i", inbox) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if not (reader.Read()) then
            return failwith "no such delivery"
        else
            return
                { Status = reader.GetString 0
                  Attempts = reader.GetInt32 1
                  LastError = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
                  NextAttemptAt = reader.GetFieldValue<DateTimeOffset> 3
                  DeliveredAt = if reader.IsDBNull 4 then None else Some(reader.GetFieldValue<DateTimeOffset> 4) }
    }

let private waitForStatus (db: NpgsqlDataSource) (activity: Guid) (inbox: string) (what: string) (predicate: DeliveryRow -> bool) : Task =
    waitUntilAsync what (TimeSpan.FromSeconds 15.0) (fun () ->
        task {
            let! row = deliveryRow db activity inbox
            return predicate row
        })

/// Queue one delivery of a fresh activity by `u1` to the stub's `alice` inbox
let private queueOne (w: World) (inboxName: string) : Task<Guid * byte[] * string> =
    task {
        let! activityId, body = Seed.activity w.Db "u1"
        let inbox = w.Stub.InboxUrl inboxName
        let! _ = Seed.enqueueDeliveries w.Db w.Time activityId [ inbox ]
        return activityId, body, inbox
    }

let private listening (db: NpgsqlDataSource) (channel: string) =
    task {
        let! n =
            Db.count db ("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND query LIKE 'LISTEN " + channel + "%'")

        return n > 0L
    }

let private seedUser (w: World) =
    task {
        let! pem = Seed.localActor w.Db "u1" "alex"
        return pem
    }

let private status (db: NpgsqlDataSource) (sql: string) =
    task {
        let! v = Db.scalar db sql
        return string v
    }

/// A string at a dotted path of a JSON document
let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

let private postRequest (post: StubRemote.ReceivedPost) : HttpSignatures.SignedRequest =
    { Method = "POST"; PathAndQuery = post.Path; Headers = post.Headers; Body = post.Body }

type QueueTests(pg: PostgresFixture) =

    // ---- deliveries ---------------------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-050 a queued delivery is picked up by NOTIFY, signed with the author's key and marked delivered``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! pem = seedUser w
            do! waitUntilAsync "the LISTEN connection" (TimeSpan.FromSeconds 10.0) (fun () -> listening w.Db Outbox.deliveriesChannel)

            // No Wake call: only the NOTIFY sent in the enqueue transaction can get this delivered before the 5 s poll
            let started = DateTime.UtcNow
            let! activityId, body, inbox = queueOne w "alice"
            do! waitForStatus w.Db activityId inbox "delivery" (fun r -> r.Status = "delivered")
            Assert.True((DateTime.UtcNow - started).TotalSeconds < 4.0, "delivered by NOTIFY, not by the poll")

            let post = w.Stub.Posts |> List.exactlyOne
            Assert.Equal("/users/alice/inbox", post.Path)
            Assert.Equal<byte[]>(body, post.Body)
            Assert.Equal(Some "application/activity+json", post.Header "Content-Type")
            Assert.Equal(Some "Megafono/0.1.0 (+https://megafono.test/)", post.Header "User-Agent")

            // Signed as the author, verifies with the author's public key, and the digest covers the body
            Assert.Equal(Ok "https://megafono.test/ap/actors/u1#main-key", HttpSignatures.draftCavage.ExtractKeyId(postRequest post))
            use pub = KeyVault.importPublicPem pem
            Assert.Equal(Ok(), HttpSignatures.draftCavage.Verify w.Time pub (postRequest post))

            let! row = deliveryRow w.Db activityId inbox
            Assert.Equal(1, row.Attempts)
            Assert.True row.DeliveredAt.IsSome
            Assert.Equal(None, row.LastError)
        }

    [<Fact>]
    member _.``FR-051 a failing delivery is rescheduled with backoff and resent with the same bytes``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            w.Stub.InboxMode <- StubRemote.Fail500
            let! activityId, body, inbox = queueOne w "alice"

            do! waitForStatus w.Db activityId inbox "first failure" (fun r -> r.Status = "pending" && r.Attempts = 1)
            let! first = deliveryRow w.Db activityId inbox
            Assert.Equal(Some "HTTP 500", first.LastError)
            // 1 minute plus up to 10 % jitter after the (fake) now
            let delay = first.NextAttemptAt - w.Time.GetUtcNow()
            Assert.InRange(delay, TimeSpan.FromMinutes 1.0 - TimeSpan.FromSeconds 1.0, TimeSpan.FromMinutes 1.1)

            // Not due yet: nothing happens
            host.Wake()
            do! Task.Delay 400
            Assert.Equal(1, w.Stub.Posts.Length)

            w.Time.Advance(TimeSpan.FromMinutes 1.2)
            host.Wake()
            do! waitForStatus w.Db activityId inbox "second failure" (fun r -> r.Attempts = 2 && r.Status = "pending")
            let! second = deliveryRow w.Db activityId inbox
            Assert.InRange(second.NextAttemptAt - w.Time.GetUtcNow(), TimeSpan.FromMinutes 2.0 - TimeSpan.FromSeconds 1.0, TimeSpan.FromMinutes 2.2)

            w.Stub.InboxMode <- StubRemote.Accept
            w.Time.Advance(TimeSpan.FromMinutes 3.0)
            host.Wake()
            do! waitForStatus w.Db activityId inbox "delivered" (fun r -> r.Status = "delivered")

            let posts = w.Stub.Posts
            Assert.Equal(3, posts.Length)
            Assert.All(posts, fun p -> Assert.Equal<byte[]>(body, p.Body))
            let! row = deliveryRow w.Db activityId inbox
            Assert.Equal(3, row.Attempts)
            Assert.Equal(None, row.LastError)
        }

    [<Fact>]
    member _.``FR-051 a failure after the 48 hour deadline ends the delivery as failed``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            w.Stub.InboxMode <- StubRemote.Fail500
            let! activityId, _, inbox = queueOne w "alice"
            do! waitForStatus w.Db activityId inbox "first failure" (fun r -> r.Attempts = 1 && r.Status = "pending")

            w.Time.Advance(TimeSpan.FromHours 47.0)
            host.Wake()
            do! waitForStatus w.Db activityId inbox "still retrying" (fun r -> r.Attempts = 2 && r.Status = "pending")

            w.Time.Advance(TimeSpan.FromHours 7.0)
            host.Wake()
            do! waitForStatus w.Db activityId inbox "failed" (fun r -> r.Status = "failed")
            let! row = deliveryRow w.Db activityId inbox
            Assert.Equal(Some "HTTP 500", row.LastError)

            // Nothing is sent after that
            let sent = w.Stub.Posts.Length
            w.Time.Advance(TimeSpan.FromDays 3.0)
            host.Wake()
            do! Task.Delay 500
            Assert.Equal(sent, w.Stub.Posts.Length)
        }

    [<Fact>]
    member _.``SC-004 a hard kill between the remote answer and the write re-sends the same bytes once``() : Task =
        task {
            let! w = world pg
            let armed = ref 1
            let fired = ref false

            let faults: Deliveries.DeliveryFaults =
                { BeforeCompletionWrite =
                    Some(fun _ ->
                        if Interlocked.Exchange(&armed.contents, 0) = 1 then
                            fired.Value <- true
                            failwith "simulated crash after the remote answered"
                        else
                            Task.CompletedTask) }

            let! first = startHost w [] (Some faults)
            let! _ = seedUser w
            let! activityId, body, inbox = queueOne w "alice"
            do! waitUntil "the fault hook" (TimeSpan.FromSeconds 15.0) (fun () -> fired.Value)

            // The remote got it, the write never happened: the row is still claimed
            Assert.Equal(1, w.Stub.Posts.Length)
            let! stuck = deliveryRow w.Db activityId inbox
            Assert.Equal("sending", stuck.Status)
            do! stop first

            // A new process: the lease (60 s) has not expired, so nothing is re-sent yet
            let! second = startHost w [] None
            use _ = MegafonoHost.disposer second
            second.Wake()
            do! Task.Delay 800
            Assert.Equal(1, w.Stub.Posts.Length)

            w.Time.Advance(TimeSpan.FromSeconds 61.0)
            second.Wake()
            do! waitForStatus w.Db activityId inbox "re-delivered" (fun r -> r.Status = "delivered")

            let posts = w.Stub.Posts
            Assert.Equal(2, posts.Length)
            Assert.Equal<byte[]>(body, posts[0].Body)
            Assert.Equal<byte[]>(posts[0].Body, posts[1].Body)
            let id = get (JsonNode.Parse(Text.Encoding.UTF8.GetString posts[1].Body)) "id"
            Assert.Equal(Ids.activityId Seed.domain activityId, id)

            // And never a third time
            w.Time.Advance(TimeSpan.FromHours 2.0)
            second.Wake()
            do! Task.Delay 500
            Assert.Equal(2, w.Stub.Posts.Length)
        }

    [<Fact>]
    member _.``SC-004 a clean stop lets the in-flight delivery finish and nothing is repeated after the restart``() : Task =
        task {
            let! w = world pg
            let! first = startHost w [] None
            let! _ = seedUser w
            w.Stub.InboxMode <- StubRemote.Slow(TimeSpan.FromMilliseconds 1500.0)
            let! activityId, _, inbox = queueOne w "alice"
            do! waitUntil "the request to be in flight" (TimeSpan.FromSeconds 15.0) (fun () -> w.Stub.InFlight = 1)

            do! stop first // returns only when the in-flight job has been recorded
            let! afterStop = deliveryRow w.Db activityId inbox
            Assert.Equal("delivered", afterStop.Status)
            Assert.Equal(1, w.Stub.Posts.Length)

            let! second = startHost w [] None
            use _ = MegafonoHost.disposer second
            w.Time.Advance(TimeSpan.FromHours 3.0)
            second.Wake()
            do! Task.Delay 1500
            Assert.Equal(1, w.Stub.Posts.Length)
        }

    [<Fact>]
    member _.``FR-053 at most 2 requests per host run at once``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let names = [ "a"; "b"; "c"; "d"; "e"; "f" ]
            names |> List.iter w.Stub.AddUser
            w.Stub.InboxMode <- StubRemote.Slow(TimeSpan.FromMilliseconds 500.0)
            let! activityId, _ = Seed.activity w.Db "u1"
            let! _ = Seed.enqueueDeliveries w.Db w.Time activityId (names |> List.map w.Stub.InboxUrl)

            for n in names do
                do! waitForStatus w.Db activityId (w.Stub.InboxUrl n) $"delivery to {n}" (fun r -> r.Status = "delivered")

            Assert.Equal(6, w.Stub.Posts.Length)
            Assert.Equal(2, w.Stub.MaxConcurrent)
        }

    [<Fact>]
    member _.``FR-053 the per-host limit is configurable``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [ "Megafono:Delivery:PerHost", "1" ] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let names = [ "a"; "b"; "c" ]
            names |> List.iter w.Stub.AddUser
            w.Stub.InboxMode <- StubRemote.Slow(TimeSpan.FromMilliseconds 300.0)
            let! activityId, _ = Seed.activity w.Db "u1"
            let! _ = Seed.enqueueDeliveries w.Db w.Time activityId (names |> List.map w.Stub.InboxUrl)

            for n in names do
                do! waitForStatus w.Db activityId (w.Stub.InboxUrl n) $"delivery to {n}" (fun r -> r.Status = "delivered")

            Assert.Equal(1, w.Stub.MaxConcurrent)
        }

    [<Fact>]
    member _.``FR-053 the global limit holds across hosts``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [ "Megafono:Delivery:PerHost", "10"; "Megafono:Delivery:Global", "3" ] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let names = [ for i in 1..8 -> $"g{i}" ]
            names |> List.iter w.Stub.AddUser
            w.Stub.InboxMode <- StubRemote.Slow(TimeSpan.FromMilliseconds 400.0)
            let! activityId, _ = Seed.activity w.Db "u1"
            let! _ = Seed.enqueueDeliveries w.Db w.Time activityId (names |> List.map w.Stub.InboxUrl)

            for n in names do
                do! waitForStatus w.Db activityId (w.Stub.InboxUrl n) $"delivery to {n}" (fun r -> r.Status = "delivered")

            Assert.Equal(3, w.Stub.MaxConcurrent)
        }

    [<Fact>]
    member _.``FR-050 two processes on one database never send the same delivery twice``() : Task =
        task {
            let! w = world pg
            let! one = startHost w [] None
            let! two = startHost w [] None
            use _ = MegafonoHost.disposer one
            use _ = MegafonoHost.disposer two
            let! _ = seedUser w
            let names = [ for i in 1..20 -> $"n{i}" ]
            names |> List.iter w.Stub.AddUser
            w.Stub.InboxMode <- StubRemote.Slow(TimeSpan.FromMilliseconds 100.0)
            let! activityId, _ = Seed.activity w.Db "u1"
            let! _ = Seed.enqueueDeliveries w.Db w.Time activityId (names |> List.map w.Stub.InboxUrl)

            for n in names do
                do! waitForStatus w.Db activityId (w.Stub.InboxUrl n) $"delivery to {n}" (fun r -> r.Status = "delivered")

            let paths = w.Stub.Posts |> List.map (fun p -> p.Path)
            Assert.Equal(20, paths.Length)
            Assert.Equal(20, (List.distinct paths).Length)
        }

    [<Fact>]
    member _.``FR-050 enqueueDeliveries makes one row per distinct inbox and a retry adds nothing``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let! activityId, _ = Seed.activity w.Db "u1"
            let inboxes = [ "https://a.example/inbox"; "https://a.example/inbox"; "https://B.example:8443/inbox"; "https://c.example/users/x/inbox" ]

            let! first = Seed.enqueueDeliveries w.Db w.Time activityId inboxes
            let! again = Seed.enqueueDeliveries w.Db w.Time activityId inboxes
            Assert.Equal(3, first)
            Assert.Equal(0, again)

            let! hosts = status w.Db "SELECT string_agg(host, ',' ORDER BY host) FROM deliveries"
            Assert.Equal("a.example,b.example:8443,c.example", hosts)
            Assert.Equal("a.example", Outbox.hostOf "https://A.example/inbox")

            // Created with the 48 h deadline on the injected clock
            let! span = Db.scalar w.Db "SELECT min(deadline_at - created_at) FROM deliveries"
            Assert.Equal(TimeSpan.FromHours 48.0, span :?> TimeSpan)

            // Nothing queued when the transaction rolls back
            let! other, _ = Seed.activity w.Db "u1"
            use! connection = w.Db.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! _ = Outbox.enqueueDeliveries tx w.Time other [ "https://z.example/inbox" ]
            do! tx.RollbackAsync()
            let! rows = Db.count w.Db $"SELECT count(*) FROM deliveries WHERE activity = '{other}'"
            Assert.Equal(0L, rows)
        }

    // ---- app events ---------------------------------------------------------------------------------------

    [<Fact>]
    member _.``FR-055 an event is retried after the app answers 500 and is never dropped, even after 48 hours``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            w.App.WebhookMode <- FakeApp.Fail500
            let! eventId = Seed.enqueueEvent w.Db w.Time "u1" (Seed.followRequested "bob")

            let state () = status w.Db $"SELECT status || ':' || attempts FROM app_events WHERE id = '{eventId}'"

            do! waitUntilAsync "first attempt" (TimeSpan.FromSeconds 15.0) (fun () -> task { let! s = state () in return s = "pending:1" })
            Assert.Equal(1, w.App.Received.Length)

            w.Time.Advance(TimeSpan.FromMinutes 1.2)
            host.Wake()
            do! waitUntilAsync "second attempt" (TimeSpan.FromSeconds 15.0) (fun () -> task { let! s = state () in return s = "pending:2" })

            // Two days later the app is still down: the event is still there, with the delay capped at 6 hours (+10 %)
            w.Time.Advance(TimeSpan.FromHours 49.0)
            host.Wake()
            do! waitUntilAsync "third attempt" (TimeSpan.FromSeconds 15.0) (fun () -> task { let! s = state () in return s = "pending:3" })
            let! next = Db.scalar w.Db $"SELECT next_attempt_at FROM app_events WHERE id = '{eventId}'"
            let delay = (next :?> DateTime) - w.Time.GetUtcNow().UtcDateTime
            Assert.InRange(delay, TimeSpan.FromMinutes 3.0, TimeSpan.FromHours 6.6)

            for _ in 1..6 do
                w.Time.Advance(TimeSpan.FromHours 7.0)
                host.Wake()
                do! Task.Delay 150

            do! waitUntil "attempts keep coming" (TimeSpan.FromSeconds 15.0) (fun () -> w.App.Received.Length >= 5)
            let! stillThere = state ()
            Assert.StartsWith("pending:", stillThere)

            // The app recovers: delivered, same event id on every attempt, every signature valid
            w.App.WebhookMode <- FakeApp.Normal
            w.Time.Advance(TimeSpan.FromHours 7.0)
            host.Wake()
            do! waitUntilAsync "delivered" (TimeSpan.FromSeconds 15.0) (fun () -> task { let! s = state () in return s.StartsWith "delivered:" })

            let received = w.App.Received
            Assert.All(received, fun r -> Assert.Equal(eventId, r.EventId))
            Assert.All(received, fun r -> Assert.True(r.SignatureValid, r.SignatureHeader))
            Assert.Equal(200, (List.last received).Answered)
        }

    [<Fact>]
    member _.``contract the webhook carries the envelope, a timestamped HMAC and a Megafono-Signature header``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let! eventId = Seed.enqueueEvent w.Db w.Time "u1" (Seed.followRequested "bob")
            do! waitUntil "the webhook" (TimeSpan.FromSeconds 15.0) (fun () -> not w.App.Received.IsEmpty)

            let r = w.App.Received.Head
            Assert.True r.SignatureValid
            Assert.Matches(@"^t=\d+,v1=[0-9a-f]{64}\z", r.SignatureHeader)
            Assert.Equal($"t={w.Time.GetUtcNow().ToUnixTimeSeconds()}", r.SignatureHeader.Split(',')[0])

            let json = r.Json
            Assert.Equal(eventId, json["id"].GetValue<string>())
            Assert.Matches(@"^evt_[0-9A-HJKMNP-TV-Z]{26}\z", eventId)
            Assert.Equal(1, json["version"].GetValue<int>())
            Assert.Equal("follow.requested", json["type"].GetValue<string>())
            Assert.Equal(1L, json["seq"].GetValue<int64>())
            Assert.Equal("u1", get json "user.id")
            Assert.Equal("alex", get json "user.handle")
            Assert.Equal("@bob@remote.example", get json "actor.handle")
            Assert.Equal("bob", get json "actor.name")
            Assert.Null json["extensions"]
            Assert.StartsWith(w.Time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss"), json["occurredAt"].GetValue<string>())

            // A tampered body or a wrong secret does not verify
            Assert.NotEqual<string>(r.SignatureHeader, Events.signature "other-secret" 1L r.Raw)
        }

    [<Fact>]
    member _.``FR-057 seq increases per user, and a rolled back transaction does not use one``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let! _ = Seed.localActor w.Db "u2" "blake"
            w.App.WebhookMode <- FakeApp.Fail500 // keep them queued

            for name in [ "a"; "b"; "c" ] do
                let! _ = Seed.enqueueEvent w.Db w.Time "u1" (Seed.followRequested name)
                ()

            let! _ = Seed.enqueueEvent w.Db w.Time "u2" (Seed.followRequested "a")

            use! connection = w.Db.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! _ = Outbox.enqueueEvent tx w.Time "u1" (Seed.followRequested "rolled-back")
            do! tx.RollbackAsync()

            let! _ = Seed.enqueueEvent w.Db w.Time "u1" (Seed.followRequested "d")

            let! u1 = status w.Db "SELECT string_agg(seq::text, ',' ORDER BY seq) FROM app_events WHERE local_actor = 'u1'"
            let! u2 = status w.Db "SELECT string_agg(seq::text, ',' ORDER BY seq) FROM app_events WHERE local_actor = 'u2'"
            let! counter = status w.Db "SELECT event_seq FROM local_actors WHERE app_user_id = 'u1'"
            Assert.Equal("1,2,3,4", u1)
            Assert.Equal("1", u2)
            Assert.Equal("4", counter)
        }

    [<Fact>]
    member _.``FR-057 concurrent events for one user get distinct consecutive seq numbers``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            w.App.WebhookMode <- FakeApp.Fail500

            let! _ = Task.WhenAll [| for i in 1..20 -> Seed.enqueueEvent w.Db w.Time "u1" (Seed.followRequested $"p{i}") |]
            let! distinct = Db.count w.Db "SELECT count(DISTINCT seq) FROM app_events WHERE local_actor = 'u1'"
            let! max = Db.count w.Db "SELECT max(seq) FROM app_events WHERE local_actor = 'u1'"
            Assert.Equal(20L, distinct)
            Assert.Equal(20L, max)
        }

    [<Fact>]
    member _.``FR-057 a follow event carries the relationship's status at send time``() : Task =
        task {
            let! w = world pg
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            let! _ = seedUser w
            let remote = Seed.sampleActor "bob"
            let followId = Guid.NewGuid()

            do!
                Db.execute
                    w.Db
                    $"""INSERT INTO remote_actors (id, host, actor_type, inbox, key_id, public_key_pem)
                        VALUES ('{remote.Id}', 'remote.example', 'Person', 'https://remote.example/inbox', '{remote.Id}#k', 'pem');
                        INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
                        VALUES ('{followId}', 'inbound', 'u1', '{remote.Id}', 'https://remote.example/f/1', 'pending')"""

            w.App.WebhookMode <- FakeApp.Fail500

            let body: Events.EventBody =
                { Type = Events.FollowRequested
                  Actor = Some remote
                  Follow = Some { Id = followId; Direction = "inbound"; Status = "pending" }
                  Extra = [] }

            let! _ = Seed.enqueueEvent w.Db w.Time "u1" body
            do! waitUntil "first attempt" (TimeSpan.FromSeconds 15.0) (fun () -> w.App.Received.Length = 1)
            Assert.Equal("pending", get w.App.Received.Head.Json "follow.status")

            // The app approves meanwhile; the retry reports what is true now
            do! Db.execute w.Db $"UPDATE follows SET status = 'accepted' WHERE id = '{followId}'"
            w.App.WebhookMode <- FakeApp.Normal
            w.Time.Advance(TimeSpan.FromMinutes 2.0)
            host.Wake()
            do! waitUntil "retry" (TimeSpan.FromSeconds 15.0) (fun () -> w.App.Received.Length = 2)
            Assert.Equal("accepted", get (List.last w.App.Received).Json "follow.status")
        }

    [<Fact>]
    member _.``FR-014 housekeeping prunes processed activities older than 7 days, at start and on demand``() : Task =
        task {
            let! w = world pg
            do! Database.initializeSchema w.Db
            let now = w.Time.GetUtcNow().UtcDateTime

            let insert (id: string) (age: TimeSpan) =
                Db.execute w.Db $"INSERT INTO processed_activities (activity_id, received_at) VALUES ('{id}', '{(now - age):O}')"

            do! insert "old" (TimeSpan.FromDays 8.0)
            do! insert "recent" (TimeSpan.FromDays 6.0)

            // On demand
            let! removed = Housekeeping.pruneProcessedActivities w.Db w.Time
            Assert.Equal(1, removed)
            let! left = status w.Db "SELECT string_agg(activity_id, ',') FROM processed_activities"
            Assert.Equal("recent", left)

            // At start, using the injected clock
            w.Time.Advance(TimeSpan.FromDays 2.0)
            let! host = startHost w [] None
            use _ = MegafonoHost.disposer host
            do! waitUntilAsync "the start-up prune" (TimeSpan.FromSeconds 10.0) (fun () -> task { let! n = Db.count w.Db "SELECT count(*) FROM processed_activities" in return n = 0L })
        }
