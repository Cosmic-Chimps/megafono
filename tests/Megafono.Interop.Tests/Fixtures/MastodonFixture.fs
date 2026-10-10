/// Spike fixture for the Mastodon side of the interop harness (R-15, T007): Postgres, Redis, a pinned
/// Mastodon web and sidekiq in production mode on one network behind a TLS Caddy (`mastodon.test`), the
/// `megafono.test` Caddy in front of the in-process Kestrel host, and a user token made without a browser.
module Megafono.Interop.Tests.MastodonFixture

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Security.Cryptography
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Configurations
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Networks
open Xunit
open Megafono.Interop.Tests.GoToSocialFixture

[<Literal>]
let MastodonImage = "ghcr.io/mastodon/mastodon:v4.7.3"

[<Literal>]
let PostgresImage = "docker.io/library/postgres:17-alpine"

[<Literal>]
let RedisImage = "docker.io/library/redis:7-alpine"

[<Literal>]
let MastodonDomain = "mastodon.test"

let private alnum (length: int) =
    RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", length)

/// The environment shared by web and sidekiq (and the one-shot commands run by exec).
let private mastodonEnv () =
    [ "RAILS_ENV", "production"
      "LOCAL_DOMAIN", MastodonDomain
      "SECRET_KEY_BASE", Convert.ToHexString(RandomNumberGenerator.GetBytes 64).ToLowerInvariant()
      // The three Active Record encryption keys are required since 4.3; any random 32-char strings work.
      "ACTIVE_RECORD_ENCRYPTION_DETERMINISTIC_KEY", alnum 32
      "ACTIVE_RECORD_ENCRYPTION_KEY_DERIVATION_SALT", alnum 32
      "ACTIVE_RECORD_ENCRYPTION_PRIMARY_KEY", alnum 32
      "DB_HOST", "db"
      "DB_PORT", "5432"
      "DB_USER", "mastodon"
      "DB_NAME", "mastodon"
      "DB_PASS", "mastodon-test-pw"
      "REDIS_HOST", "redis"
      "REDIS_PORT", "6379"
      "ES_ENABLED", "false"
      "SINGLE_USER_MODE", "false"
      "AUTHORIZED_FETCH", "true"
      "ALLOWED_PRIVATE_ADDRESSES", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16"
      // Mastodon validates the e-mail domain's MX record in production, which a `.test` domain fails.
      "EMAIL_DOMAIN_ALLOWLIST", MastodonDomain
      "SSL_CERT_FILE", "/certs/ca.pem" ]

let private withEnv (env: (string * string) list) (builder: ContainerBuilder) =
    env |> List.fold (fun (b: ContainerBuilder) (k, v) -> b.WithEnvironment(k, v)) builder

let private tokenScript =
    """user = User.find_by!(email: 'alice@mastodon.test')
app = Doorkeeper::Application.create!(name: 'megafono-harness', redirect_uri: 'urn:ietf:wg:oauth:2.0:oob', scopes: 'read write follow')
token = Doorkeeper::AccessToken.create!(application: app, resource_owner_id: user.id, scopes: 'read write follow', expires_in: nil)
puts "TOKEN=#{token.token}"
"""

let private email = "alice@mastodon.test"

[<Literal>]
let private SidekiqReadyFile = "/opt/mastodon/tmp/sidekiq_process_has_started_and_will_begin_processing_jobs"

let private waitForHealth (client: HttpClient) (timeout: TimeSpan) =
    task {
        let deadline = DateTime.UtcNow + timeout
        let mutable ready = false
        let mutable last = ""
        while not ready do
            try
                let! resp = client.GetAsync $"https://{MastodonDomain}/health"
                ready <- resp.IsSuccessStatusCode
                last <- string (int resp.StatusCode)
            with e -> last <- e.Message
            if not ready then
                if DateTime.UtcNow > deadline then failwith $"Mastodon did not become healthy; last: {last}"
                do! Task.Delay 1000
    }

type MastodonFixture() =
    let mutable pki: TestCa.TestPki option = None
    let mutable probe: ProbeHost option = None
    let mutable network: INetwork option = None
    let mutable containers: IContainer list = []
    let mutable web: IContainer option = None
    let mutable front: IContainer option = None
    let mutable token = ""
    let timings = ConcurrentDictionary<string, TimeSpan>()

    member _.Pki = pki.Value
    member _.Probe = probe.Value
    member _.AccessToken = token
    member _.Timings = timings
    member _.Username = "alice"

    member _.Logs(container: IContainer) =
        task {
            let! stdout, stderr = container.GetLogsAsync()
            return stdout + stderr
        }

    member _.WebLogs() =
        task {
            let! stdout, stderr = web.Value.GetLogsAsync()
            return stdout + stderr
        }

    /// A client for `https://mastodon.test` (through the Caddy front's mapped port) with the bearer token set.
    member this.AuthedClient() : HttpClient =
        let c = TestCa.httpClient pki.Value (Some(front.Value.Hostname, int (front.Value.GetMappedPublicPort 443)))
        c.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
        c

    interface IAsyncLifetime with
        member this.InitializeAsync() =
            ValueTask(
                task {
                    let dir = IO.Path.Combine(IO.Path.GetTempPath(), "megafono-interop-" + Guid.NewGuid().ToString("N"))
                    pki <- Some(TestCa.create dir [ MegafonoDomain; MastodonDomain ])
                    let! p = startProbeHost ()
                    probe <- Some p
                    do! timed timings "expose host port" (TestcontainersSettings.ExposeHostPortsAsync(uint16 p.Port))

                    let net = NetworkBuilder().Build()
                    network <- Some net
                    do! net.CreateAsync()
                    let env = mastodonEnv ()
                    let caMapping (b: ContainerBuilder) = b.WithResourceMapping(bytes pki.Value.CaPem, "/certs/ca.pem")

                    let postgres =
                        ContainerBuilder(PostgresImage)
                            .WithNetwork(net)
                            .WithNetworkAliases("db")
                            .WithEnvironment("POSTGRES_USER", "mastodon")
                            .WithEnvironment("POSTGRES_PASSWORD", "mastodon-test-pw")
                            .WithEnvironment("POSTGRES_DB", "mastodon")
                            // TCP on 127.0.0.1 only answers once the real server is up (the init server is socket-only).
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "mastodon"))
                            .Build()
                    let redis =
                        ContainerBuilder(RedisImage)
                            .WithNetwork(net)
                            .WithNetworkAliases("redis")
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
                            .Build()

                    // db:setup loads the schema (about 12 s), then puma replaces the shell.
                    let w =
                        ContainerBuilder(MastodonImage)
                            .WithNetwork(net)
                            .WithNetworkAliases("web")
                            .WithCommand("sh", "-c", "bundle exec rails db:setup && exec bundle exec puma -C config/puma.rb")
                            .WithResourceMapping(bytes tokenScript, "/tmp/token.rb")
                            |> withEnv env
                            |> caMapping
                    let web' = w.Build()
                    web <- Some web'

                    let sidekiq =
                        (ContainerBuilder(MastodonImage)
                            .WithNetwork(net)
                            .WithNetworkAliases("sidekiq")
                            .WithCommand("bundle", "exec", "sidekiq")
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("test", "-f", SidekiqReadyFile))
                         |> withEnv env
                         |> caMapping)
                            .Build()

                    let mastodonFront = caddyFront net pki.Value MastodonDomain "web:3000"
                    front <- Some mastodonFront
                    let megafonoFront = caddyFront net pki.Value MegafonoDomain $"host.testcontainers.internal:{p.Port}"
                    containers <- [ megafonoFront; mastodonFront; sidekiq; web'; redis; postgres ]

                    do! timed timings "start postgres+redis+caddy x2" (Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), mastodonFront.StartAsync(), megafonoFront.StartAsync()))
                    do! web'.StartAsync()
                    let c = TestCa.httpClient pki.Value (Some(mastodonFront.Hostname, int (mastodonFront.GetMappedPublicPort 443)))
                    do! timed timings "web healthy (db:setup + boot)" (waitForHealth c (TimeSpan.FromSeconds 240.0))
                    do! timed timings "sidekiq ready" (sidekiq.StartAsync())

                    let exec (args: string list) =
                        task {
                            let! r = web'.ExecAsync(Array.ofList args)
                            if r.ExitCode <> Nullable 0L then
                                failwith $"exec {String.Join(' ', args)} failed with {r.ExitCode}: {r.Stderr} {r.Stdout}"
                            return r
                        }
                    let sw = Diagnostics.Stopwatch.StartNew()
                    let! _ = exec [ "bin/tootctl"; "accounts"; "create"; "alice"; "--email"; email; "--confirmed"; "--approve" ]
                    let! tokenRun = exec [ "bin/rails"; "runner"; "/tmp/token.rb" ]
                    timings["account + token"] <- sw.Elapsed
                    let m = Regex.Match(tokenRun.Stdout, "TOKEN=(\\S+)")
                    if not m.Success then failwith $"no token in: {tokenRun.Stdout} {tokenRun.Stderr}"
                    token <- m.Groups[1].Value
                }
            )

        member this.DisposeAsync() =
            ValueTask(
                task {
                    for c in containers do
                        do! c.DisposeAsync().AsTask()
                    match network with
                    | Some n -> do! n.DisposeAsync().AsTask()
                    | None -> ()
                    match probe with
                    | Some p -> do! (p :> IAsyncDisposable).DisposeAsync().AsTask()
                    | None -> ()
                    match pki with
                    | Some k -> try IO.Directory.Delete(k.Directory, true) with _ -> ()
                    | None -> ()
                }
            )
