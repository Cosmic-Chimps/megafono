/// Tries to break FR-045 in every way the task lists: a followers-only object and its Create activity must
/// answer 404, never 403, to anyone but a validly signed accepted follower (SC-002)
module Megafono.Tests.Integration.FetchPrivacyTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Tests.Fixtures.Publishing

type World =
    { Rig: Rig
      /// A followers-only object of u-1 and its Create activity, as federation paths
      Paths: string list
      ObjectPath: string }

let private worldWith (pg: PostgresFixture) (extra: (string * string) list) : Task<World> =
    task {
        let! rig = startWith pg extra [ "alice"; "bob"; "carol"; "dave"; "instance"; "erin" ]
        rig.Stub.Mutate <- Some(fun name doc -> if name = "instance" then doc["type"] <- Text.Json.Nodes.JsonValue.Create "Application")
        do! addFollower rig.Db "u-1" rig.Stub "alice" "accepted" None
        do! addFollower rig.Db "u-1" rig.Stub "bob" "pending" None
        do! addFollower rig.Db "u-1" rig.Stub "carol" "removed" None
        // erin follows u-2, a different author
        do! addFollower rig.Db "u-2" rig.Stub "erin" "accepted" None
        let! id = publish rig "u-1" "followers" "<p>for friends</p>"
        let! activity = createActivityId rig id
        return { Rig = rig; Paths = [ objectPath id; $"/ap/activities/{activity}" ]; ObjectPath = objectPath id }
    }

let private world (pg: PostgresFixture) : Task<World> = worldWith pg []

let private expect (status: HttpStatusCode) (label: string) (response: HttpResponseMessage) =
    Assert.True((response.StatusCode = status), $"{label}: expected {status}, got {response.StatusCode}")
    Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode)

let private expectHidden (label: string) (response: HttpResponseMessage) =
    expect HttpStatusCode.NotFound label response
    Assert.False(response.Headers.Contains "WWW-Authenticate", $"{label}: no hint that authentication would help")

/// Deletes the followers-only object of the world (its Delete activity is stored)
let private delete' (rig: Rig) =
    task {
        let! id = Db.scalar rig.Db "SELECT id::text FROM objects LIMIT 1"
        return! sendInternal rig HttpMethod.Delete $"/internal/objects/{id}" None
    }

type FetchPrivacyTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-045 SC-002 an unsigned request gets 404``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for path in w.Paths do
                use! response = federationGet w.Rig path []
                expectHidden $"unsigned {path}" response
                let! body = response.Content.ReadAsStringAsync()
                Assert.Equal("", body)
        }

    [<Fact>]
    member _.``FR-045 SC-002 a signed request from a non-follower, a pending follower or a removed follower gets 404``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for path in w.Paths do
                for name, label in [ "dave", "non-follower"; "bob", "pending follower"; "carol", "removed follower"; "erin", "follower of another author" ] do
                    use! response = signedGet w.Rig w.Rig.Stub name path
                    expectHidden $"{label} {path}" response
        }

    [<Fact>]
    member _.``FR-045 SC-002 a request signed by a remote instance actor gets 404``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for path in w.Paths do
                use! response = signedGet w.Rig w.Rig.Stub "instance" path
                expectHidden $"instance actor {path}" response
        }

    [<Fact>]
    member _.``FR-045 SC-002 a signature made with the wrong key, or for another path, or damaged, gets 404``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig
            let! impostor = StubRemote.start [ "alice" ]
            use _ = { new IDisposable with member _.Dispose() = (impostor :> IAsyncDisposable).DisposeAsync().AsTask().Wait() }
            let path = w.ObjectPath
            let url = w.Rig.Host.FederationUrl + path

            // The impostor's key signs, but the keyId claims to be the real alice
            let forged =
                impostor.Sign("alice", "GET", url, None)
                |> List.map (fun (n, v) ->
                    if n = "Signature" then n, v.Replace(impostor.KeyId "alice", w.Rig.Stub.KeyId "alice") else n, v)

            use! wrongKey = federationGet w.Rig path forged
            expectHidden "wrong key" wrongKey

            // A valid signature for the object, replayed on the activity's URL
            let signedForObject = w.Rig.Stub.Sign("alice", "GET", url, None)
            use! otherPath = federationGet w.Rig (List.last w.Paths) signedForObject
            expectHidden "signature for another path" otherPath

            // The same signature with a changed Date, then with garbage
            let altered = signedForObject |> List.map (fun (n, v) -> if n = "Date" then n, DateTime.UtcNow.AddMinutes(-3.0).ToString "r" else n, v)
            use! changedDate = federationGet w.Rig path altered
            expectHidden "changed Date" changedDate

            use! garbage = federationGet w.Rig path [ "Signature", "keyId=\"nope\",signature=\"AAAA\"" ]
            expectHidden "garbage signature" garbage
            use! noKey = federationGet w.Rig path [ "Signature", "this is not a signature" ]
            expectHidden "malformed signature" noKey
        }

    [<Fact>]
    member _.``FR-045 an accepted follower with a valid signature gets 200, and only while the follow lasts``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for path in w.Paths do
                use! response = signedGet w.Rig w.Rig.Stub "alice" path
                expect HttpStatusCode.OK $"accepted follower {path}" response
                Assert.Equal("application/activity+json", response.Content.Headers.ContentType.MediaType)
                Assert.Contains("private", response.Headers.CacheControl.ToString())

            // The check is made at fetch time: removed, the same follower is hidden again; accepted again, shown again
            do! setFollowStatus w.Rig.Db "u-1" w.Rig.Stub "alice" "removed"
            use! removed = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
            expectHidden "alice after removal" removed
            do! setFollowStatus w.Rig.Db "u-1" w.Rig.Stub "alice" "accepted"
            use! back = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
            expect HttpStatusCode.OK "alice accepted again" back
        }

    [<Fact>]
    member _.``FR-045 a rotated key is picked up once before the signature is refused``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig
            use! before = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
            expect HttpStatusCode.OK "before rotation" before

            // alice's server rotates its key; Megáfono still has the old one cached
            w.Rig.Stub.RotateKey()
            use! after = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
            expect HttpStatusCode.OK "after rotation" after
        }

    [<Fact>]
    member _.``FR-014 signed fetches are limited per verified host with 429 and Retry-After, unsigned ones never by that layer``() : Task =
        task {
            let! w = worldWith pg [ "Megafono:RateLimit:PerHost", "2" ]
            use _ = disposeRig w.Rig

            // The first two signed fetches from the follower's host are served
            for n in 1..2 do
                use! response = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
                expect HttpStatusCode.OK $"signed fetch {n}" response

            // The third, from the same verified host, is told to wait: 429, not 404, and with Retry-After
            use! limited = signedGet w.Rig w.Rig.Stub "alice" w.ObjectPath
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode)
            Assert.True(limited.Headers.Contains "Retry-After")
            Assert.True(int (Seq.head (limited.Headers.GetValues "Retry-After")) > 0)

            // The limit is by host, whoever signs: another user of the same server is limited too
            use! sameHost = signedGet w.Rig w.Rig.Stub "dave" w.ObjectPath
            Assert.Equal(HttpStatusCode.TooManyRequests, sameHost.StatusCode)

            // Unsigned requests never reach the per-host layer: always the plain 404 for a private object
            for n in 1..6 do
                use! unsigned = federationGet w.Rig w.ObjectPath []
                expectHidden $"unsigned fetch {n}" unsigned
        }

    [<Fact>]
    member _.``FR-045 public and unlisted objects are returned to anyone, signed or not``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for audience in [ "public"; "unlisted" ] do
                let! id = publish w.Rig "u-1" audience $"<p>{audience}</p>"
                let! activity = createActivityId w.Rig id

                for path in [ objectPath id; $"/ap/activities/{activity}" ] do
                    use! unsigned = federationGet w.Rig path []
                    expect HttpStatusCode.OK $"{audience} unsigned {path}" unsigned
                    use! stranger = signedGet w.Rig w.Rig.Stub "dave" path
                    expect HttpStatusCode.OK $"{audience} non-follower {path}" stranger
        }

    [<Fact>]
    member _.``FR-045 unknown and malformed object ids are 404, and a non-canonical path is 404``() : Task =
        task {
            let! w = world pg
            use _ = disposeRig w.Rig

            for path in [ $"/ap/objects/{Guid.NewGuid()}"; "/ap/objects/not-a-guid"; $"/ap/activities/{Guid.NewGuid()}"; w.ObjectPath.ToUpperInvariant() ] do
                use! response = federationGet w.Rig path []
                expect HttpStatusCode.NotFound path response

            // Only Create and Update activities are fetchable: a Delete activity's id is not
            let! _ = delete' w.Rig
            let! deleteId = Db.scalar w.Rig.Db "SELECT id::text FROM activities WHERE activity_type = 'Delete' LIMIT 1"
            use! deleteActivity = signedGet w.Rig w.Rig.Stub "alice" $"/ap/activities/{deleteId}"
            expect HttpStatusCode.NotFound "a Delete activity" deleteActivity
        }
