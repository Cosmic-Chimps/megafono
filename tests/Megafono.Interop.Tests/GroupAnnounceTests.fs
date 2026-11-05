/// Feature 006 against real servers (T021, SC-001): the app's series account is a `Group` with `announcesReplies`;
/// carol on GoToSocial or Mastodon follows it and replies to its post. The Group boosts the reply (`Announce`), so the
/// Group is listed among the reply's boosters (`reblogged_by`); carol's followers-only reply is not announced; her edit
/// is followed by an `Announce` of an `Update` and her deletion by an `Undo` of the `Announce`. What each server
/// showed is printed (R-70).
module Megafono.Interop.Tests.GroupAnnounceTests

open System
open System.Diagnostics
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

/// A field as JSON text, "-" when absent
let private shown (node: JsonNode) (path: string) : string = match at node path with null -> "-" | v -> v.ToJsonString()

/// Prints an observation for the research notes (R-70). A passing test shows no output, so
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

let private events (megafono: MegafonoInterop.InteropMegafono) (eventType: string) =
    megafono.FakeApp.Received |> List.filter (fun e -> e.Type = eventType)

let private eventWithin (limit: float) (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (what: string) (matches: JsonNode -> bool) =
    task {
        let clock = Stopwatch.StartNew()

        let! event =
            pollUntil (TimeSpan.FromSeconds limit) $"{eventType}: {what}" (fun () ->
                task { return events megafono eventType |> List.tryFind (fun e -> matches e.Json) })

        observe "%s: %s (%s) after %.1fs" server.Domain eventType what clock.Elapsed.TotalSeconds
        return event
    }

/// Waits up to `limit` seconds for an event and says whether it came (an observation, not an assertion)
let private eventArrives (limit: float) (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (eventType: string) (what: string) (matches: JsonNode -> bool) =
    task {
        let clock = Stopwatch.StartNew()
        let deadline = DateTime.UtcNow.AddSeconds limit
        let mutable seen = false

        while not seen && DateTime.UtcNow < deadline do
            seen <- events megafono eventType |> List.exists (fun e -> matches e.Json)
            if not seen then do! Task.Delay 500

        observe "%s: %s (%s): %s (waited %.1fs)" server.Domain eventType what (if seen then "ARRIVED" else "did NOT arrive") clock.Elapsed.TotalSeconds
        return seen
    }

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

/// The Group publishes a public Note; returns its object URL (what a reader searches for)
let private publishPublic (megafono: MegafonoInterop.InteropMegafono) (userId: string) (content: string) : Task<string> =
    task {
        let body = JsonObject()
        body["audience"] <- JsonValue.Create "public"
        let inner = JsonObject()
        inner["type"] <- JsonValue.Create "Note"
        inner["content"] <- JsonValue.Create $"<p>{content}</p>"
        body["object"] <- inner
        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{userId}/objects" (Some(body.ToJsonString()))
        Assert.Equal(201, status)
        return answer["objectUrl"].GetValue<string>()
    }

/// One row of the delivery queue with its activity: the Group's own activities, as Megáfono queued them
type private Queued =
    { Type: string
      Body: JsonNode
      Status: string
      Inbox: string
      Attempts: int
      Error: string }

let private queued (megafono: MegafonoInterop.InteropMegafono) : Task<Queued list> =
    task {
        let db = megafono.Services.GetService(typeof<Npgsql.NpgsqlDataSource>) :?> Npgsql.NpgsqlDataSource
        use command = db.CreateCommand "SELECT a.activity_type, convert_from(a.body, 'UTF8'), d.status, d.inbox_url, d.attempts, d.last_error FROM activities a LEFT JOIN deliveries d ON d.activity = a.id ORDER BY a.created_at, d.inbox_url"
        use! reader = command.ExecuteReaderAsync()
        let rows = ResizeArray<Queued>()

        while reader.Read() do
            rows.Add
                { Type = reader.GetString 0
                  Body = JsonNode.Parse(reader.GetString 1)
                  Status = (if reader.IsDBNull 2 then "-" else reader.GetString 2)
                  Inbox = (if reader.IsDBNull 3 then "-" else reader.GetString 3)
                  Attempts = (if reader.IsDBNull 4 then 0 else reader.GetInt32 4)
                  Error = (if reader.IsDBNull 5 then "-" else reader.GetString 5) }

        return List.ofSeq rows
    }

/// The Group's `Announce` of the reply itself (the object is the reply's id as a string)
let private isAnnounceOf (replyUri: string) (row: Queued) =
    row.Type = "Announce" && (match row.Body["object"] with :? JsonValue as v -> v.GetValueKind() = JsonValueKind.String && v.GetValue<string>() = replyUri | _ -> false)

/// The Group's `Announce` of an `Update` of the reply
let private isAnnounceUpdateOf (replyUri: string) (row: Queued) =
    row.Type = "Announce" && (match row.Body["object"] with :? JsonObject -> get row.Body "object.type" = "Update" && get row.Body "object.object" = replyUri | _ -> false)

/// The Group's `Undo` of its `Announce` of the reply
let private isUndoOf (replyUri: string) (row: Queued) =
    row.Type = "Undo" && get row.Body "object.type" = "Announce" && get row.Body "object.object" = replyUri

let private showRows (server: IFediServer) (what: string) (rows: Queued list) =
    for row in rows do
        observe "%s: %s delivery %s %s attempts=%d error=%s" server.Domain what row.Type row.Status row.Attempts row.Error

/// Waits up to `limit` seconds until the activities picked by `pick` exist and every delivery of them is `delivered`
let private deliveredWithin (limit: float) (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (what: string) (pick: Queued -> bool) =
    task {
        let clock = Stopwatch.StartNew()

        let! rows =
            pollUntil (TimeSpan.FromSeconds limit) $"{what} queued and delivered" (fun () ->
                task {
                    let! all = queued megafono
                    let mine = all |> List.filter pick
                    return (if not mine.IsEmpty && mine |> List.forall (fun r -> r.Status = "delivered") then Some mine else None)
                })

        observe "%s: %s delivered after %.1fs" server.Domain what clock.Elapsed.TotalSeconds
        showRows server what rows
        return rows
    }

/// What reached the inbox for one reply, by activity type: its author's own `Update` and `Delete` reach Megáfono as
/// a booster's server (nobody here follows the author), answered with 2xx
let private dumpInbox (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (replyUri: string) =
    for request in megafono.InboxRequests do
        let json = try JsonNode.Parse request.Body with _ -> null

        if not (isNull json) && request.Body.Contains replyUri then
            let kind = match json["type"] with null -> "?" | t -> t.GetValue<string>()
            observe "%s inbox: %s %s %d" server.Domain request.Path kind request.Status

let private announceScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        // A series-like Group that boosts the replies in its threads; unique per run (servers cache actors)
        let handle = uniqueName "series"
        let groupId = $"g-{handle}"
        let groupHandle = $"{handle}@megafono.test"

        megafono.FakeApp.AddUser
            { FakeApp.user groupId handle with
                Name = "Series Group"
                Type = "Group"
                Locked = false
                AnnouncesReplies = Some true
                ProfileUrl = $"https://megafono.test/series/{handle}" }

        use! carol = server.CreateUser "carol"

        // carol follows the Group (unlocked: accepted at once)
        let! found = pollFor "carol finds the Group" (fun () -> task { return! carol.SearchAccount(groupHandle, true) })
        let! _ = carol.Follow found.Id

        let! _ =
            pollFor "carol follows the Group" (fun () ->
                task {
                    let! r = carol.Relationship found.Id
                    return (if r.Following then Some r else None)
                })

        // The Group publishes a post; carol finds it by its URL and replies publicly, mentioning the Group like a client's reply box
        let! rootUrl = publishPublic megafono groupId "Episode 7 thread"

        let! thread =
            pollFor "carol resolves the thread post" (fun () ->
                task {
                    let! found = carol.SearchStatus rootUrl
                    return List.tryHead found
                })

        let marker = uniqueName "reply"
        let clock = Stopwatch.StartNew()
        let! reply = carol.PostReply(thread.Id, "public", $"@{groupHandle} {marker} that ending!", None)
        let! received = eventWithin 60.0 server megafono "reply.received" "carol's public reply" (fun e -> (get e "object.content").Contains marker)
        Assert.Equal(groupId, get received.Json "user.id")
        Assert.Equal(reply.Uri, get received.Json "object.id")
        let receivedAt = clock.Elapsed

        // 1. SC-001: within 30 s the Group is among the boosters of carol's reply, and the Announce was delivered
        let! boosters =
            pollUntil (TimeSpan.FromSeconds 30.0) "the Group lists among the boosters of carol's reply" (fun () ->
                task {
                    let! accounts = carol.RebloggedBy reply.Id
                    return (if accounts |> List.exists (fun a -> a.Username = handle) then Some accounts else None)
                })

        observe "%s: reblogged_by of carol's reply (%.1fs after posting, %.1fs after reply.received): %s" server.Domain clock.Elapsed.TotalSeconds (clock.Elapsed - receivedAt).TotalSeconds (boosters |> List.map (fun a -> $"{a.Acct} group={a.Group} url={a.Url}") |> String.concat "; ")
        let! announced = deliveredWithin 30.0 server megafono "the Announce of carol's reply" (isAnnounceOf reply.Uri)
        let announce = announced.Head.Body
        Assert.Equal($"https://megafono.test/ap/actors/{groupId}", get announce "actor")
        observe "%s: Announce to=%s cc=%s inboxes=%s" server.Domain (shown announce "to") (shown announce "cc") (announced |> List.map (fun r -> r.Inbox) |> String.concat ", ")

        // 2. a followers-only reply reaches the app but is not announced
        let privateMarker = uniqueName "private"
        let! privateReply = carol.PostReply(thread.Id, "private", $"@{groupHandle} {privateMarker} only for followers", None)
        let! privateReceived = eventWithin 60.0 server megafono "reply.received" "carol's followers-only reply" (fun e -> (get e "object.content").Contains privateMarker)
        Assert.Equal(privateReply.Uri, get privateReceived.Json "object.id")
        Assert.Equal("followers", get privateReceived.Json "object.audience")
        do! Task.Delay 5000
        let! afterPrivate = queued megafono
        Assert.Empty(afterPrivate |> List.filter (fun r -> r.Body.ToJsonString().Contains privateReply.Uri))
        observe "%s: no Announce activity for the followers-only reply (%d activities queued in all)" server.Domain (afterPrivate |> List.map (fun r -> r.Body["id"].GetValue<string>()) |> List.distinct |> List.length)
        let! boostersOfPrivate = carol.RebloggedBy privateReply.Id
        observe "%s: reblogged_by of the followers-only reply: %d" server.Domain boostersOfPrivate.Length

        // 3. carol edits her public reply. First without the mention: nobody here follows carol, so the Update reaches
        // Megáfono only if her server sends it to the servers that boosted the reply (observed, not asserted)
        let boosterMarker = uniqueName "nomention"
        let! _ = carol.EditStatus(reply.Id, $"{boosterMarker} still that ending", None)
        let! _ = eventArrives 15.0 server megafono "reply.updated" "carol's edit without the mention, nobody here follows her" (fun e -> (get e "object.content").Contains boosterMarker)

        // Mastodon names an Update after the edit's second (`…#updates/{unix time}`, `updated` to the second): a second
        // edit within the same second repeats the first one's id and time, which Megáfono takes for a repeat (002 R-25)
        do! Task.Delay 1500

        // Then with the mention, as a client's reply box keeps it: the Update reaches the Group, which announces it
        let editMarker = uniqueName "edited"
        let! _ = carol.EditStatus(reply.Id, $"@{groupHandle} {editMarker} still that ending", None)
        let! updated = eventWithin 30.0 server megafono "reply.updated" "carol's edit" (fun e -> (get e "object.content").Contains editMarker)
        Assert.Equal(reply.Uri, get updated.Json "object.id")
        let! announcedUpdate = deliveredWithin 30.0 server megafono "the Announce of the Update" (isAnnounceUpdateOf reply.Uri)
        let u = announcedUpdate.Head.Body
        observe "%s: Announce{Update} object.id=%s object.actor=%s to=%s cc=%s" server.Domain (get u "object.id") (get u "object.actor") (shown u "to") (shown u "cc")

        // The server answered 2xx to the Announce of the Update; the boost is still there, unchanged (observed)
        do! Task.Delay 3000
        let! boostersAfterUpdate = carol.RebloggedBy reply.Id
        observe "%s: reblogged_by after the Announce{Update}: %s" server.Domain (boostersAfterUpdate |> List.map (fun a -> a.Acct) |> String.concat "; ")

        // 4. carol deletes it: object.deleted, then the Group's Undo of the Announce
        do! carol.DeleteStatus reply.Id
        let! deleted = eventWithin 30.0 server megafono "object.deleted" "carol's deletion" (fun e -> get e "object.id" = reply.Uri)
        Assert.Equal(groupId, get deleted.Json "user.id")
        let! undone = deliveredWithin 30.0 server megafono "the Undo of the Announce" (isUndoOf reply.Uri)
        let undo = undone.Head.Body
        Assert.Equal(get announce "id", get undo "object.id")
        observe "%s: Undo object.id=%s to=%s cc=%s" server.Domain (get undo "object.id") (shown undo "to") (shown undo "cc")

        // 5. The booster-only path for a deletion (observed, not asserted): a second public reply, announced, then edited
        // to drop the mention and deleted. Does the Delete reach Megáfono, which nobody here follows carol through?
        let lonelyMarker = uniqueName "lonely"
        let! lonely = carol.PostReply(thread.Id, "public", $"@{groupHandle} {lonelyMarker} one more thought", None)
        let! _ = eventWithin 60.0 server megafono "reply.received" "carol's second public reply" (fun e -> (get e "object.content").Contains lonelyMarker)
        let! _ = deliveredWithin 30.0 server megafono "the Announce of carol's second reply" (isAnnounceOf lonely.Uri)
        let! _ = carol.EditStatus(lonely.Id, $"{lonelyMarker} one more thought, without the mention", None)
        do! carol.DeleteStatus lonely.Id
        let! lonelyDeleted = eventArrives 15.0 server megafono "object.deleted" "carol's deletion of the second reply without the mention" (fun e -> get e "object.id" = lonely.Uri)
        if lonelyDeleted then
            let! _ = deliveredWithin 30.0 server megafono "the Undo of the second Announce" (isUndoOf lonely.Uri)
            ()

        dumpInbox server megafono reply.Uri
        dumpInbox server megafono lonely.Uri
    }

let private announceFlow (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]

        try
            do! announceScenario server megafono
        with e ->
            // What reached the app and the inbox, and what the queue says, to read a timeout by
            for event in megafono.FakeApp.Received do
                observe "%s event: %s user=%s" server.Domain event.Type (get event.Json "user.id")

            for request in megafono.InboxRequests do
                observe "%s inbox: %s %d %s" server.Domain request.Path request.Status (request.Body.Substring(0, min 200 request.Body.Length))

            let! rows = queued megafono
            showRows server "failure" rows

            match box server with
            | :? GoToSocialServer.GoToSocialServer as gts ->
                let! text = gts.Logs()
                let lines = text.Split('\n') |> Array.filter (fun l -> l.Contains "level=ERROR" || l.Contains "level=WARN") |> Array.truncate 40

                for line in lines do
                    observe "gts log: %s" (if line.Length > 400 then line.Substring(0, 400) else line)
            | _ -> ()

            return raise e
    }

type GroupAnnounceTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 GoToSocial: a Group announces a follower's reply (reblogged_by), not a followers-only one, and follows its edit and deletion`` () =
        task {
            let! gts = net.GoToSocial()
            do! announceFlow gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonGroupAnnounceTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``US1 Mastodon: a Group announces a follower's reply (reblogged_by), not a followers-only one, and follows its edit and deletion`` () =
        task {
            let! mastodon = net.Mastodon()
            do! announceFlow mastodon mastodon.Front net pg
        }
