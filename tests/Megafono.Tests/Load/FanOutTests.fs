/// SC-003: one post to 1,000 followers on 200 servers reaches every server within 2 minutes.
///
/// The fleet is one in-process Kestrel listening on 200 loopback ports; `127.0.0.1:<port>` is the lowercase
/// authority the delivery worker keys hosts by, so each port counts as its own host for the per-host limit and
/// for shared-inbox grouping. Every inbox answers 202 after 20-50 ms and verifies the draft-cavage signature and
/// digest of what it receives.
///
/// The target is a 2 vCPU host. A process cannot pin its own CPUs portably, so run it as CI does:
///
///     DOTNET_PROCESSOR_COUNT=2 dotnet test --project tests/Megafono.Tests -- --filter-trait "Suite=load"
///
/// (the runtime then sees two processors: thread pool and GC are sized for two), or inside Docker with `--cpus=2`.
/// The test prints `Environment.ProcessorCount` and the measured fan-out times. It is excluded from the default
/// run by `--filter-not-trait "Suite=load"`.
module Megafono.Tests.Load.FanOutTests

open System
open System.Collections.Concurrent
open System.Net
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private hostCount = 200
let private followersPerHost = 5
let private deadline = TimeSpan.FromMinutes 2.0

/// The fleet: counts what it receives per port and the most requests it ever had open, per port and overall
type private Fleet(authorKey: RSA) =
    let ports = ResizeArray<int>()
    let received = ConcurrentDictionary<int, int>()
    let open' = ConcurrentDictionary<int, int>()
    let mutable openTotal = 0
    let mutable maxTotal = 0
    let mutable maxPerHost = 0
    let mutable invalid = 0
    let mutable app: WebApplication = null
    let rand = Random(7)

    let raise' (current: byref<int>) (value: int) =
        let mutable seen = current

        while value > seen && Interlocked.CompareExchange(&current, value, seen) <> seen do
            seen <- current

    let handle (ctx: HttpContext) : Task =
        task {
            let port = ctx.Connection.LocalPort
            let total = Interlocked.Increment &openTotal
            raise' &maxTotal total
            let perHost = open'.AddOrUpdate(port, 1, fun _ n -> n + 1)
            raise' &maxPerHost perHost

            try
                use buffer = new IO.MemoryStream()
                do! ctx.Request.Body.CopyToAsync buffer

                let request: SignedRequest =
                    { Method = ctx.Request.Method
                      PathAndQuery = ctx.Features.Get<IHttpRequestFeature>().RawTarget
                      Headers = [ for h in ctx.Request.Headers -> h.Key, h.Value.ToString() ]
                      Body = buffer.ToArray() }

                match draftCavage.Verify TimeProvider.System authorKey request with
                | Ok() -> received.AddOrUpdate(port, 1, fun _ n -> n + 1) |> ignore
                | Error _ -> Interlocked.Increment &invalid |> ignore

                do! Task.Delay(lock rand (fun () -> rand.Next(20, 51)))
                ctx.Response.StatusCode <- 202
            finally
                Interlocked.Decrement &openTotal |> ignore
                open'.AddOrUpdate(port, 0, fun _ n -> n - 1) |> ignore
        }

    member _.Ports = ports |> Seq.toList
    member _.ReceivedBy(port: int) = match received.TryGetValue port with | true, n -> n | _ -> 0
    member _.HostsReached = received.Count
    member _.TotalReceived = received.Values |> Seq.sum
    member _.MaxOpenTotal = maxTotal
    member _.MaxOpenPerHost = maxPerHost
    member _.Invalid = invalid

    member _.Start() : Task =
        task {
            let builder = WebApplication.CreateBuilder()
            builder.Logging.ClearProviders() |> ignore

            builder.WebHost.ConfigureKestrel(fun options ->
                options.Limits.MaxConcurrentConnections <- Nullable()
                for _ in 1..hostCount do options.Listen(IPAddress.Loopback, 0))
            |> ignore

            app <- builder.Build()
            app.Run(RequestDelegate handle)
            do! app.StartAsync()

            for address in app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>().Addresses do
                ports.Add(Uri(address).Port)
        }

    interface IAsyncDisposable with
        member _.DisposeAsync() = if isNull app then ValueTask() else ValueTask(app.DisposeAsync().AsTask())

/// 1,000 accepted followers of u-1, five per host; `shared` gives each host a shared inbox
let private seedFollowers (db: NpgsqlDataSource) (ports: int list) (shared: bool) : Task =
    task {
        let rows =
            [ for hostIndex, port in List.indexed ports do
                  for n in 1..followersPerHost -> hostIndex * followersPerHost + n, port ]

        use command =
            db.CreateCommand
                """INSERT INTO remote_actors (id, host, actor_type, inbox, shared_inbox, key_id, public_key_pem)
                   SELECT 'http://127.0.0.1:' || p || '/users/f' || n, '127.0.0.1:' || p, 'Person',
                          'http://127.0.0.1:' || p || '/users/f' || n || '/inbox',
                          CASE WHEN @shared THEN 'http://127.0.0.1:' || p || '/inbox' END,
                          'http://127.0.0.1:' || p || '/users/f' || n || '#main-key', 'pem'
                   FROM unnest(@ns, @ports) AS t(n, p);
                   INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status)
                   SELECT gen_random_uuid(), 'inbound', 'u-1', 'http://127.0.0.1:' || p || '/users/f' || n, 'f' || n, 'accepted'
                   FROM unnest(@ns, @ports) AS t(n, p)"""

        command.Parameters.AddWithValue("shared", shared) |> ignore
        command.Parameters.AddWithValue("ns", rows |> List.map fst |> Array.ofList) |> ignore
        command.Parameters.AddWithValue("ports", rows |> List.map snd |> Array.ofList) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

[<Trait("Suite", "load")>]
type FanOutTests(pg: PostgresFixture, output: Xunit.ITestOutputHelper) =

    /// Runs one variant and returns the time from the publish answer to the last expected request
    let run (shared: bool) : Task<TimeSpan> =
        task {
            let! connectionString = pg.CreateDatabase()
            // The production pool is 100; the fixture's small one would throttle 32 concurrent deliveries
            let builder = NpgsqlConnectionStringBuilder(connectionString)
            builder.MaxPoolSize <- 100
            let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
            app.AddUser(FakeApp.user "u-1" "alex")
            let time = FakeTime()
            let! host = MegafonoHost.start (MegafonoHost.settings builder.ConnectionString app.Url time)
            use _ = MegafonoHost.disposer host
            use client = host.FederationClient()
            use! actor = client.GetAsync "/ap/actors/u-1"
            actor.EnsureSuccessStatusCode() |> ignore

            // The fleet verifies against the author's real public key
            let! pem = Db.scalar host.DataSource "SELECT public_key_pem FROM local_actors WHERE app_user_id = 'u-1'"
            use authorKey = KeyVault.importPublicPem (string pem)
            let fleet = new Fleet(authorKey)
            use _ = { new IDisposable with member _.Dispose() = (fleet :> IAsyncDisposable).DisposeAsync().AsTask().Wait() }
            do! fleet.Start()
            Assert.Equal(hostCount, fleet.Ports.Length)
            do! seedFollowers host.DataSource fleet.Ports shared

            let expectedRequests = if shared then hostCount else hostCount * followersPerHost
            let rig: Publishing.Rig = { App = app; Host = host; Db = host.DataSource; Time = time; Stub = Unchecked.defaultof<_> }

            let clock = Diagnostics.Stopwatch.StartNew()
            let! id = Publishing.publish rig "u-1" "followers" "<p>to every follower</p>"
            let publishedAfter = clock.Elapsed
            let! queued = Db.count host.DataSource "SELECT count(*) FROM deliveries"
            Assert.Equal(int64 expectedRequests, queued)

            do!
                waitUntil $"{expectedRequests} requests" deadline (fun () -> fleet.TotalReceived + fleet.Invalid >= expectedRequests)

            let elapsed = clock.Elapsed
            let variant = if shared then "shared inboxes" else "personal inboxes"

            let report =
                $"fan-out ({variant}): {expectedRequests} requests to {fleet.HostsReached} hosts in {elapsed.TotalSeconds:F1} s (publish answered after {publishedAfter.TotalSeconds:F2} s); processors {Environment.ProcessorCount}; max open per host {fleet.MaxOpenPerHost}, overall {fleet.MaxOpenTotal}"

            output.WriteLine report
            // Also in a file next to the test assembly: a passing test's output is otherwise not shown by `dotnet test`
            IO.File.AppendAllText(IO.Path.Combine(AppContext.BaseDirectory, "fanout-report.txt"), report + Environment.NewLine)

            Assert.Equal(0, fleet.Invalid)
            Assert.Equal(hostCount, fleet.HostsReached)
            Assert.True(elapsed < deadline, $"fan-out took {elapsed}")
            Assert.All(fleet.Ports, fun port -> Assert.Equal(expectedRequests / hostCount, fleet.ReceivedBy port))
            // FR-053: never more than 2 at one host, nor 32 overall
            Assert.True(fleet.MaxOpenPerHost <= 2, $"per-host concurrency reached {fleet.MaxOpenPerHost}")
            Assert.True(fleet.MaxOpenTotal <= 32, $"global concurrency reached {fleet.MaxOpenTotal}")

            // Every delivery is recorded as delivered (the completion writes follow the requests)
            do!
                waitUntilAsync "every delivery recorded" (TimeSpan.FromSeconds 30.0) (fun () ->
                    task {
                        let! delivered = Db.count host.DataSource $"SELECT count(*) FROM deliveries WHERE status = 'delivered'"
                        return delivered = int64 expectedRequests
                    })

            ignore id
            return elapsed
        }

    [<Fact>]
    member _.``SC-003 one post to 1000 followers on 200 servers with shared inboxes reaches every server within 2 minutes``() : Task =
        task {
            let! _ = run true
            ()
        }

    [<Fact>]
    member _.``SC-003 the same fan-out without shared inboxes (1000 deliveries) stays within 2 minutes``() : Task =
        task {
            let! _ = run false
            ()
        }
