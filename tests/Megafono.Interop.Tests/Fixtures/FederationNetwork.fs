/// The xUnit v3 assembly fixture of the interop suite (R-15): one test PKI, one Docker network, host-port
/// exposure and the `megafono.test` Caddy front for the whole test run, plus lazily started GoToSocial
/// and Mastodon servers (a GoToSocial-only run never starts Mastodon). Test classes take it as a
/// constructor argument: `type MyTests(net: FederationNetwork) = ...`.
///
/// Megáfono (or its test host) attaches to `megafono.test` with `net.AttachHost` (see `FederationContext`
/// for the design: one reserved host port, one lease at a time).
namespace Megafono.Interop.Tests

open System
open System.IO
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Networks
open Xunit
open Megafono.Interop.Tests.FederationContext
open Megafono.Interop.Tests.GoToSocialServer
open Megafono.Interop.Tests.MastodonServer

/// Exclusive ownership of the name `megafono.test` for a test that runs its own containers behind it (the deployment
/// shape test). The name normally belongs to the Caddy front that proxies to the in-process host; while this lease is
/// held that front is stopped, so the test's gateway container can carry the network alias `megafono.test` itself.
/// Disposing it starts the front again and releases the host port lease.
type DomainLease(port: PortLease, front: IContainer) =
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        do! front.StartAsync()
                    finally
                        (port :> IDisposable).Dispose()
                }
            )

type FederationNetwork() =
    let mutable ctx: FederationContext option = None
    let mutable network: INetwork option = None
    let mutable front: IContainer option = None
    let mutable dir = ""
    // Lazy<Task> with ExecutionAndPublication: the first caller starts the server, others await the same task.
    let mutable gts: Lazy<Task<GoToSocialServer>> option = None
    let mutable mastodon: Lazy<Task<MastodonServer>> option = None

    member _.Context = ctx.Value
    member _.Pki = ctx.Value.Pki
    member _.Network = ctx.Value.Network
    member _.HostPort = ctx.Value.HostPort
    member _.Timings = ctx.Value.Timings

    /// GoToSocial, started on first use.
    member _.GoToSocial() : Task<GoToSocialServer> = gts.Value.Value

    /// Mastodon, started on first use (about 30 s).
    member _.Mastodon() : Task<MastodonServer> = mastodon.Value.Value

    /// Starts a Kestrel app reachable as `https://megafono.test` (see `FederationContext.AttachHost`).
    member _.AttachHost(configure) = ctx.Value.AttachHost configure

    /// The host port for a host with its own Kestrel (the real Megáfono); see `FederationContext.PortLease`.
    member _.LeasePort() = ctx.Value.LeasePort()

    /// The name `megafono.test` for a test with its own containers: waits for any host-based test to finish, stops the
    /// Caddy front, and starts it again when the lease is disposed (see `DomainLease`).
    member _.LeaseDomain() : Task<DomainLease> =
        task {
            let! port = ctx.Value.LeasePort()
            do! front.Value.StopAsync()
            return new DomainLease(port, front.Value)
        }

    /// A host that serves `/probe.json` and records every request.
    member _.AttachProbe() = ctx.Value.AttachProbe()

    interface IAsyncLifetime with
        member _.InitializeAsync() =
            ValueTask(
                task {
                    dir <- Path.Combine(Path.GetTempPath(), "megafono-interop-" + Guid.NewGuid().ToString("N"))
                    let pki = TestCa.create dir [ MegafonoDomain; GtsDomain; MastodonDomain ]
                    let port = reserveFreePort ()
                    // Must precede container creation: containers started afterwards can reach the host.
                    let timings = Collections.Concurrent.ConcurrentDictionary<string, TimeSpan>()
                    let sw = Diagnostics.Stopwatch.StartNew()
                    do! TestcontainersSettings.ExposeHostPortsAsync(uint16 port)
                    timings["expose host port"] <- sw.Elapsed

                    let net = NetworkBuilder().Build()
                    network <- Some net
                    do! net.CreateAsync()
                    let c = FederationContext(pki, net, port)
                    ctx <- Some c
                    let f = c.CaddyFront(MegafonoDomain, $"host.testcontainers.internal:{port}")
                    front <- Some f
                    do! c.Time("megafono.test front", f.StartAsync())
                    for kv in timings do
                        c.Timings[kv.Key] <- kv.Value
                    gts <- Some(Lazy<Task<GoToSocialServer>>((fun () -> GoToSocialServer.StartAsync c), LazyThreadSafetyMode.ExecutionAndPublication))
                    mastodon <- Some(Lazy<Task<MastodonServer>>((fun () -> MastodonServer.StartAsync c), LazyThreadSafetyMode.ExecutionAndPublication))
                }
            )

        member _.DisposeAsync() =
            ValueTask(
                task {
                    // Servers that were never started are not started now.
                    match gts with
                    | Some l when l.IsValueCreated ->
                        try
                            let! s = l.Value
                            do! (s :> IAsyncDisposable).DisposeAsync().AsTask()
                        with _ -> ()
                    | _ -> ()
                    match mastodon with
                    | Some l when l.IsValueCreated ->
                        try
                            let! s = l.Value
                            do! (s :> IAsyncDisposable).DisposeAsync().AsTask()
                        with _ -> ()
                    | _ -> ()
                    match front with
                    | Some f -> do! f.DisposeAsync().AsTask()
                    | None -> ()
                    match network with
                    | Some n -> do! n.DisposeAsync().AsTask()
                    | None -> ()
                    if dir <> "" then
                        try Directory.Delete(dir, true) with _ -> ()
                }
            )

[<assembly: AssemblyFixture(typeof<FederationNetwork>)>]
do ()
