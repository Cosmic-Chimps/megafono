/// The shared pieces every fake server needs: the test PKI, the Docker network, the Caddy-front helper
/// and the host-side attachment point for `megafono.test` (R-15).
///
/// How Megáfono's host attaches to `megafono.test`: the fixture reserves one free host port up front,
/// exposes it with `TestcontainersSettings.ExposeHostPortsAsync` (which must precede container creation)
/// and points the `megafono.test` Caddy at `host.testcontainers.internal:<port>`. A test (or the later
/// Megáfono test host) calls `AttachHost`, which starts a Kestrel app bound to exactly that port and
/// hands back a lease. Only one lease exists at a time (the port is single and the tests run in
/// parallel), so `AttachHost` waits until the previous lease is disposed: tests that attach a host are
/// serialized, everything else (user creation, searches on the fake servers) is not.
module Megafono.Interop.Tests.FederationContext

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Networks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Server.Kestrel.Core
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Megafono.Interop.Tests.FediClient

// Pinned images (R-15). Mastodon and GoToSocial are pinned in their own modules.
[<Literal>]
let CaddyImage = "docker.io/library/caddy:2.11.7-alpine"

[<Literal>]
let CurlImage = "docker.io/curlimages/curl:8.22.0"

[<Literal>]
let GtsDomain = "gts.test"

[<Literal>]
let MastodonDomain = "mastodon.test"

[<Literal>]
let MegafonoDomain = "megafono.test"

let bytes (s: string) : byte[] = Encoding.UTF8.GetBytes s

/// A request that reached the host-side app, recorded so tests can assert on what a fake server sent.
type SeenRequest =
    { Method: string
      PathAndQuery: string
      Host: string
      UserAgent: string }

/// Something a test can ask a fake server for: a domain and users with tokens.
type IFediServer =
    abstract Domain: string
    /// Creates a user (a fresh name is derived from `prefix`) and returns a client logged in as it.
    abstract CreateUser: prefix: string -> Task<FediClient>

/// `prefix` plus five random lowercase alphanumerics: valid on both servers, unique per test.
let uniqueName (prefix: string) =
    prefix + RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 5)

/// The single app attached to the `megafono.test` front. Disposing it stops Kestrel and frees the port.
type HostLease(app: WebApplication, seen: ConcurrentQueue<SeenRequest>, release: unit -> unit) =
    member _.App = app
    /// Every request the app received, in order, whatever the path.
    member _.Seen = seen
    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    try
                        do! app.StopAsync()
                        do! app.DisposeAsync()
                    finally
                        release ()
                }
            )

/// Exclusive use of the host port that `megafono.test` proxies to, for a host that builds its own Kestrel
/// (the real Megáfono app) instead of the minimal one `AttachHost` creates. Dispose it after the host has stopped.
type PortLease(port: int, release: unit -> unit) =
    member _.Port = port

    interface IDisposable with
        member _.Dispose() = release ()

let private caddyfile (domain: string) (upstream: string) =
    $"""{{
	auto_https off
	admin off
}}
https://{domain} {{
	tls /certs/{domain}.crt.pem /certs/{domain}.key.pem
	reverse_proxy {upstream}
}}
"""

type FederationContext(pki: TestCa.TestPki, network: INetwork, hostPort: int) =
    let hostGate = new SemaphoreSlim(1, 1)
    let timings = ConcurrentDictionary<string, TimeSpan>()

    member _.Pki = pki
    member _.Network = network
    /// The host port that `megafono.test` proxies to; `AttachHost` binds Kestrel to it.
    member _.HostPort = hostPort
    member _.Timings = timings

    /// A TLS-terminating Caddy with network alias `domain`, serving the domain's leaf certificate and
    /// reverse-proxying to `upstream` (`host:port`, plain HTTP) with the Host preserved (Caddy's default).
    /// Caddy also sets `X-Forwarded-Proto: https`, which Mastodon needs in production.
    member _.CaddyFront(domain: string, upstream: string) : IContainer =
        let leaf = pki.Leaf domain
        ContainerBuilder(CaddyImage)
            .WithNetwork(network)
            .WithNetworkAliases(domain)
            .WithPortBinding(443, true)
            .WithResourceMapping(bytes (caddyfile domain upstream), "/etc/caddy/Caddyfile")
            .WithResourceMapping(bytes leaf.CertPem, $"/certs/{domain}.crt.pem")
            .WithResourceMapping(bytes leaf.KeyPem, $"/certs/{domain}.key.pem")
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("serving initial configuration"))
            .Build()

    /// An HttpClient that trusts only the test CA and reaches `front` through its mapped port while
    /// keeping the fake domain in the URL (SNI and `Host`). Redirects are not followed; cookies are kept.
    member _.HttpClientFor(front: IContainer) : Net.Http.HttpClient =
        TestCa.httpClient pki (Some(front.Hostname, int (front.GetMappedPublicPort 443)))

    /// Records how long a startup step took (shown in the research notes and useful when CI is slow).
    member _.Time(name: string, work: Task) =
        task {
            let sw = Diagnostics.Stopwatch.StartNew()
            do! work
            timings[name] <- sw.Elapsed
        }

    /// Starts a Kestrel app on `0.0.0.0:HostPort` (so `https://megafono.test/...` lands on it). `configure`
    /// maps routes; every request is also recorded. Waits for the previous lease to be disposed.
    member _.AttachHost(configure: WebApplication -> unit) : Task<HostLease> =
        task {
            let! acquired = hostGate.WaitAsync(TimeSpan.FromMinutes 5.0)
            if not acquired then
                failwith "timed out waiting for the megafono.test host to be released by another test"
            try
                let builder = WebApplication.CreateSlimBuilder()
                builder.WebHost.UseKestrel(fun (o: KestrelServerOptions) -> o.Listen(IPAddress.Any, hostPort)) |> ignore
                builder.Logging.ClearProviders() |> ignore
                let app = builder.Build()
                let seen = ConcurrentQueue<SeenRequest>()
                app.Use(
                    Func<HttpContext, Func<Task>, Task>(fun ctx next ->
                        seen.Enqueue
                            { Method = ctx.Request.Method
                              PathAndQuery = ctx.Request.Path.Value + ctx.Request.QueryString.Value
                              Host = ctx.Request.Host.Value
                              UserAgent = string ctx.Request.Headers.UserAgent }
                        next.Invoke())
                )
                |> ignore
                configure app
                do! app.StartAsync()
                return HostLease(app, seen, (fun () -> hostGate.Release() |> ignore))
            with e ->
                hostGate.Release() |> ignore
                return raise e
        }

    /// Takes the host port for a host with its own Kestrel (see `PortLease`). Waits for the previous lease,
    /// whether from this method or from `AttachHost`.
    member _.LeasePort() : Task<PortLease> =
        task {
            let! acquired = hostGate.WaitAsync(TimeSpan.FromMinutes 5.0)
            if not acquired then
                failwith "timed out waiting for the megafono.test host port to be released by another test"
            return new PortLease(hostPort, (fun () -> hostGate.Release() |> ignore))
        }

    /// A host that serves `GET /probe.json` -> `{"ok":true}` and 404 for everything else.
    member this.AttachProbe() : Task<HostLease> =
        this.AttachHost(fun app ->
            app.Run(
                RequestDelegate(fun ctx ->
                    if ctx.Request.Path.Value = "/probe.json" then
                        ctx.Response.ContentType <- "application/json"
                        ctx.Response.WriteAsync """{"ok":true}"""
                    else
                        ctx.Response.StatusCode <- 404
                        Task.CompletedTask)
            ))

/// A free TCP port on the host. There is a small window between releasing it and Kestrel binding it.
let reserveFreePort () =
    use listener = new TcpListener(IPAddress.Loopback, 0)
    listener.Start()
    (listener.LocalEndpoint :?> IPEndPoint).Port
