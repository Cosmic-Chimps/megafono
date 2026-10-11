/// User Story 5 in the shape a self-hoster runs it (T075, FR-063, SC-006): the three images are built from the repository's
/// Dockerfiles and run like deploy/compose.yaml does, with the gateway answering for `megafono.test` itself (TLS
/// terminated by the gateway, as in production), and a real GoToSocial finds and follows the sample app's users through it.
///
/// Slow (three image builds, about 3 to 6 minutes cold), so it has its own trait and is not part of the default run:
/// It is `Explicit`, so a plain `dotnet test` skips it. Run it with:
///   dotnet test --project tests/Megafono.Interop.Tests -- --explicit on --filter-trait "Suite=deployment"
module Megafono.Interop.Tests.DeploymentShapeTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks
open DotNet.Testcontainers.Builders
open DotNet.Testcontainers.Containers
open DotNet.Testcontainers.Images
open DotNet.Testcontainers.Networks
open Xunit
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private repoRoot () =
    let rec up (dir: DirectoryInfo) =
        if isNull dir then failwith "Megafono.slnx not found above the test directory"
        elif File.Exists(Path.Combine(dir.FullName, "Megafono.slnx")) then dir.FullName
        else up dir.Parent

    up (DirectoryInfo AppContext.BaseDirectory)

let private buildImage (root: string) (dockerfile: string) (name: string) : Task<IImage> =
    task {
        let image =
            ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(root)
                .WithDockerfile(dockerfile)
                .WithName($"megafono-deployment-test/{name}:{Guid.NewGuid():N}")
                .WithDeleteIfExists(true)
                .Build()

        do! image.CreateAsync()
        return image :> IImage
    }

// Secrets the way .env.example says to make them (the values do not matter, only that the shapes are valid)
// The master key is the run's shared one, because the instance actor's key is seeded into the database (see below)
let private masterKey = MegafonoInterop.masterKeyBase64
let private apiToken = Convert.ToHexString(Security.Cryptography.RandomNumberGenerator.GetBytes 32).ToLowerInvariant()
let private webhookSecret = Convert.ToHexString(Security.Cryptography.RandomNumberGenerator.GetBytes 32).ToLowerInvariant()
let private dbPassword = Convert.ToHexString(Security.Cryptography.RandomNumberGenerator.GetBytes 12).ToLowerInvariant()
let private adminPassword = Convert.ToHexString(Security.Cryptography.RandomNumberGenerator.GetBytes 8).ToLowerInvariant()

// Compose calls the database `db`. Here the federation network also carries Mastodon's own Postgres under that alias, and
// Megáfono is on both networks, so the test uses a name nothing else has
let private dbAlias = "megafono-deployment-db"

let private logsOf (c: IContainer) =
    task {
        try
            let! out, err = c.GetLogsAsync()
            return out + err
        with e ->
            return $"(no logs: {e.Message})"
    }

type DeploymentShapeTests(net: FederationNetwork) =

    [<Trait("Suite", "deployment")>]
    [<Fact(Explicit = true)>]
    member _.``US5 the built images behind the gateway: GoToSocial finds and follows the sample's users, the internal API stays internal`` () : Task =
        task {
            let! gts = net.GoToSocial()
            let root = repoRoot ()
            let pki = net.Pki
            let caPem = pki.CaPem

            // 1. The three images, from the same Dockerfiles compose builds
            let! megafonoImage, gatewayImage, sampleImage =
                task {
                    let a = buildImage root "deploy/Dockerfile.megafono" "megafono"
                    let b = buildImage root "deploy/Dockerfile.gateway" "gateway"
                    let c = buildImage root "deploy/Dockerfile.sample" "sample"
                    let! images = Task.WhenAll [| a; b; c |]
                    return images[0], images[1], images[2]
                }

            // 2. The deployment's own networks: `edge` is the federation network (GoToSocial is on it), `internal` has no route out
            let internalNet = NetworkBuilder().WithCreateParameterModifier(fun p -> p.Internal <- true).Build()
            do! internalNet.CreateAsync()

            let started = Collections.Generic.List<IContainer>()

            let start (c: IContainer) =
                task {
                    started.Add c
                    do! c.StartAsync()
                    return c
                }

            // The name megafono.test belongs to this test's gateway for the duration; the lease is released after the
            // containers are gone (it is disposed when the test ends, after the `finally` below)
            use! _domain = net.LeaseDomain()

            try
                let! db =
                    start (
                        ContainerBuilder("docker.io/library/postgres:17-alpine")
                            .WithNetwork(internalNet)
                            .WithNetworkAliases(dbAlias)
                            .WithEnvironment("POSTGRES_DB", "megafono")
                            .WithEnvironment("POSTGRES_USER", "megafono")
                            .WithEnvironment("POSTGRES_PASSWORD", dbPassword)
                            .WithPortBinding(5432, true)
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilCommandIsCompleted("pg_isready", "-h", "127.0.0.1", "-U", "megafono", "-d", "megafono"))
                            .Build()
                    )

                // GoToSocial (shared by every test of the run) remembers the instance actor key it saw for megafono.test; a
                // real deployment keeps its key, so the database starts with the run's key instead of a new one
                let hostConnection =
                    $"Host={db.Hostname};Port={db.GetMappedPublicPort 5432};Database=megafono;Username=megafono;Password={dbPassword};Pooling=false"

                do! MegafonoInterop.seedInstanceKey hostConnection

                // 3. Megáfono, with the settings compose gives it. The test CA is trusted through SSL_CERT_FILE so it can call
                //    gts.test; the private-address switch is a test-only setting and so needs a non-Production environment.
                let! megafono =
                    start (
                        ContainerBuilder(megafonoImage)
                            .WithNetwork(net.Network)
                            .WithNetwork(internalNet)
                            .WithNetworkAliases("megafono")
                            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
                            .WithEnvironment("Megafono__Domain", MegafonoDomain)
                            .WithEnvironment("Megafono__MasterKey", masterKey)
                            .WithEnvironment("Megafono__App__BaseUrl", "http://sample:8080")
                            .WithEnvironment("Megafono__App__Token", apiToken)
                            .WithEnvironment("Megafono__App__WebhookSecret", webhookSecret)
                            .WithEnvironment("ConnectionStrings__Database", $"Host={dbAlias};Database=megafono;Username=megafono;Password={dbPassword}")
                            .WithEnvironment("Megafono__TrustedProxies", "10.0.0.0/8,172.16.0.0/12,192.168.0.0/16")
                            .WithEnvironment("Megafono__Federation__AllowPrivateAddresses", "true")
                            .WithEnvironment("SSL_CERT_FILE", "/certs/ca.pem")
                            .WithResourceMapping(Encoding.UTF8.GetBytes caPem, "/certs/ca.pem")
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on"))
                            .Build()
                    )

                let! sample =
                    start (
                        ContainerBuilder(sampleImage)
                            .WithNetwork(net.Network)
                            .WithNetwork(internalNet)
                            .WithNetworkAliases("sample")
                            .WithEnvironment("APP__DOMAIN", MegafonoDomain)
                            .WithEnvironment("MEGAFONO__URL", "http://megafono:8081")
                            .WithEnvironment("MEGAFONO__TOKEN", apiToken)
                            .WithEnvironment("MEGAFONO__WEBHOOKSECRET", webhookSecret)
                            .WithEnvironment("SAMPLE__ADMINPASSWORD", adminPassword)
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on"))
                            .Build()
                    )

                // 4. The gateway: the only container on the federation network that answers for megafono.test, with the
                //    megafono.test certificate mounted like the operator's certificate in production
                let leaf = pki.Leaf MegafonoDomain

                let! gateway =
                    start (
                        ContainerBuilder(gatewayImage)
                            .WithNetwork(net.Network)
                            .WithNetworkAliases(MegafonoDomain)
                            .WithPortBinding(443, true)
                            .WithEnvironment("Kestrel__Certificates__Default__Path", "/certs/tls.crt")
                            .WithEnvironment("Kestrel__Certificates__Default__KeyPath", "/certs/tls.key")
                            .WithEnvironment("ReverseProxy__Clusters__megafono__Destinations__primary__Address", "http://megafono:8080")
                            .WithEnvironment("ReverseProxy__Clusters__app__Destinations__primary__Address", "http://sample:8080")
                            .WithResourceMapping(Encoding.UTF8.GetBytes leaf.CertPem, "/certs/tls.crt")
                            .WithResourceMapping(Encoding.UTF8.GetBytes leaf.KeyPem, "/certs/tls.key")
                            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Now listening on"))
                            .Build()
                    )

                // The test process reaches the gateway through its mapped port, trusting only the test CA
                use viaGateway = TestCa.httpClient pki (Some(gateway.Hostname, int (gateway.GetMappedPublicPort 443)))
                use basicAuth = TestCa.httpClient pki (Some(gateway.Hostname, int (gateway.GetMappedPublicPort 443)))
                basicAuth.DefaultRequestHeaders.Authorization <-
                    Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes $"admin:{adminPassword}"))

                try
                    // Ready when the whole chain answers: gateway -> Megáfono -> database, and gateway -> sample
                    let! _ =
                        pollUntil (TimeSpan.FromSeconds 90.0) "the deployment to answer through the gateway" (fun () ->
                            task {
                                let! nodeinfo = viaGateway.GetAsync $"https://{MegafonoDomain}/nodeinfo/2.1"
                                let! profile = viaGateway.GetAsync $"https://{MegafonoDomain}/u/test"
                                return (if nodeinfo.IsSuccessStatusCode && profile.IsSuccessStatusCode then Some() else None)
                            })

                    // The routing split at the edge: Megáfono's paths and the app's pages through one name
                    let! actor = viaGateway.GetAsync $"https://{MegafonoDomain}/ap/actor"
                    Assert.Equal(HttpStatusCode.OK, actor.StatusCode)
                    let! profile = viaGateway.GetAsync $"https://{MegafonoDomain}/u/test"
                    let! profileBody = profile.Content.ReadAsStringAsync()
                    Assert.Contains("rel=\"alternate\"", profileBody)
                    Assert.True(profile.Headers.Contains "Link")

                    // 5. US5 scenario 1: a GoToSocial user finds the sample's user by handle
                    use! carol = gts.CreateUser "carol"
                    let! found = carol.SearchAccount("test@megafono.test", true)
                    let testAccount = Assert.IsType<Account>(found.Value)
                    Assert.Equal("test", testAccount.Username)
                    Assert.Equal("Test User", testAccount.DisplayName)

                    // 6. ...follows it (the sample's `test` user is unlocked: accepted at once) and the sample's own page shows
                    //    the follower and the event Megáfono sent it, so the whole app contract worked through the images
                    let! _ = carol.Follow testAccount.Id
                    let! _ =
                        pollFor "carol follows test" (fun () ->
                            task {
                                let! r = carol.Relationship testAccount.Id
                                return (if r.Following then Some r else None)
                            })

                    let! manage =
                        pollFor "the sample's manage page lists carol and the follow event" (fun () ->
                            task {
                                let! page = basicAuth.GetStringAsync $"https://{MegafonoDomain}/u/test/manage"
                                return (if page.Contains $"@{carol.Username}@gts.test" && page.Contains "follow.accepted" then Some page else None)
                            })

                    Assert.Contains("Manage @test", manage)

                    // 7. A locked user: the request waits on the sample's page, approving through the page reaches GoToSocial
                    use! dave = gts.CreateUser "dave"
                    let! alice = dave.SearchAccount("alice@megafono.test", true)
                    let aliceAccount = alice.Value
                    let! requested = dave.Follow aliceAccount.Id
                    Assert.True(requested.Requested || requested.Following)

                    let! pending =
                        pollFor "the pending request on alice's manage page" (fun () ->
                            task {
                                let! page = basicAuth.GetStringAsync $"https://{MegafonoDomain}/u/alice/manage"
                                let m = Regex.Match(page, "action=\"(/u/alice/follow-requests/[^/\"]+/approve)\"")
                                return (if m.Success then Some m.Groups[1].Value else None)
                            })

                    let! approved = basicAuth.PostAsync($"https://{MegafonoDomain}{pending}", new FormUrlEncodedContent([]))
                    Assert.True(approved.StatusCode = HttpStatusCode.OK || approved.StatusCode = HttpStatusCode.Found, $"approve answered {approved.StatusCode}")
                    let! _ =
                        pollFor "dave follows alice after the approval" (fun () ->
                            task {
                                let! r = dave.Relationship aliceAccount.Id
                                return (if r.Following then Some r else None)
                            })

                    // 8. US5 scenario 3: the internal API is not reachable through the gateway. The paths go to the app, which has
                    //    no such route; a bearer token changes nothing
                    for path in [ "/internal/actors/x/followers"; "/internal/actors/x/objects"; "/internal/remote-actors?handle=a@b.c" ] do
                        let! r = viaGateway.GetAsync $"https://{MegafonoDomain}{path}"
                        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode)
                        use request = new HttpRequestMessage(HttpMethod.Get, $"https://{MegafonoDomain}{path}")
                        request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", apiToken)
                        let! withToken = viaGateway.SendAsync request
                        Assert.Equal(HttpStatusCode.NotFound, withToken.StatusCode)

                    // The gateway publishes 443 and nothing else, and does not listen on 8081 at all
                    Assert.ThrowsAny<Exception>(fun () -> gateway.GetMappedPublicPort 8081 |> ignore) |> ignore
                    Assert.ThrowsAny<Exception>(fun () -> megafono.GetMappedPublicPort 8081 |> ignore) |> ignore

                    let curl =
                        ContainerBuilder(CurlImage)
                            .WithNetwork(net.Network)
                            .WithEntrypoint("sleep", "infinity")
                            .WithResourceMapping(Encoding.UTF8.GetBytes caPem, "/certs/ca.pem")
                            .Build()

                    let! _ = start curl
                    // From the federation network (where GoToSocial lives): the gateway's address has no 8081
                    let! refused = curl.ExecAsync [| "curl"; "-sS"; "-m"; "5"; $"http://{MegafonoDomain}:8081/health" |]
                    Assert.NotEqual(Nullable 0L, refused.ExitCode)
                    // ...and the one door that does exist on 8081 asks for the bearer token, so nothing is served without it
                    let! unauthenticated =
                        curl.ExecAsync [| "curl"; "-sS"; "-m"; "5"; "-o"; "/dev/null"; "-w"; "%{http_code}"; "http://megafono:8081/internal/actors/x/followers" |]
                    Assert.Equal("401", unauthenticated.Stdout.Trim())
                    let! wrongPort =
                        curl.ExecAsync [| "curl"; "-sS"; "-m"; "5"; "-o"; "/dev/null"; "-w"; "%{http_code}"; "http://megafono:8081/ap/actor" |]
                    Assert.Equal("404", wrongPort.Stdout.Trim()) // nothing federated is served on the internal port either
                with e ->
                    // A failed run is only useful with the containers' logs
                    let! megafonoLogs = logsOf megafono
                    let! sampleLogs = logsOf sample
                    let! gatewayLogs = logsOf gateway
                    return failwith $"{e.Message}\n--- megafono ---\n{megafonoLogs}\n--- sample ---\n{sampleLogs}\n--- gateway ---\n{gatewayLogs}\n{e.StackTrace}"
            finally
                for c in Seq.rev started do
                    try c.DisposeAsync().AsTask().Wait() with _ -> ()

                try internalNet.DisposeAsync().AsTask().Wait() with _ -> ()
        }
