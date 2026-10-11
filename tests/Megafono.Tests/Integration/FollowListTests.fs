module Megafono.Tests.Integration.FollowListTests

open System
open System.Net
open System.Text.Json
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

type World =
    { Host: MegafonoHost.MegafonoHost
      Db: NpgsqlDataSource
      T0: DateTimeOffset }

let private start (pg: PostgresFixture) : Task<World> =
    task {
        let! cs = pg.CreateDatabase()
        let! app = FakeApp.start MegafonoHost.token MegafonoHost.webhookSecret
        app.AddUser(FakeApp.user "u-1" "alex")
        app.AddUser(FakeApp.user "u-2" "sam")
        let! host = MegafonoHost.start (MegafonoHost.settings cs app.Url (FakeTime()))
        let db = NpgsqlDataSource.Create cs
        // Both users exist as local actors once the app has been asked about them
        let! _ = host.InternalClient(Some MegafonoHost.token).PutAsync("/internal/actors/u-1", null)
        let! _ = host.InternalClient(Some MegafonoHost.token).PutAsync("/internal/actors/u-2", null)
        return { Host = host; Db = db; T0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) }
    }

let private actor (n: int) = $"https://remote.example/users/r{n:D2}"

let private list (w: World) (user: string) (kind: string) (query: string) =
    task {
        let! response = w.Host.InternalClient(Some MegafonoHost.token).GetAsync($"/internal/actors/{user}/{kind}{query}")
        let! text = response.Content.ReadAsStringAsync()
        return response.StatusCode, text
    }

let private items (body: string) =
    use json = JsonDocument.Parse body
    [ for i in json.RootElement.GetProperty("items").EnumerateArray() -> i.GetProperty("actor").GetProperty("id").GetString(), i.GetProperty("status").GetString(), i.GetProperty("mutual").GetBoolean() ]

let private nextOf (body: string) =
    use json = JsonDocument.Parse body
    let n = json.RootElement.GetProperty "next"
    if n.ValueKind = JsonValueKind.Null then None else Some(n.GetString())

type FollowListTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``FR-035 an item carries the follow id, status, since and the actor``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! Seed.followRow w.Db "inbound" "u-1" (actor 1) "accepted" w.T0
            let! status, body = list w "u-1" "followers" ""
            Assert.Equal(HttpStatusCode.OK, status)
            use json = JsonDocument.Parse body
            let item = json.RootElement.GetProperty("items")[0]
            Assert.True(Guid.TryParse(item.GetProperty("followId").GetString()) |> fst)
            Assert.Equal("accepted", item.GetProperty("status").GetString())
            Assert.Equal("2026-01-01T00:00:00Z", item.GetProperty("since").GetString())
            let a = item.GetProperty "actor"
            Assert.Equal(actor 1, a.GetProperty("id").GetString())
            Assert.Equal("@r01@remote.example", a.GetProperty("handle").GetString())
            Assert.Equal("r01", a.GetProperty("name").GetString())
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("next").ValueKind)
        }

    [<Fact>]
    member _.``FR-035 followers and following are separate lists, per user``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            do! Seed.followRow w.Db "inbound" "u-1" (actor 1) "accepted" w.T0
            do! Seed.followRow w.Db "outbound" "u-1" (actor 2) "accepted" w.T0
            do! Seed.followRow w.Db "inbound" "u-2" (actor 3) "accepted" w.T0

            let! _, followers = list w "u-1" "followers" ""
            let! _, following = list w "u-1" "following" ""
            let! _, other = list w "u-2" "followers" ""
            Assert.Equal<string list>([ actor 1 ], items followers |> List.map (fun (a, _, _) -> a))
            Assert.Equal<string list>([ actor 2 ], items following |> List.map (fun (a, _, _) -> a))
            Assert.Equal<string list>([ actor 3 ], items other |> List.map (fun (a, _, _) -> a))
        }

    [<Fact>]
    member _.``FR-035 the status filter selects, and the default hides removed ones``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host

            for i, s in [ 1, "accepted"; 2, "pending"; 3, "rejected"; 4, "removed"; 5, "accepted" ] do
                do! Seed.followRow w.Db "inbound" "u-1" (actor i) s (w.T0.AddMinutes(float i))

            let statuses (query: string) =
                task {
                    let! status, body = list w "u-1" "followers" query
                    Assert.Equal(HttpStatusCode.OK, status)
                    return items body |> List.map (fun (_, s, _) -> s) |> List.sort
                }

            let! all = statuses ""
            let! accepted = statuses "?status=accepted"
            let! pending = statuses "?status=pending"
            let! removed = statuses "?status=removed"
            let! two = statuses "?status=pending,rejected"
            Assert.Equal<string list>([ "accepted"; "accepted"; "pending"; "rejected" ], all)
            Assert.Equal<string list>([ "accepted"; "accepted" ], accepted)
            Assert.Equal<string list>([ "pending" ], pending)
            Assert.Equal<string list>([ "removed" ], removed)
            Assert.Equal<string list>([ "pending"; "rejected" ], two)

            let! bad, _ = list w "u-1" "followers" "?status=maybe"
            Assert.Equal(HttpStatusCode.BadRequest, bad)
        }

    [<Fact>]
    member _.``FR-035 limit and the cursor page through everything once, newest first``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host

            for i in 1..7 do
                do! Seed.followRow w.Db "inbound" "u-1" (actor i) "accepted" (w.T0.AddMinutes(float i))
            // Same creation time as another row: the tie is broken by id, nothing is skipped or repeated
            do! Seed.followRow w.Db "inbound" "u-1" (actor 8) "accepted" (w.T0.AddMinutes 7.0)

            let collected = ResizeArray<string>()
            let mutable cursor: string option = None
            let mutable pages = 0
            let mutable fin = false

            while not fin do
                let query = "?limit=3" + (cursor |> Option.map (fun c -> "&after=" + Uri.EscapeDataString c) |> Option.defaultValue "")
                let! status, body = list w "u-1" "followers" query
                Assert.Equal(HttpStatusCode.OK, status)
                let page = items body
                Assert.True(page.Length <= 3)
                collected.AddRange(page |> List.map (fun (a, _, _) -> a))
                pages <- pages + 1
                cursor <- nextOf body
                fin <- cursor.IsNone

            Assert.Equal(3, pages)
            Assert.Equal(8, collected.Count)
            Assert.Equal(8, (collected |> Seq.distinct |> Seq.length))
            // newest first: r07/r08 (same time) come before r06 ... r01
            Assert.Equal(actor 1, collected[7])
            Assert.Contains(actor 7, collected.GetRange(0, 2))
            Assert.Contains(actor 8, collected.GetRange(0, 2))
        }

    [<Fact>]
    member _.``contract limit defaults to 50, is capped at 200, and bad values are refused``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host

            for i in 1..60 do
                do! Seed.followRow w.Db "inbound" "u-1" (actor i) "accepted" (w.T0.AddMinutes(float i))

            let! _, defaultPage = list w "u-1" "followers" ""
            Assert.Equal(50, (items defaultPage).Length)
            Assert.True((nextOf defaultPage).IsSome)

            let! _, capped = list w "u-1" "followers" "?limit=1000"
            Assert.Equal(60, (items capped).Length) // all 60, under the cap of 200
            Assert.True((nextOf capped).IsNone)

            for bad in [ "?limit=0"; "?limit=-1"; "?limit=many"; "?after=not-a-cursor"; "?after=" ] do
                let! status, _ = list w "u-1" "followers" bad
                Assert.Equal(HttpStatusCode.BadRequest, status)

            let! unknown, _ = list w "u-gone" "followers" ""
            Assert.Equal(HttpStatusCode.NotFound, unknown)
        }

    [<Fact>]
    member _.``FR-035 mutual is true only when both directions are accepted``() : Task =
        task {
            let! w = start pg
            use _ = MegafonoHost.disposer w.Host
            // r01: both accepted -> mutual. r02: they follow us, we asked and it is pending -> not mutual.
            // r03: they follow us, we do not follow them. r04: we follow them, they asked and we have not answered.
            // r05: both directions exist, ours was rejected -> not mutual.
            for i in 1..5 do
                do! Seed.followRow w.Db "inbound" "u-1" (actor i) (if i = 4 then "pending" else "accepted") (w.T0.AddMinutes(float i))

            do! Seed.followRow w.Db "outbound" "u-1" (actor 1) "accepted" w.T0
            do! Seed.followRow w.Db "outbound" "u-1" (actor 2) "pending" w.T0
            do! Seed.followRow w.Db "outbound" "u-1" (actor 4) "accepted" w.T0
            do! Seed.followRow w.Db "outbound" "u-1" (actor 5) "rejected" w.T0
            // someone else's relationship with r03 must not leak into u-1's flag
            do! Seed.followRow w.Db "outbound" "u-2" (actor 3) "accepted" w.T0

            let! _, followers = list w "u-1" "followers" ""
            let mutualOf (id: string) = items followers |> List.find (fun (a, _, _) -> a = id) |> fun (_, _, m) -> m
            Assert.True(mutualOf (actor 1))
            Assert.False(mutualOf (actor 2))
            Assert.False(mutualOf (actor 3))
            Assert.False(mutualOf (actor 4)) // their request is still pending
            Assert.False(mutualOf (actor 5))

            // The same pair seen from the following side
            let! _, following = list w "u-1" "following" ""
            let followingMutual (id: string) = items following |> List.find (fun (a, _, _) -> a = id) |> fun (_, _, m) -> m
            Assert.True(followingMutual (actor 1))
            Assert.False(followingMutual (actor 2)) // our follow is pending
            Assert.False(followingMutual (actor 4)) // theirs is pending
        }
