/// User Story 1 of feature 002 against real servers (T011): replies from GoToSocial and Mastodon to a post that a
/// local Group published reach the app as `reply.received`; an edit gives `reply.updated`; a delete gives
/// `object.deleted` (SC-001, SC-006). The remote users find the post by searching its URL with `resolve=true`, as a
/// reader would, and reply through the client API.
module Megafono.Interop.Tests.RepliesTests

open System
open System.Net.Http
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

/// A string, a number or a flag at a dotted path of a JSON document
let private at (node: JsonNode) (path: string) : JsonNode =
    path.Split('.')
    |> Array.fold
        (fun (n: JsonNode) key ->
            match n with
            | null -> null
            | :? JsonArray as a -> a[int key]
            | o -> o[key])
        node

let private get (node: JsonNode) (path: string) : string = (at node path).GetValue<string>()

let private events (megafono: MegafonoInterop.InteropMegafono) (eventType: string) =
    megafono.FakeApp.Received |> List.filter (fun e -> e.Type = eventType)

/// The first event of `eventType` that `matches`, waiting up to a minute for it
let private eventWhere (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (what: string) (matches: JsonNode -> bool) =
    pollFor $"{eventType}: {what}" (fun () ->
        task { return events megafono eventType |> List.tryFind (fun e -> matches e.Json) })

let private internalCall (megafono: MegafonoInterop.InteropMegafono) (httpMethod: HttpMethod) (path: string) (body: string option) : Task<int * JsonNode> =
    task {
        use client = new HttpClient()
        client.DefaultRequestHeaders.Authorization <- Headers.AuthenticationHeaderValue("Bearer", MegafonoInterop.token)
        use request = new HttpRequestMessage(httpMethod, megafono.InternalUrl + path)
        body |> Option.iter (fun b -> request.Content <- new StringContent(b, Text.Encoding.UTF8, "application/json"))
        use! response = client.SendAsync request
        let! text = response.Content.ReadAsStringAsync()
        return int response.StatusCode, (if String.IsNullOrWhiteSpace text then null else JsonNode.Parse text)
    }

let private publishNote (megafono: MegafonoInterop.InteropMegafono) (userId: string) (content: string) : Task<string * string> =
    task {
        let o = JsonObject()
        o["audience"] <- JsonValue.Create "public"
        let inner = JsonObject()
        inner["type"] <- JsonValue.Create "Note"
        inner["content"] <- JsonValue.Create $"<p>{content}</p>"
        o["object"] <- inner
        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{userId}/objects" (Some(o.ToJsonString()))
        Assert.Equal(201, status)
        let id = answer["id"].GetValue<string>()
        return id, $"https://megafono.test/ap/objects/{id}"
    }

/// What the server actually put on the wire to our inbox, for the research notes (R-22)
let private dumpWire (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) =
    for request in megafono.InboxRequests do
        let json = try JsonNode.Parse request.Body with _ -> null
        let kind = if isNull json then "?" else (match json["type"] with null -> "?" | t -> t.GetValue<string>())
        let note = if isNull json then null else (match json["object"] with :? JsonObject as o -> o | _ -> null)

        let addressed (node: JsonNode) (name: string) =
            if isNull node then "-" else (match node[name] with null -> "-" | v -> v.ToJsonString())

        printfn "%s inbox %s: %s %d to=%s cc=%s object.to=%s object.cc=%s tag=%s" server.Domain request.Path kind request.Status (addressed json "to") (addressed json "cc") (addressed note "to") (addressed note "cc") (addressed note "tag")

/// When a step times out: what reached our inbox, what the queue says, and what the server complained about
let private explain (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (e: exn) : Task<exn> =
    task {
        dumpWire server megafono
        let db = megafono.Services.GetService(typeof<Npgsql.NpgsqlDataSource>) :?> Npgsql.NpgsqlDataSource
        use command = db.CreateCommand "SELECT a.activity_type, d.status, d.attempts, d.last_error FROM deliveries d JOIN activities a ON a.id = d.activity ORDER BY a.created_at"
        use! reader = command.ExecuteReaderAsync()
        let lines = ResizeArray<string>()

        while reader.Read() do
            let error = if reader.IsDBNull 3 then "-" else reader.GetString 3
            lines.Add $"{reader.GetString 0} {reader.GetString 1} attempts={reader.GetInt32 2} error={error}"

        let! logs =
            match box server with
            | :? GoToSocialServer.GoToSocialServer as gts ->
                task {
                    let! text = gts.Logs()
                    return text.Split('\n') |> Array.filter (fun l -> l.Contains "megafono" || l.Contains "eliver" || l.Contains "level=ERROR") |> Array.rev |> Array.truncate 40 |> Array.rev |> String.concat "\n"
                }
            | _ -> Task.FromResult ""

        let deliveries = String.Join("; ", lines)
        return Exception($"{e.Message}\ninbox requests: {megafono.InboxRequests.Length}\ndeliveries: {deliveries}\n{logs}", e)
    }

let private threadScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        // A series-like Group: a person-less account that publishes the thread post (unique id and handle per run)
        let handle = uniqueName "series"
        let groupId = $"series-{handle}"
        megafono.FakeApp.AddUser { FakeApp.user groupId handle with Name = "A Series"; Type = "Group"; Locked = false; ProfileUrl = $"https://megafono.test/u/{handle}" }

        let! rootId, rootUrl = publishNote megafono groupId "Episode 5 thread"
        use! alice = server.CreateUser "alice"
        use! bob = server.CreateUser "bob"

        // alice finds the post by its URL, then replies with a content warning. Like a client's reply box she mentions
        // the post's author: a status reaches the people it mentions, and the Group does not follow alice.
        let! thread =
            pollFor "alice resolves the thread post" (fun () ->
                task {
                    let! found = alice.SearchStatus rootUrl
                    return List.tryHead found
                })

        let marker = uniqueName "reply"
        let! aliceReply = alice.PostReply(thread.Id, "public", $"@{handle}@megafono.test {marker} that ending!", Some "Spoilers: S2 E5")
        Assert.Equal(thread.Id, aliceReply.InReplyToId)

        let! received =
            eventWhere megafono "reply.received" "alice's reply" (fun e -> (get e "object.content").Contains marker)

        Assert.True received.SignatureValid
        Assert.Equal(groupId, get received.Json "user.id")
        Assert.Equal(handle, get received.Json "user.handle")
        Assert.StartsWith("@alice", get received.Json "actor.handle")
        Assert.Equal(aliceReply.Uri, get received.Json "object.id")
        Assert.Equal("Note", get received.Json "object.type")
        Assert.Equal("Spoilers: S2 E5", get received.Json "object.summary")
        Assert.True((at received.Json "object.sensitive").GetValue<bool>())
        Assert.Equal(rootUrl, get received.Json "object.inReplyTo")
        Assert.True((at received.Json "object.parent.local").GetValue<bool>())
        Assert.Equal(rootId, get received.Json "object.parent.objectId")
        Assert.Equal(groupId, get received.Json "object.parent.userId")
        Assert.Equal(rootId, get received.Json "object.thread.objectId")
        Assert.Equal(rootUrl, get received.Json "object.thread.id")
        Assert.Equal("public", get received.Json "object.audience")
        Assert.False(String.IsNullOrEmpty(get received.Json "object.published"))
        printfn "%s reply.received: language=%s url=%s published=%s" server.Domain (match at received.Json "object.language" with null -> "(none)" | l -> l.GetValue<string>()) (get received.Json "object.url") (get received.Json "object.published")

        // bob replies to alice's reply, mentioning her and the Group the way a client's reply box prefills them
        let nestedMarker = uniqueName "nested"
        // the same server, so alice's reply has a local id for bob
        let! bobTarget = bob.GetStatus aliceReply.Id

        let! bobReply = bob.PostReply(bobTarget.Id, "public", $"@{alice.Username} @{handle}@megafono.test {nestedMarker}", None)
        Assert.Equal(bobTarget.Id, bobReply.InReplyToId)

        let! nested =
            eventWhere megafono "reply.received" "bob's reply to alice's reply" (fun e -> (get e "object.content").Contains nestedMarker)

        Assert.Equal(groupId, get nested.Json "user.id")
        Assert.Equal(aliceReply.Uri, get nested.Json "object.inReplyTo")
        Assert.False((at nested.Json "object.parent.local").GetValue<bool>())
        Assert.Equal(rootId, get nested.Json "object.thread.objectId")

        // alice edits her reply: reply.updated with the new content
        let editMarker = uniqueName "edited"
        let! _ = alice.EditStatus(aliceReply.Id, $"@{handle}@megafono.test {editMarker} still that ending", Some "Spoilers: S2 E5 (edited)")

        let! updated =
            eventWhere megafono "reply.updated" "alice's edit" (fun e -> (get e "object.content").Contains editMarker)

        Assert.Equal(aliceReply.Uri, get updated.Json "object.id")
        Assert.Equal("Spoilers: S2 E5 (edited)", get updated.Json "object.summary")
        Assert.False(isNull (at updated.Json "object.updated"))
        Assert.Equal(groupId, get updated.Json "user.id")

        // and deletes it: object.deleted
        do! alice.DeleteStatus aliceReply.Id

        let! deleted =
            eventWhere megafono "object.deleted" "alice's delete" (fun e -> get e "object.id" = aliceReply.Uri)

        Assert.Equal(rootId, get deleted.Json "object.thread.objectId")
        Assert.StartsWith("@alice", get deleted.Json "actor.handle")

        dumpWire server megafono
    }

let private threadFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        try
            do! threadScenario server megafono
        with e ->
            let! explained = explain server megafono e
            return raise explained
    }

type RepliesTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 GoToSocial: replies to a Group's thread post reach the app, nested replies keep the thread, an edit and a delete follow`` () =
        task {
            let! gts = net.GoToSocial()
            do! threadFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonRepliesTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 Mastodon: replies to a Group's thread post reach the app, nested replies keep the thread, an edit and a delete follow`` () =
        task {
            let! mastodon = net.Mastodon()
            do! threadFlow mastodon mastodon.Front net pg
        }
