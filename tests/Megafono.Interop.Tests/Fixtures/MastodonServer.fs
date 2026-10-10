/// Mastodon for the interop harness (R-15, T007): Postgres, Redis, a pinned Mastodon web and sidekiq in
/// production mode on the shared network behind a TLS Caddy aliased `mastodon.test`; users created with
/// `tootctl accounts create` and tokens minted with a `rails runner` script (no browser).
module Megafono.Interop.Tests.MastodonServer

open System
open System.Net.Http
open System.Security.Cryptography
open System.Text.RegularExpressions
open System.Threading
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

[<Literal>]
let MastodonImage = "ghcr.io/mastodon/mastodon:v4.7.3"

[<Literal>]
let PostgresImage = "docker.io/library/postgres:17-alpine"

[<Literal>]
let RedisImage = "docker.io/library/redis:7-alpine"

[<Literal>]
let private SidekiqReadyFile = "/opt/mastodon/tmp/sidekiq_process_has_started_and_will_begin_processing_jobs"

let private alnum (length: int) =
    RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", length)

/// The environment shared by web and sidekiq (exec'd one-shot commands inherit it from web).
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
      // Only the test CA: nothing in the harness needs the system roots.
      "SSL_CERT_FILE", "/certs/ca.pem" ]

let private withEnv (env: (string * string) list) (builder: ContainerBuilder) =
    env |> List.fold (fun (b: ContainerBuilder) (k, v) -> b.WithEnvironment(k, v)) builder

/// `rails runner /tmp/user.rb <username>`: creates the user with tootctl's own code path (one Rails boot
/// instead of two) and prints a Doorkeeper token (non-expiring, `read write follow`) for it.
let private userScript =
    """require 'mastodon/cli/accounts'
name = ARGV.fetch(0)
Mastodon::CLI::Accounts.new.invoke(:create, [name], email: "#{name}@mastodon.test", confirmed: true, approve: true)
user = User.find_by!(email: "#{name}@mastodon.test")
app = Doorkeeper::Application.create!(name: 'megafono-harness', redirect_uri: 'urn:ietf:wg:oauth:2.0:oob', scopes: 'read write follow')
token = Doorkeeper::AccessToken.create!(application: app, resource_owner_id: user.id, scopes: 'read write follow', expires_in: nil)
puts "TOKEN=#{token.token}"
"""

/// `/health` is exempt from `force_ssl`, and puma only starts after `db:setup` has loaded the schema.
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

type MastodonServer private (ctx: FederationContext, web: IContainer, containers: IContainer list, front: IContainer) =
    let createGate = new SemaphoreSlim(1, 1)

    member _.Front = front

    member _.WebLogs() =
        task {
            let! stdout, stderr = web.GetLogsAsync()
            return stdout + stderr
        }

    member private _.Exec(args: string list) =
        task {
            let! r = web.ExecAsync(Array.ofList args)
            if r.ExitCode <> Nullable 0L then
                failwith $"exec {String.Join(' ', args)} failed with {r.ExitCode}: {r.Stderr} {r.Stdout}"
            return r
        }

    member this.CreateUser(prefix: string) : Task<FediClient> =
        task {
            let name = uniqueName prefix
            do! createGate.WaitAsync()
            let! token =
                task {
                    try
                        let! run = this.Exec [ "bin/rails"; "runner"; "/tmp/user.rb"; name ]
                        // stdout also carries Sidekiq log lines.
                        let m = Regex.Match(run.Stdout, "TOKEN=(\\S+)")
                        if not m.Success then failwith $"no token in: {run.Stdout} {run.Stderr}"
                        return m.Groups[1].Value
                    finally
                        createGate.Release() |> ignore
                }
            return new FediClient(ctx.HttpClientFor front, $"https://{MastodonDomain}", token, name, MastodonDomain)
        }

    interface IFediServer with
        member _.Domain = MastodonDomain
        member this.CreateUser prefix = this.CreateUser prefix

    interface IAsyncDisposable with
        member _.DisposeAsync() =
            ValueTask(
                task {
                    for c in containers do
                        do! c.DisposeAsync().AsTask()
                }
            )

    static member StartAsync(ctx: FederationContext) : Task<MastodonServer> =
        task {
            let env = mastodonEnv ()
            let caMapping (b: ContainerBuilder) = b.WithResourceMapping(bytes ctx.Pki.CaPem, "/certs/ca.pem")

            let postgres =
                ContainerBuilder(PostgresImage)
                    .WithNetwork(ctx.Network)
                    .WithNetworkAliases("db")
                    .WithEnvironment("POSTGRES_USER", "mastodon")
                    .WithEnvironment("POSTGRES_PASSWORD", "mastodon-test-pw")
                    .WithEnvironment("POSTGRES_DB", "mastodon")
                    // TCP on 127.0.0.1 only answers once the real server is up (the init server is socket-only).
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "mastodon"))
                    .Build()
            let redis =
                ContainerBuilder(RedisImage)
                    .WithNetwork(ctx.Network)
                    .WithNetworkAliases("redis")
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("redis-cli", "ping"))
                    .Build()

            // db:setup loads the schema (about 12 s), then puma replaces the shell.
            let web =
                (ContainerBuilder(MastodonImage)
                    .WithNetwork(ctx.Network)
                    .WithNetworkAliases("web")
                    .WithCommand("sh", "-c", "bundle exec rails db:setup && exec bundle exec puma -C config/puma.rb")
                    .WithResourceMapping(bytes userScript, "/tmp/user.rb")
                 |> withEnv env
                 |> caMapping)
                    .Build()

            let sidekiq =
                (ContainerBuilder(MastodonImage)
                    .WithNetwork(ctx.Network)
                    .WithNetworkAliases("sidekiq")
                    .WithCommand("bundle", "exec", "sidekiq")
                    .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("test", "-f", SidekiqReadyFile))
                 |> withEnv env
                 |> caMapping)
                    .Build()

            let front = ctx.CaddyFront(MastodonDomain, "web:3000")
            do! ctx.Time("mastodon: start postgres, redis, caddy", Task.WhenAll(postgres.StartAsync(), redis.StartAsync(), front.StartAsync()))
            do! web.StartAsync()
            use probe = ctx.HttpClientFor front
            do! ctx.Time("mastodon: web healthy (db:setup + boot)", waitForHealth probe (TimeSpan.FromSeconds 240.0))
            do! ctx.Time("mastodon: sidekiq ready", sidekiq.StartAsync())
            return MastodonServer(ctx, web, [ front; sidekiq; web; redis; postgres ], front)
        }
