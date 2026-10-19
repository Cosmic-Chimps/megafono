module Megafono.Tests.Integration.FeedPostsTests

open System
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private time = FakeTime(DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero))
let private published = DateTimeOffset(2026, 10, 18, 11, 59, 0, TimeSpan.Zero)
let private postId = "https://gts.example/users/bob/statuses/1"

let private inTx (dataSource: NpgsqlDataSource) (work: NpgsqlTransaction -> Task<'a>) : Task<'a> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! result = work tx
        do! tx.CommitAsync()
        return result
    }

/// alex and bea follow bob (accepted), cy too but was deleted, dan's follow is pending
let private setup (pg: PostgresFixture) (stub: StubRemote.StubRemote) =
    task {
        let! cs = pg.CreateDatabase()
        let db = NpgsqlDataSource.Create cs
        do! Database.initializeSchema db

        for user in [ "alex"; "bea"; "cy"; "dan" ] do
            let! _ = Seed.localActor db user user
            ()

        for user, status in [ "alex", "accepted"; "bea", "accepted"; "cy", "accepted"; "dan", "pending" ] do
            do! Publishing.addFollowing db user stub "bob" status

        do! Db.execute db "UPDATE local_actors SET deleted_at = now() WHERE app_user_id = 'cy'"
        return db
    }

type FeedPostsTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``005 R-52 candidates are the live users with an accepted follow of the author, sorted``() : Task =
        task {
            use! stub = StubRemote.start [ "bob" ]
            let! db = setup pg stub
            let! candidates = inTx db (fun tx -> FeedPosts.followerCandidates tx (stub.ActorId "bob"))
            Assert.Equal<string list>([ "alex"; "bea" ], candidates)
        }

    [<Fact>]
    member _.``005 R-57 lockUsers returns the live users among those asked for, in app_user_id order``() : Task =
        task {
            use! stub = StubRemote.start [ "bob" ]
            let! db = setup pg stub
            let! locked = inTx db (fun tx -> FeedPosts.lockUsers tx [ "dan"; "bea"; "cy"; "nobody"; "alex" ])
            Assert.Equal<string list>([ "alex"; "bea"; "dan" ], locked)
        }

    [<Fact>]
    member _.``005 R-52 R-57 qualifying drops blocked users and, when asked, users who no longer follow``() : Task =
        task {
            use! stub = StubRemote.start [ "bob" ]
            let! db = setup pg stub
            let bob = stub.ActorId "bob"
            let host = stub.Authority.ToLowerInvariant()
            let all = [ "alex"; "bea"; "cy"; "dan" ]

            let qualifying mustFollow = inTx db (fun tx -> FeedPosts.qualifying tx all bob host mustFollow)

            // follows count when asked (dan's is pending); a deleted user never qualifies
            let! following = qualifying true
            Assert.Equal<string list>([ "alex"; "bea" ], following)
            let! anyone = qualifying false
            Assert.Equal<string list>([ "alex"; "bea"; "dan" ], anyone)

            // a user's block of the author takes only that user out
            do! Db.execute db $"INSERT INTO blocks (id, local_actor, actor) VALUES (gen_random_uuid(), 'bea', '{bob}')"
            let! afterUserBlock = qualifying true
            Assert.Equal<string list>([ "alex" ], afterUserBlock)

            // the instance's block of the author's domain takes everyone out
            do! Db.execute db $"INSERT INTO blocks (id, domain) VALUES (gen_random_uuid(), '{Blocks.hostName host}')"
            let! afterInstanceBlock = qualifying false
            Assert.Empty afterInstanceBlock
        }

    [<Fact>]
    member _.``005 R-55 rows: inserted once per post and user, live rows read back, updated, deleted once``() : Task =
        task {
            use! stub = StubRemote.start [ "bob" ]
            let! db = setup pg stub
            let bob = stub.ActorId "bob"

            let! first = inTx db (fun tx -> FeedPosts.insertRows tx postId bob "followers" published [ "bea"; "alex" ])
            Assert.Equal<string list>([ "alex"; "bea" ], first)

            // the same post again, also for a user it has not reached: only the new row is returned
            let! second = inTx db (fun tx -> FeedPosts.insertRows tx postId bob "public" published [ "alex"; "bea"; "dan" ])
            Assert.Equal<string list>([ "dan" ], second)

            let! rows = inTx db (fun tx -> FeedPosts.liveRows tx postId bob)
            Assert.Equal<string list>([ "alex"; "bea"; "dan" ], rows |> List.map (fun r -> r.LocalUser))
            // each row keeps the audience the post arrived with
            Assert.Equal<string list>([ "followers"; "followers"; "public" ], rows |> List.map (fun r -> r.Audience))
            Assert.All(rows, fun r -> Assert.Equal(published, r.PublishedAt))

            // another author's rows are not this signer's
            let! others = inTx db (fun tx -> FeedPosts.liveRows tx postId "https://gts.example/users/eve")
            Assert.Empty others

            do! inTx db (fun tx -> task { do! FeedPosts.markUpdated tx time postId bob })
            let! updated = Db.count db $"SELECT count(*) FROM feed_posts WHERE id = '{postId}' AND updated_at = '2026-10-18T12:00:00Z'"
            Assert.Equal(3L, updated)

            let! deleted = inTx db (fun tx -> FeedPosts.markDeleted tx time postId bob)
            Assert.Equal<string list>([ "alex"; "bea"; "dan" ], deleted)
            let! again = inTx db (fun tx -> FeedPosts.markDeleted tx time postId bob)
            Assert.Empty again
            let! live = inTx db (fun tx -> FeedPosts.liveRows tx postId bob)
            Assert.Empty live

            // a deleted row is never live again: inserting the post anew finds the rows
            let! afterDelete = inTx db (fun tx -> FeedPosts.insertRows tx postId bob "public" published [ "alex" ])
            Assert.Empty afterDelete
        }

    [<Fact>]
    member _.``005 R-58 recordNamespace is idempotent and knownNamespaces answers only for recorded ones``() : Task =
        task {
            let! cs = pg.CreateDatabase()
            let db = NpgsqlDataSource.Create cs
            do! Database.initializeSchema db

            do!
                inTx db (fun tx ->
                    task {
                        do! FeedPosts.recordNamespace tx "https://app.example/ns#"
                        do! FeedPosts.recordNamespace tx "https://app.example/ns#"
                    })

            let! rows = Db.count db "SELECT count(*) FROM extension_namespaces"
            Assert.Equal(1L, rows)

            let! known = inTx db (fun tx -> FeedPosts.knownNamespaces tx [ "https://app.example/ns#"; "https://other.example/ns#" ])
            Assert.Equal<string list>([ "https://app.example/ns#" ], Set.toList known)
            let! none = inTx db (fun tx -> FeedPosts.knownNamespaces tx [])
            Assert.Empty none
        }
