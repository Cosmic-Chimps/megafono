/// Feature 005 against real servers (T021, SC-001, SC-002): a local user follows an account on GoToSocial or Mastodon
/// through the app's own call; the account's public and followers-only posts reach the app as `post.received`, an
/// edit as `post.updated`, a deletion as `post.deleted`, each within 30 s. A second local user who does not follow
/// hears nothing, and once the follower blocks the account its next post produces no event.
module Megafono.Interop.Tests.FeedTests

open System
open System.Diagnostics
open System.Net
open System.Net.Http
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open DotNet.Testcontainers.Containers
open Xunit
open Megafono.Tests.Fixtures
open Megafono.Tests.Fixtures.PostgresFixture
open Megafono.Interop.Tests.FediClient
open Megafono.Interop.Tests.FederationContext

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

/// Prints an observation for the research notes (R-60). A passing test shows no output, so
/// `MEGAFONO_OBSERVATIONS=<file>` also appends each line to a file.
let private observe fmt =
    Printf.ksprintf
        (fun (line: string) ->
            printfn "%s" line

            match Environment.GetEnvironmentVariable "MEGAFONO_OBSERVATIONS" with
            | null
            | "" -> ()
            | file -> IO.File.AppendAllText(file, line + "\n"))
        fmt

let private events (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (userId: string) =
    megafono.FakeApp.Received |> List.filter (fun e -> e.Type = eventType && get e.Json "user.id" = userId)

/// The first event of `eventType` for `userId` that `matches`, waiting up to 30 s (SC-001) and noting how long it took
let private eventWithin (limit: float) (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (userId: string) (eventType: string) (what: string) (matches: JsonNode -> bool) =
    task {
        let clock = Stopwatch.StartNew()

        let! event =
            pollUntil (TimeSpan.FromSeconds limit) $"{eventType} for {userId}: {what}" (fun () ->
                task { return events megafono eventType userId |> List.tryFind (fun e -> matches e.Json) })

        observe "%s: %s (%s) after %.1fs" server.Domain eventType what clock.Elapsed.TotalSeconds
        return event
    }

let private eventWhere = eventWithin 30.0

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

/// What the server put on the wire for the feed: path, addressing and identifiers of each Create, Update and Delete
let private dumpWire (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) =
    for request in megafono.InboxRequests do
        let json = try JsonNode.Parse request.Body with _ -> null
        let kind = if isNull json then "?" else (match json["type"] with null -> "?" | t -> t.GetValue<string>())

        if kind = "Create" || kind = "Update" || kind = "Delete" then
            let note = match json["object"] with :? JsonObject as o -> o | _ -> null
            let shown (node: JsonNode) (name: string) = if isNull node then "-" else (match node[name] with null -> "-" | v -> v.ToJsonString())
            let objectShape = if isNull note then (match json["object"] with null -> "-" | v -> v.ToJsonString()) else "Note"
            observe "%s wire: %s %s %d id=%s to=%s cc=%s object.to=%s object.cc=%s object.updated=%s object=%s" server.Domain request.Path kind request.Status (shown json "id") (shown json "to") (shown json "cc") (shown note "to") (shown note "cc") (shown note "updated") objectShape

let private feedScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        // alex follows bob; erin does not
        let alexHandle = uniqueName "alexf"
        let alexId = $"u-{alexHandle}"
        let erinHandle = uniqueName "erinf"
        let erinId = $"u-{erinHandle}"
        megafono.FakeApp.AddUser { FakeApp.user alexId alexHandle with Name = "Alex Feed"; Locked = false; ProfileUrl = $"https://megafono.test/u/{alexHandle}" }
        megafono.FakeApp.AddUser { FakeApp.user erinId erinHandle with Name = "Erin Feed"; Locked = false; ProfileUrl = $"https://megafono.test/u/{erinHandle}" }

        use! bob = server.CreateUser "bob"
        let bobHandle = $"@{bob.Username}@{server.Domain}"

        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{alexId}/following" (Some(JsonSerializer.Serialize {| target = bobHandle |}))
        Assert.Equal(202, status)
        let bobActorId = get answer "actor.id"
        // GoToSocial holds the request of a remote follower until the account approves it; Mastodon accepts at once
        let! _ =
            pollFor "bob's server answers the follow" (fun () ->
                task {
                    if not (events megafono "follow.accepted" alexId).IsEmpty then
                        return Some()
                    else
                        let! held = bob.FollowRequests()

                        match held with
                        | request :: _ ->
                            observe "%s: bob's server held the follow request until approved" server.Domain
                            let! _ = bob.AuthorizeFollowRequest request.Id
                            return Some()
                        | [] -> return None
                })

        // Setting the follow up is not what SC-001 times: the first contact between two servers takes a while
        let! accepted = eventWithin 60.0 server megafono alexId "follow.accepted" "bob accepts" (fun e -> get e "actor.id" = bobActorId)
        Assert.Equal("outbound", get accepted.Json "follow.direction")

        // 1. a public post and a followers-only one reach alex, each with its audience (SC-001)
        let publicMarker = uniqueName "public"
        let! publicPost = bob.PostStatus("public", $"{publicMarker} Started Frieren S2")
        let privateMarker = uniqueName "private"
        let! privatePost = bob.PostStatus("private", $"{privateMarker} only for followers")

        let! publicEvent = eventWhere server megafono alexId "post.received" "the public post" (fun e -> get e "object.id" = publicPost.Uri)
        Assert.True publicEvent.SignatureValid
        Assert.Equal(bobActorId, get publicEvent.Json "actor.id")
        Assert.StartsWith($"@{bob.Username}@", get publicEvent.Json "actor.handle")
        Assert.Equal("public", get publicEvent.Json "object.audience")
        Assert.Contains(publicMarker, get publicEvent.Json "object.content")
        Assert.Equal("Note", get publicEvent.Json "object.type")
        Assert.Null(at publicEvent.Json "object.inReplyTo")
        Assert.False(String.IsNullOrEmpty(get publicEvent.Json "object.published"))
        Assert.Null(at publicEvent.Json "object.updated")
        Assert.Null(at publicEvent.Json "object.parent")

        let! privateEvent = eventWhere server megafono alexId "post.received" "the followers-only post" (fun e -> get e "object.id" = privatePost.Uri)
        Assert.Equal("followers", get privateEvent.Json "object.audience")
        Assert.Contains(privateMarker, get privateEvent.Json "object.content")

        // 2. an edit of the public post: post.updated with the new content, the original audience
        let editMarker = uniqueName "edited"
        let! _ = bob.EditStatus(publicPost.Id, $"{editMarker} still Frieren S2", None)
        let! updated = eventWhere server megafono alexId "post.updated" "the edit" (fun e -> (get e "object.content").Contains editMarker)
        Assert.Equal(publicPost.Uri, get updated.Json "object.id")
        Assert.Equal("public", get updated.Json "object.audience")
        Assert.False(isNull (at updated.Json "object.updated"))
        Assert.Equal(get publicEvent.Json "object.published", get updated.Json "object.published")

        // 3. deleting the followers-only post: post.deleted with just its id
        do! bob.DeleteStatus privatePost.Id
        let! deleted = eventWhere server megafono alexId "post.deleted" "the deletion" (fun e -> get e "object.id" = privatePost.Uri)
        Assert.Equal(bobActorId, get deleted.Json "actor.id")
        Assert.Equal(1, (at deleted.Json "object").AsObject().Count)

        // 3b. a direct post mentioning alex, who follows bob: it reaches alex only, as `direct` (R-52)
        let directMarker = uniqueName "direct"
        let! directPost = bob.PostStatus("direct", $"@{alexHandle}@megafono.test {directMarker} just between us")
        let! directEvent = eventWhere server megafono alexId "post.received" "the direct post" (fun e -> get e "object.id" = directPost.Uri)
        Assert.Equal("direct", get directEvent.Json "object.audience")
        Assert.Contains(directMarker, get directEvent.Json "object.content")

        // 4. erin follows nobody: no feed event for her, whatever bob did (SC-002)
        for kind in [ "post.received"; "post.updated"; "post.deleted" ] do
            Assert.Empty(events megafono kind erinId)

        // 5. alex blocks bob: the next post produces no event (SC-002)
        let! blockStatus, _ = internalCall megafono HttpMethod.Post $"/internal/actors/{alexId}/blocks" (Some(JsonSerializer.Serialize {| actor = bobActorId |}))
        Assert.Equal(201, blockStatus)
        let before = (events megafono "post.received" alexId).Length
        let afterMarker = uniqueName "afterblock"
        let! afterBlock = bob.PostStatus("public", $"{afterMarker} posted after the block")
        let deadline = DateTime.UtcNow.AddSeconds 15.0

        while DateTime.UtcNow < deadline && not (megafono.InboxRequests |> List.exists (fun r -> r.Body.Contains afterMarker)) do
            do! Task.Delay 500

        let arrived = megafono.InboxRequests |> List.filter (fun r -> r.Body.Contains afterMarker)
        observe "%s: the post after the block reached the inbox %d time(s): %s" server.Domain arrived.Length (arrived |> List.map (fun r -> $"{r.Path} {r.Status}") |> String.concat ", ")
        do! Task.Delay 3000
        Assert.Equal(before, (events megafono "post.received" alexId).Length)
        Assert.Empty(events megafono "post.received" alexId |> List.filter (fun e -> get e.Json "object.id" = afterBlock.Uri))
        for kind in [ "post.received"; "post.updated"; "post.deleted" ] do
            Assert.Empty(events megafono kind erinId)

        dumpWire server megafono
    }

let private feedFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]

        try
            do! feedScenario server megafono
        with e ->
            // What reached the app and the inbox, to read a timeout by
            for event in megafono.FakeApp.Received do
                observe "%s event: %s user=%s actor=%s" server.Domain event.Type (get event.Json "user.id") (match at event.Json "actor.id" with null -> "-" | a -> a.GetValue<string>())

            for request in megafono.InboxRequests do
                observe "%s inbox: %s %d %s" server.Domain request.Path request.Status (request.Body.Substring(0, min 160 request.Body.Length))

            let db = megafono.Services.GetService(typeof<Npgsql.NpgsqlDataSource>) :?> Npgsql.NpgsqlDataSource
            use command = db.CreateCommand "SELECT a.activity_type, d.status, d.attempts, d.last_error FROM deliveries d JOIN activities a ON a.id = d.activity ORDER BY a.created_at"
            use! reader = command.ExecuteReaderAsync()

            while reader.Read() do
                observe "%s delivery: %s %s attempts=%d error=%s" server.Domain (reader.GetString 0) (reader.GetString 1) (reader.GetInt32 2) (if reader.IsDBNull 3 then "-" else reader.GetString 3)

            match box server with
            | :? GoToSocialServer.GoToSocialServer as gts ->
                let! text = gts.Logs()
                let lines = text.Split('\n') |> Array.filter (fun l -> l.Contains "level=ERROR" || l.Contains "level=WARN" || l.ToLowerInvariant().Contains "follow") |> Array.truncate 60
                for line in lines do
                    observe "gts log: %s" (if line.Length > 400 then line.Substring(0, 400) else line)
            | _ -> ()

            dumpWire server megafono
            return raise e
    }

type FeedTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 GoToSocial: a followed account's posts, edit and deletion reach the app, a non-follower hears nothing, a block stops it`` () =
        task {
            let! gts = net.GoToSocial()
            do! feedFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonFeedTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 Mastodon: a followed account's posts, edit and deletion reach the app, a non-follower hears nothing, a block stops it`` () =
        task {
            let! mastodon = net.Mastodon()
            do! feedFlow mastodon mastodon.Front net pg
        }
