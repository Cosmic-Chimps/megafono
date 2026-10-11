/// User Story 3 of feature 002 against real servers (T017, T018): a reader on Mastodon who follows nobody in the
/// thread opens it and sees the replies Megáfono lists (FR-020, SC-003), and an app page that links to the thread
/// post resolves on Mastodon while GoToSocial finds the post by its own URL (R-21, SC-005).
module Megafono.Interop.Tests.ThreadLoadingTests

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Npgsql
open NpgsqlTypes
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

let private publishNote (megafono: MegafonoInterop.InteropMegafono) (userId: string) (content: string) : Task<Guid * string> =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        let o = JsonObject()
        o["audience"] <- JsonValue.Create "public"
        let inner = JsonObject()
        inner["type"] <- JsonValue.Create "Note"
        inner["content"] <- JsonValue.Create $"<p>{content}</p>"
        o["object"] <- inner
        use request = new HttpRequestMessage(HttpMethod.Post, $"{megafono.InternalUrl}/internal/actors/{userId}/objects")
        request.Content <- new StringContent(o.ToJsonString(), Text.Encoding.UTF8, "application/json")
        use! response = client.SendAsync request
        Assert.Equal(Net.HttpStatusCode.Created, response.StatusCode)
        let! text = response.Content.ReadAsStringAsync()
        let id = Guid.Parse(string ((JsonNode.Parse text).["id"]))
        return id, $"https://megafono.test/ap/objects/{id}"
    }

/// A series Group of the fake app (unique id and handle per run: servers cache what they have seen)
let private addGroup (megafono: MegafonoInterop.InteropMegafono) : string * string =
    let handle = uniqueName "series"
    let id = $"series-{handle}"
    megafono.FakeApp.AddUser { FakeApp.user id handle with Name = "A Series"; Type = "Group"; Locked = false; ProfileUrl = $"https://megafono.test/u/{handle}" }
    id, handle

/// The app's own reply to `rootGuid`, stored the way the publish path will store it (a stored document and a row of
/// `replies`). Publishing a reply is user story 2's job; this test is about how readers load what is stored.
let private storeLocalReply (megafono: MegafonoInterop.InteropMegafono) (userId: string) (rootGuid: Guid) (text: string) : Task<string> =
    task {
        let db = megafono.Services.GetService(typeof<NpgsqlDataSource>) :?> NpgsqlDataSource
        let id = Guid.NewGuid()
        let rootIri = Ids.objectId MegafonoDomain rootGuid
        let replyIri = Ids.objectId MegafonoDomain id
        let published = DateTimeOffset.UtcNow

        let content: ActivityStreams.ObjectContent =
            { ObjectType = "Note"; Content = $"<p>{text}</p>"; Url = None; InReplyTo = Some rootIri; Summary = None; Sensitive = false; Attachments = [] }

        let document =
            ActivityStreams.noteObjectIn MegafonoDomain userId id Audience.Public published None content None { Root = rootGuid; ExtraCc = []; Mentions = [] }

        use command =
            db.CreateCommand
                """INSERT INTO objects (id, local_actor, audience, object_type, document, in_reply_to, thread_root)
                   VALUES (@id, @user, 'public', 'Note', @document, @parent, @root);
                   INSERT INTO replies (id, local_object, parent, parent_object, thread_root, audience, state, published_at)
                   VALUES (@iri, @id, @parent, @root, @root, 'public', 'live', @published)"""

        command.Parameters.AddWithValue("id", id) |> ignore
        command.Parameters.AddWithValue("user", userId) |> ignore
        command.Parameters.AddWithValue("document", NpgsqlDbType.Jsonb, document.ToJsonString()) |> ignore
        command.Parameters.AddWithValue("parent", rootIri) |> ignore
        command.Parameters.AddWithValue("root", rootGuid) |> ignore
        command.Parameters.AddWithValue("iri", replyIri) |> ignore
        command.Parameters.AddWithValue("published", published.UtcDateTime) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        return replyIri
    }

let private resolveStatus (reader: FediClient) (url: string) : Task<Status> =
    pollFor $"{reader.Handle} resolves {url}" (fun () ->
        task {
            let! found = reader.SearchStatus url
            return List.tryHead found
        })

/// T017, fast part: the reader resolves the thread post, and the app's own reply is in the thread at once
let private localReplyFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        let groupId, _ = addGroup megafono
        let! rootGuid, rootUrl = publishNote megafono groupId "Episode 7 thread"
        let marker = uniqueName "local"
        let! _ = storeLocalReply megafono groupId rootGuid $"{marker} the app answers"

        use! reader = server.CreateUser "reader"
        let! thread = resolveStatus reader rootUrl

        let! _, descendants =
            pollFor "the app's reply in the thread's context" (fun () ->
                task {
                    let! _, below = reader.Context thread.Id
                    return (if below |> List.exists (fun s -> s.Content.Contains marker) then Some((), below) else None)
                })

        printfn "%s: context of the thread post shows %d descendant(s) after resolving it" server.Domain descendants.Length
        Assert.Contains(descendants, fun s -> s.Content.Contains marker)
    }

/// T017, slow part: a GoToSocial user's reply (recorded by Megáfono, listed in `replies`) reaches the reader's view of
/// the thread once Mastodon crawls the collection (statuses at least 5 minutes old)
let private remoteReplyFlow (mastodon: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        let! gts = net.GoToSocial()
        use! megafono = MegafonoInterop.start net pg [ mastodon.Domain, front; GtsDomain, gts.Front ]
        let groupId, handle = addGroup megafono
        let! _, rootUrl = publishNote megafono groupId "Episode 8 thread"

        use! alice = gts.CreateUser "alice"
        let! aliceView = resolveStatus alice rootUrl
        let marker = uniqueName "gtsreply"
        // The Group is mentioned, as a client's reply box does: GoToSocial delivers a reply to the people it mentions
        let! _ = alice.PostReply(aliceView.Id, "public", $"@{handle}@megafono.test {marker} from GoToSocial", None)

        let! _ =
            pollFor "Megáfono records the GoToSocial reply" (fun () ->
                task {
                    let db = megafono.Services.GetService(typeof<NpgsqlDataSource>) :?> NpgsqlDataSource
                    use command = db.CreateCommand "SELECT count(*) FROM replies WHERE state = 'live'"
                    let! n = command.ExecuteScalarAsync()
                    return (if (n :?> int64) >= 1L then Some() else None)
                })

        use! reader = mastodon.CreateUser "reader"
        let! thread = resolveStatus reader rootUrl
        let started = DateTime.UtcNow

        let! seen =
            pollUntil (TimeSpan.FromMinutes 7.5) "the GoToSocial reply in the Mastodon thread" (fun () ->
                task {
                    let! _, below = reader.Context thread.Id
                    do! Task.Delay 10000
                    return (if below |> List.exists (fun s -> s.Content.Contains marker) then Some below else None)
                })

        printfn "mastodon.test: the GoToSocial reply showed up after %.0f s" (DateTime.UtcNow - started).TotalSeconds
        Assert.Contains(seen, fun s -> s.Content.Contains marker)
    }

/// T018: the episode page's alternate link makes the thread post findable by the page URL
let private pastedPageFlow (server: IFediServer) (front: IContainer) (pastePage: bool) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        let groupId, _ = addGroup megafono
        let! rootGuid, rootUrl = publishNote megafono groupId "Episode 9 thread"
        use! reader = server.CreateUser "reader"

        let url = if pastePage then $"https://megafono.test/episodes/{rootGuid}" else rootUrl
        let! found = resolveStatus reader url
        Assert.Contains("Episode 9 thread", found.Content)
        Assert.Equal(rootUrl, found.Uri)
    }

type ThreadLoadingTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US3 GoToSocial: the thread post is found by its own URL`` () =
        task {
            let! gts = net.GoToSocial()
            do! pastedPageFlow gts gts.Front false net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonThreadLoadingTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US3 Mastodon: a reader who follows nobody sees the app's reply in the thread`` () =
        task {
            let! mastodon = net.Mastodon()
            do! localReplyFlow mastodon mastodon.Front net pg
        }

    [<Fact>]
    member _.``US3 Mastodon: pasting the episode page URL finds the thread post`` () =
        task {
            let! mastodon = net.Mastodon()
            do! pastedPageFlow mastodon mastodon.Front true net pg
        }

[<Trait("Suite", "mastodon-slow")>]
type MastodonSlowThreadLoadingTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact(Explicit = true)>]
    member _.``US3 Mastodon (slow): a GoToSocial reply appears in the thread after Mastodon's crawl`` () =
        task {
            let! mastodon = net.Mastodon()
            do! remoteReplyFlow mastodon mastodon.Front net pg
        }
