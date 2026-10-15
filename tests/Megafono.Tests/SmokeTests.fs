module Megafono.Tests.SmokeTests

open System.Net
open System.Threading.Tasks
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type Smoke(pg: PostgresFixture) =
    [<Fact>]
    member _.``health answers 200 on real Kestrel, on both ports``() : Task =
        task {
            let! connectionString = pg.CreateDatabase()
            let! host = MegafonoHost.start (MegafonoHost.settings connectionString "http://127.0.0.1:9" (FakeTime()))
            use _ = MegafonoHost.disposer host

            let! federation = host.FederationClient().GetAsync "/health"
            let! internalPort = host.InternalClient(None).GetAsync "/health"
            Assert.Equal(HttpStatusCode.OK, federation.StatusCode)
            Assert.Equal(HttpStatusCode.OK, internalPort.StatusCode)
        }
