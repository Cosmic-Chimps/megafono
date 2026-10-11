/// Delivery retries, giving up, and host reachability, driven by the fake clock (FR-051, FR-052, FR-054)
module Megafono.Tests.Integration.DeliveryTests

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

type private Row =
    { Status: string
      Attempts: int
      LastError: string option
      NextAttemptAt: DateTimeOffset }

/// The single delivery of an object
let private delivery (rig: Rig) (objectId: string) : Task<Row option> =
    task {
        use command =
            rig.Db.CreateCommand
                """SELECT d.status, d.attempts, d.last_error, d.next_attempt_at
                   FROM deliveries d JOIN activities a ON a.id = d.activity WHERE a.object = @id"""

        command.Parameters.AddWithValue("id", Guid.Parse objectId) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { Status = reader.GetString 0
                      Attempts = reader.GetInt32 1
                      LastError = if reader.IsDBNull 2 then None else Some(reader.GetString 2)
                      NextAttemptAt = reader.GetFieldValue<DateTimeOffset> 3 }
        else
            return None
    }

let private waitForDelivery (rig: Rig) (objectId: string) (what: string) (predicate: Row -> bool) : Task =
    waitUntilAsync what (TimeSpan.FromSeconds 15.0) (fun () ->
        task {
            let! row = delivery rig objectId
            return row |> Option.exists predicate
        })

let private failedEvents (rig: Rig) =
    rig.App.Received |> List.filter (fun w -> w.Type = "delivery.failed")

/// The event ids of what the app received are unique: wait for one about this object
let private waitForFailureEvent (rig: Rig) (objectId: string) : Task<FakeApp.ReceivedWebhook> =
    task {
        let url = $"https://megafono.test/ap/objects/{objectId}"
        let find () = failedEvents rig |> List.tryFind (fun w -> jstr ((at w.Json "object.id")) = url)

        do!
            waitUntilAsync "the delivery.failed event" (TimeSpan.FromSeconds 15.0) (fun () ->
                rig.Host.Wake()
                Task.FromResult((find ()).IsSome))

        return (find ()).Value
    }

let private hostRow (rig: Rig) =
    task {
        use command = rig.Db.CreateCommand "SELECT first_failure_at, unreachable_since FROM remote_hosts"
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(not (reader.IsDBNull 0), not (reader.IsDBNull 1)) else None
    }

let private deliveriesOf (rig: Rig) (objectId: string) : Task<JsonNode> =
    task {
        let! status, text = sendInternal rig HttpMethod.Get $"/internal/objects/{objectId}/deliveries" None
        Assert.Equal(200, status)
        return json text
    }

type DeliveryTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-051 a down server is retried on the backoff schedule and given up after 48 hours with delivery.failed``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            rig.Stub.InboxMode <- StubRemote.Fail500
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let! id = publish rig "u-1" "public" "<p>hello</p>"
            let created = rig.Time.GetUtcNow()
            do! waitForDelivery rig id "the first failure" (fun r -> r.Attempts = 1 && r.Status = "pending")

            // FR-054: while it retries, the status shows the host, the attempts and the last error
            let! report = deliveriesOf rig id
            let entry = (at report "activities.0.deliveries.0")
            Assert.Equal(rig.Stub.Authority, jstr ((at entry "host")))
            Assert.Equal("pending", jstr ((at entry "status")))
            Assert.Equal(1, intOf ((at entry "attempts")))
            Assert.Equal("HTTP 500", jstr ((at entry "lastError")))
            Assert.False(isNull (at entry "nextAttemptAt"))

            let mutable attempts = 1
            let mutable finished = false

            while not finished do
                Assert.True(attempts < 30, "the delivery never gave up")
                let! row = delivery rig id
                let row = row.Value
                // The delay after attempt k is min(6 h, 1 min × 2^(k−1)) plus up to 10 % jitter
                let expected = min (TimeSpan.FromHours 6.0) (TimeSpan.FromMinutes(Math.Pow(2.0, float (attempts - 1))))
                let delay = row.NextAttemptAt - rig.Time.GetUtcNow()
                Assert.InRange(delay, expected - TimeSpan.FromSeconds 1.0, expected * 1.1 + TimeSpan.FromSeconds 1.0)

                rig.Time.Set(row.NextAttemptAt + TimeSpan.FromSeconds 1.0)
                rig.Host.Wake()
                attempts <- attempts + 1
                do! waitForDelivery rig id $"attempt {attempts}" (fun r -> r.Attempts = attempts && r.Status <> "sending")
                let! after = delivery rig id
                finished <- after.Value.Status = "failed"

            let! final = delivery rig id
            Assert.Equal("failed", final.Value.Status)
            Assert.Equal(Some "HTTP 500", final.Value.LastError)
            // It gave up only after the 48 hour deadline, and sent exactly as many requests as attempts
            Assert.True(rig.Time.GetUtcNow() - created > TimeSpan.FromHours 48.0)
            Assert.Equal(attempts, rig.Stub.Posts.Length)

            // The app is told once
            let! event = waitForFailureEvent rig id
            Assert.Equal(rig.Stub.Authority, jstr ((at event.Json "delivery.host")))
            Assert.Equal(attempts, intOf ((at event.Json "delivery.attempts")))
            Assert.Equal("HTTP 500", jstr ((at event.Json "delivery.lastError")))
            Assert.Equal("u-1", jstr ((at event.Json "user.id")))
            do! Task.Delay 300
            Assert.Single(failedEvents rig) |> ignore

            let! finalReport = deliveriesOf rig id
            Assert.Equal("failed", jstr ((at finalReport "activities.0.deliveries.0.status")))
            Assert.True(isNull (at finalReport "activities.0.deliveries.0.nextAttemptAt"))
        }

    [<Fact>]
    member _.``FR-052 a host that fails for 7 days is unreachable, new deliveries are skipped with delivery.failed, one success clears it``() : Task =
        task {
            let! rig = start pg [ "alice" ]
            use _ = disposeRig rig
            rig.Stub.InboxMode <- StubRemote.Fail500
            do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
            let t0 = rig.Time.GetUtcNow()

            // Day 0: the first failure starts the clock
            let! o1 = publish rig "u-1" "public" "<p>one</p>"
            do! waitForDelivery rig o1 "o1 first failure" (fun r -> r.Attempts = 1 && r.Status = "pending")
            let! host = hostRow rig
            Assert.Equal(Some(true, false), host)

            // Day 6: still failing, but not for 7 days yet. o1 gives up (its 48 hours are over); o2 starts.
            rig.Time.Set(t0 + TimeSpan.FromDays 6.0)
            rig.Host.Wake()
            do! waitForDelivery rig o1 "o1 gives up" (fun r -> r.Status = "failed")
            let! o2 = publish rig "u-1" "public" "<p>two</p>"
            do! waitForDelivery rig o2 "o2 first failure" (fun r -> r.Attempts = 1 && r.Status = "pending")
            let! host = hostRow rig
            Assert.Equal(Some(true, false), host)

            // Day 7 and an hour: o2's retry fails again, and now every attempt for 7 days has failed
            rig.Time.Set(t0 + TimeSpan.FromDays 7.0 + TimeSpan.FromHours 1.0)
            rig.Host.Wake()
            do! waitForDelivery rig o2 "o2 second failure" (fun r -> r.Attempts >= 2)

            do!
                waitUntilAsync "the host is marked unreachable" (TimeSpan.FromSeconds 15.0) (fun () ->
                    task {
                        let! host = hostRow rig
                        return host = Some(true, true)
                    })

            // A new delivery is skipped without a request, and the app hears of it
            let postsBefore = rig.Stub.Posts.Length
            let! o3 = publish rig "u-1" "public" "<p>three</p>"
            do! waitForDelivery rig o3 "o3 skipped" (fun r -> r.Status = "skipped")
            let! skipped = delivery rig o3
            Assert.Contains("unreachable", skipped.Value.LastError.Value)
            Assert.Equal(0, skipped.Value.Attempts - 1) // claimed once, never sent
            let! event = waitForFailureEvent rig o3
            Assert.Equal(rig.Stub.Authority, jstr ((at event.Json "delivery.host")))
            Assert.Contains("unreachable", jstr ((at event.Json "delivery.lastError")))
            Assert.True(rig.Stub.Posts.Length <= postsBefore + 0, "no request was made for the skipped delivery")

            let! report = deliveriesOf rig o3
            Assert.Equal("skipped", jstr ((at report "activities.0.deliveries.0.status")))
            Assert.Equal(rig.Stub.Authority, jstr ((at report "activities.0.deliveries.0.host")))

            // The server comes back. An hour later one delivery is let through to find out, and its success clears the mark.
            rig.Stub.InboxMode <- StubRemote.Accept
            rig.Time.Advance(TimeSpan.FromMinutes 61.0)
            rig.Host.Wake()
            do! waitForDelivery rig o2 "o2 delivered by the probe" (fun r -> r.Status = "delivered")

            do!
                waitUntilAsync "the host is reachable again" (TimeSpan.FromSeconds 15.0) (fun () ->
                    task {
                        let! host = hostRow rig
                        return host = Some(false, false)
                    })

            // New deliveries go out normally again
            let! o4 = publish rig "u-1" "public" "<p>four</p>"
            do! waitForDelivery rig o4 "o4 delivered" (fun r -> r.Status = "delivered")
            let! final = deliveriesOf rig o1
            Assert.Equal("failed", jstr ((at final "activities.0.deliveries.0.status")))
            let! o4Report = deliveriesOf rig o4
            Assert.Equal("delivered", jstr ((at o4Report "activities.0.deliveries.0.status")))
        }
