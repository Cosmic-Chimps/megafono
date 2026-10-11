module Megafono.Tests.Integration.ThreadsTests

open System
open System.Threading.Tasks
open Npgsql
open Xunit
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Tests.TestSupport
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture

let private domain = "megafono.test"
let private time = FakeTime(DateTimeOffset(2026, 10, 12, 12, 0, 0, TimeSpan.Zero))

let private objectIri (g: Guid) = Ids.objectId domain g

let private inTx (dataSource: NpgsqlDataSource) (work: NpgsqlTransaction -> Task<'a>) : Task<'a> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! result = work tx
        do! tx.CommitAsync()
        return result
    }

let private object' (db: NpgsqlDataSource) (user: string) (root: Guid option) (parent: string option) : Task<Guid> =
    task {
        let id = Guid.NewGuid()
        let orNull (v: string option) = match v with Some s -> $"'{s}'" | None -> "NULL"
        let rootSql = match root with Some r -> $"'{r}'" | None -> "NULL"

        do!
            Db.execute
                db
                $"""INSERT INTO objects (id, local_actor, audience, object_type, document, in_reply_to, thread_root)
                    VALUES ('{id}', '{user}', 'public', 'Note', '{{}}', {orNull parent}, {rootSql})"""

        return id
    }

let private remoteActor (db: NpgsqlDataSource) (id: string) : Task =
    Db.execute
        db
        $"INSERT INTO remote_actors (id, host, actor_type, inbox, key_id, public_key_pem) VALUES ('{id}', 'r.example', 'Person', 'https://r.example/i', '{id}#k', 'p') ON CONFLICT DO NOTHING"

let private setup (pg: PostgresFixture) =
    task {
        let! cs = pg.CreateDatabase()
        let db = NpgsqlDataSource.Create cs
        do! Database.initializeSchema db
        let! _ = Seed.localActor db "series" "frieren"
        let! _ = Seed.localActor db "alex" "alex"
        do! remoteActor db "https://r.example/users/bob"
        do! remoteActor db "https://r.example/users/carl"
        return db
    }

let private resolve db iri = inTx db (fun tx -> Threads.resolveParent tx domain iri)

let private audienceOf (text: string) = (Audience.receivedFromName text).Value

let private parent (iri: string) (objectGuid: Guid option) (root: Guid) : Threads.Parent =
    { Iri = iri; Object = objectGuid; LocalUser = None; RemoteAuthor = None; Root = root; RootAuthor = "series" }

type ThreadsTests(pg: PostgresFixture) =

    [<Fact>]
    member _.``002 T005 a root object resolves to itself, with its author as the thread owner``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None

            match! resolve db (objectIri root) with
            | Threads.Resolved p ->
                Assert.Equal(root, p.Root)
                Assert.Equal(Some root, p.Object)
                Assert.Equal(Some "series", p.LocalUser)
                Assert.Equal("series", p.RootAuthor)
                Assert.Equal(None, p.RemoteAuthor)
            | other -> failwith $"{other}"
        }

    [<Fact>]
    member _.``002 T005 a reply of ours resolves to its thread root; the thread owner stays the root's author``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let! ours = object' db "alex" (Some root) (Some(objectIri root))

            match! resolve db (objectIri ours) with
            | Threads.Resolved p ->
                Assert.Equal(root, p.Root)
                Assert.Equal(Some ours, p.Object)
                // who published the parent (alex), who owns the thread (the series)
                Assert.Equal(Some "alex", p.LocalUser)
                Assert.Equal("series", p.RootAuthor)
            | other -> failwith $"{other}"
        }

    [<Fact>]
    member _.``002 T005 a recorded remote reply resolves to its root and its remote author; a deleted one is Gone``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let replyIri = "https://r.example/users/bob/statuses/1"

            let! recorded =
                inTx db (fun tx ->
                    Threads.recordRemoteReply tx time replyIri "https://r.example/users/bob" (parent (objectIri root) (Some root) root) Audience.ReceivedPublic (time.GetUtcNow()))

            Assert.True recorded

            match! resolve db replyIri with
            | Threads.Resolved p ->
                Assert.Equal(root, p.Root)
                Assert.Equal(None, p.Object)
                Assert.Equal(None, p.LocalUser)
                Assert.Equal(Some "https://r.example/users/bob", p.RemoteAuthor)
                Assert.Equal("series", p.RootAuthor)
            | other -> failwith $"{other}"

            let! deleted = inTx db (fun tx -> Threads.markDeleted tx time replyIri)
            Assert.True deleted
            let! again = inTx db (fun tx -> Threads.markDeleted tx time replyIri)
            Assert.False again
            let! afterDelete = resolve db replyIri
            Assert.Equal(Threads.Gone, afterDelete)
        }

    [<Fact>]
    member _.``002 T005 unknown IRIs and a deleted object are told apart``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let! unknown = resolve db "https://r.example/users/bob/statuses/9"
            Assert.Equal(Threads.Unknown, unknown)
            // one of our object IRIs that does not exist, and an IRI of ours that is not an object
            let! missing = resolve db (objectIri (Guid.NewGuid()))
            Assert.Equal(Threads.Unknown, missing)
            let! notObject = resolve db $"https://{domain}/ap/actors/series"
            Assert.Equal(Threads.Unknown, notObject)

            do! Db.execute db $"UPDATE objects SET state = 'deleted', deleted_at = now() WHERE id = '{root}'"
            let! gone = resolve db (objectIri root)
            Assert.Equal(Threads.Gone, gone)
        }

    [<Fact>]
    member _.``002 T005 recording the same reply twice records it once``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let p = parent (objectIri root) (Some root) root
            let record () = inTx db (fun tx -> Threads.recordRemoteReply tx time "https://r.example/users/bob/statuses/1" "https://r.example/users/bob" p Audience.ReceivedPublic (time.GetUtcNow()))
            let! first = record ()
            let! second = record ()
            Assert.True first
            Assert.False second
            let! count = Db.count db "SELECT count(*) FROM replies"
            Assert.Equal(1L, count)
        }

    [<Fact>]
    member _.``002 T005 findReply returns the references the events need, nothing of the content``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let! ours = object' db "alex" (Some root) (Some(objectIri root))
            let replyIri = "https://r.example/users/bob/statuses/1"

            let p: Threads.Parent = { parent (objectIri ours) (Some ours) root with LocalUser = Some "alex" }
            let! _ = inTx db (fun tx -> Threads.recordRemoteReply tx time replyIri "https://r.example/users/bob" p Audience.ReceivedFollowers (time.GetUtcNow()))

            let! row = inTx db (fun tx -> Threads.findReply tx replyIri)
            let row = row.Value
            Assert.Equal(Some "https://r.example/users/bob", row.RemoteActor)
            Assert.Equal(None, row.LocalObject)
            Assert.Equal(objectIri ours, row.Parent)
            Assert.Equal(Some ours, row.ParentObject)
            Assert.Equal(Some "alex", row.ParentUser)
            Assert.Equal(root, row.ThreadRoot)
            Assert.Equal("series", row.RootAuthor)
            Assert.Equal("followers", row.Audience)
            Assert.Equal("live", row.State)

            let! columns = Publishing.strings db "SELECT column_name FROM information_schema.columns WHERE table_name = 'replies' ORDER BY 1"
            // R-27: references and times only
            Assert.DoesNotContain("content", columns)
            Assert.DoesNotContain("document", columns)
            Assert.DoesNotContain("body", columns)

            let! none = inTx db (fun tx -> Threads.findReply tx "https://r.example/users/bob/statuses/none")
            Assert.True none.IsNone
        }

    [<Fact>]
    member _.``002 T005 a local reply is recorded under its own object IRI and resolves as one of our objects``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let! ours = object' db "alex" (Some root) (Some(objectIri root))
            let p = { parent (objectIri root) (Some root) root with LocalUser = Some "series" }
            let! recorded = inTx db (fun tx -> Threads.recordLocalReply tx time ours (objectIri ours) p Audience.Public (time.GetUtcNow()))
            Assert.True recorded
            let! page = Threads.repliesPage db (objectIri root) 1
            Assert.Equal<string list>([ objectIri ours ], page.Items)
        }

    [<Fact>]
    member _.``002 T005 the replies collection lists the app's own first, then remote, each oldest first, public and unlisted live ones only``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let rootIri = objectIri root
            let p = parent rootIri (Some root) root
            let at (minutes: int) = time.GetUtcNow().AddMinutes(float minutes)
            let remote (name: string) audience minutes = inTx db (fun tx -> Threads.recordRemoteReply tx time $"https://r.example/users/bob/statuses/{name}" "https://r.example/users/bob" p audience (at minutes))

            let! _ = remote "late" Audience.ReceivedPublic 30
            let! _ = remote "early" Audience.ReceivedUnlisted 1
            let! _ = remote "followers" Audience.ReceivedFollowers 2
            let! _ = remote "direct" Audience.ReceivedDirect 3
            let! _ = remote "gone" Audience.ReceivedPublic 4
            let! _ = inTx db (fun tx -> Threads.markDeleted tx time "https://r.example/users/bob/statuses/gone")

            // alex's reply is later than every remote one but listed before them
            let! ours = object' db "alex" (Some root) (Some rootIri)
            let! _ = inTx db (fun tx -> Threads.recordLocalReply tx time ours (objectIri ours) p Audience.Public (at 60))

            let! page = Threads.repliesPage db rootIri 1
            Assert.Equal(3L, page.Total)
            Assert.False page.HasNext
            Assert.Equal<string list>([ objectIri ours; "https://r.example/users/bob/statuses/early"; "https://r.example/users/bob/statuses/late" ], page.Items)
        }

    [<Fact>]
    member _.``002 T005 120 replies make pages of 50, 50 and 20, and replies to replies are not in the parent's collection``() : Task =
        task {
            let! db = setup pg
            let! root = object' db "series" None None
            let rootIri = objectIri root
            let p = parent rootIri (Some root) root
            let start = time.GetUtcNow()

            do!
                Db.execute
                    db
                    $"""INSERT INTO replies (id, remote_actor, parent, parent_object, thread_root, audience, published_at)
                        SELECT 'https://r.example/users/bob/statuses/' || lpad(n::text, 4, '0'), 'https://r.example/users/bob', '{rootIri}', '{root}', '{root}', 'public',
                               '{start:O}'::timestamptz + (n || ' seconds')::interval
                        FROM generate_series(1, 120) AS n"""

            // a reply to one of those replies belongs to the thread, not to the root's collection
            let! _ = inTx db (fun tx -> Threads.recordRemoteReply tx time "https://r.example/users/carl/statuses/nested" "https://r.example/users/carl" { p with Iri = "https://r.example/users/bob/statuses/0001"; Object = None } Audience.ReceivedPublic start)

            let! one = Threads.repliesPage db rootIri 1
            let! two = Threads.repliesPage db rootIri 2
            let! three = Threads.repliesPage db rootIri 3
            Assert.Equal(120L, one.Total)
            Assert.Equal((50, true), (one.Items.Length, one.HasNext))
            Assert.Equal((50, true), (two.Items.Length, two.HasNext))
            Assert.Equal((20, false), (three.Items.Length, three.HasNext))
            Assert.Equal("https://r.example/users/bob/statuses/0001", one.Items.Head)
            Assert.Equal("https://r.example/users/bob/statuses/0051", two.Items.Head)
            Assert.Equal("https://r.example/users/bob/statuses/0120", List.last three.Items)
            Assert.Equal(120, (one.Items @ two.Items @ three.Items |> List.distinct).Length)

            // the context holds the root first, then every reply in the thread, 50 ids a page
            let! context1 = Threads.contextPage db domain root 1
            Assert.Equal(122L, context1.Total)
            Assert.Equal(50, context1.Items.Length)
            Assert.Equal(rootIri, context1.Items.Head)
            Assert.True context1.HasNext
            let! context3 = Threads.contextPage db domain root 3
            Assert.Equal(22, context3.Items.Length)
            Assert.False context3.HasNext
            let! context2 = Threads.contextPage db domain root 2
            Assert.Equal(122, (context1.Items @ context2.Items @ context3.Items |> List.distinct).Length)
        }
