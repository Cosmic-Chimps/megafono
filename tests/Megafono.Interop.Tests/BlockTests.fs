/// Feature 004 (blocks) against real servers (T027, R-50): GoToSocial and Mastodon take a user block, an instance block
/// and a remote block the way Megáfono expects.
///   (a) SC-001: the app blocks a remote user by handle; the remote server drops both follows on receiving `Block`, and
///       no reply from that user reaches the app.
///   (b) SC-002: the app blocks the remote server's domain; a reply from its user is answered 403 before any work and
///       never reaches the app, and the server cannot look a local actor up.
///   (c) SC-003: the remote user blocks the local actor through the client API; both follows end in Megáfono, with one
///       `follow.removed` for each direction.
module Megafono.Interop.Tests.BlockTests

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
    path.Split('.') |> Array.fold (fun (n: JsonNode) key -> match n with null -> null | o -> o[key]) node

let private get (node: JsonNode) (path: string) : string = (at node path).GetValue<string>()

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

/// Prints an observation for the research notes. A passing test shows no output, so `MEGAFONO_OBSERVATIONS=<file>`
/// also appends each line to a file.
let private observe fmt =
    Printf.ksprintf
        (fun (line: string) ->
            printfn "%s" line

            match Environment.GetEnvironmentVariable "MEGAFONO_OBSERVATIONS" with
            | null
            | "" -> ()
            | path -> IO.File.AppendAllText(path, line + "\n"))
        fmt

let private json (value: obj) = JsonSerializer.Serialize value

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

/// `(actor id, status)` of each row of a follower or following list
let private listed (megafono: MegafonoInterop.InteropMegafono) (path: string) : Task<(string * string) list> =
    task {
        let! status, answer = internalCall megafono HttpMethod.Get path None
        Assert.Equal(200, status)
        return [ for item in (answer["items"] :?> JsonArray) -> get item "actor.id", get item "status" ]
    }

let private events (megafono: MegafonoInterop.InteropMegafono) (eventType: string) =
    megafono.FakeApp.Received |> List.filter (fun e -> e.Type = eventType)

/// The deliveries Megáfono queued, for the observation notes and for failure messages
let private deliverySummary (megafono: MegafonoInterop.InteropMegafono) : Task<string> =
    task {
        let db = megafono.Services.GetService(typeof<Npgsql.NpgsqlDataSource>) :?> Npgsql.NpgsqlDataSource
        use command = db.CreateCommand "SELECT a.activity_type, d.status, d.attempts, d.inbox_url, d.last_error FROM deliveries d JOIN activities a ON a.id = d.activity ORDER BY a.created_at"
        use! reader = command.ExecuteReaderAsync()
        let lines = ResizeArray<string>()

        while reader.Read() do
            let error = if reader.IsDBNull 4 then "-" else reader.GetString 4
            lines.Add $"{reader.GetString 0} {reader.GetString 1} attempts={reader.GetInt32 2} inbox={reader.GetString 3} error={error}"

        return String.Join("; ", lines)
    }

let private activityType (body: string) =
    try
        match (JsonNode.Parse body)["type"] with
        | null -> "?"
        | t -> t.GetValue<string>()
    with _ -> "?"

/// `Undo{Follow}` or `Undo{Block}`: the type of what an Undo undoes (the object may be an id or an embedded activity)
let private undoes (body: string) =
    try
        match (JsonNode.Parse body)["object"] with
        | :? JsonObject as o -> (match o["type"] with null -> "?" | t -> t.GetValue<string>())
        | _ -> "(id)"
    with _ -> "?"

/// What the remote server put on the wire to our inbox, to read the observations from the test output
let private dumpWire (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) =
    for request in megafono.InboxRequests do
        let kind = activityType request.Body
        observe "%s inbox %s: %s%s -> %d" server.Domain request.Path kind (if kind = "Undo" then "{" + undoes request.Body + "}" else "") request.Status

/// When a step times out: the wire, the queue and the webhooks that did arrive
let private explain (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (e: exn) : Task<exn> =
    task {
        dumpWire server megafono
        let! deliveries = deliverySummary megafono
        let webhooks = megafono.FakeApp.Received |> List.map (fun w -> w.Type) |> String.concat ","

        let! logs =
            match box server with
            | :? GoToSocialServer.GoToSocialServer as gts ->
                task {
                    let! text = gts.Logs()
                    return text.Split('\n') |> Array.filter (fun l -> (l.Contains "level=ERROR" || l.Contains "level=WARN" || l.Contains "Follow" || l.Contains "Accept" || l.Contains "alexb") && not (l.Contains "migrations") && not (l.Contains "couldn't count")) |> Array.rev |> Array.truncate 60 |> Array.rev |> String.concat "\n"
                }
            | _ -> Task.FromResult ""

        return Exception($"{e.Message}\ndeliveries: {deliveries}\nwebhooks: {webhooks}\n{logs}", e)
    }

let private withExplanation (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (body: unit -> Task) : Task =
    task {
        try
            do! body ()
        with e ->
            let! explained = explain server megafono e
            return raise explained
    }

type private Pair =
    { AlexId: string
      AlexHandle: string
      Carol: FediClient
      /// Alex as carol's server knows him
      AlexRemote: Account
      CarolActorId: string
      CarolHandle: string }

/// A local unlocked actor (alex) and a user of the remote server (carol) that follow each other
let private mutualFollows (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task<Pair> =
    task {
        let alexHandle = uniqueName "alexb"
        let alexId = $"u-{alexHandle}"
        megafono.FakeApp.AddUser { FakeApp.user alexId alexHandle with Name = "Alex Blocks"; Locked = false; ProfileUrl = $"https://megafono.test/u/{alexHandle}" }
        let! carol = server.CreateUser "carol"
        let carolHandle = $"@{carol.Username}@{server.Domain}"

        // carol follows alex
        let! alexRemote = pollFor "carol finds alex" (fun () -> carol.SearchAccount($"{alexHandle}@megafono.test", true))
        let! _ = carol.Follow alexRemote.Id

        let! _ =
            pollFor "carol follows alex" (fun () ->
                task {
                    let! r = carol.Relationship alexRemote.Id
                    return (if r.Following then Some r else None)
                })

        // alex follows carol. A new account may be locked by default (GoToSocial's are), in which case she approves the
        // request, as a user would; an unlocked one accepts at once.
        let! status, resolved = internalCall megafono HttpMethod.Get $"/internal/remote-actors?handle={Uri.EscapeDataString carolHandle}" None
        Assert.Equal(200, status)
        let carolActorId = get resolved "id"
        let! status, _ = internalCall megafono HttpMethod.Post $"/internal/actors/{alexId}/following" (Some(json {| target = carolHandle |}))
        Assert.Equal(202, status)

        let! _ =
            pollFor "carol's server shows alex as a follower" (fun () ->
                task {
                    let! r = carol.Relationship alexRemote.Id

                    if r.FollowedBy then
                        return Some r
                    else
                        let! requests = carol.FollowRequests()

                        match requests |> List.tryFind (fun a -> a.Username = alexHandle) with
                        | Some request ->
                            observe "%s: alex's follow of a new account is a request: the account is locked by default" server.Domain
                            let! _ = carol.AuthorizeFollowRequest request.Id
                            return None
                        | None -> return None
                })

        do!
            pollForTrue "both follows accepted in Megáfono" (fun () ->
                task {
                    let! followers = listed megafono $"/internal/actors/{alexId}/followers?status=accepted"
                    let! following = listed megafono $"/internal/actors/{alexId}/following?status=accepted"
                    return followers = [ carolActorId, "accepted" ] && following = [ carolActorId, "accepted" ]
                })

        return
            { AlexId = alexId
              AlexHandle = alexHandle
              Carol = carol
              AlexRemote = alexRemote
              CarolActorId = carolActorId
              CarolHandle = carolHandle }
    }

/// After a block both follows are over, each with its webhook: the inbound one `removed`, the outbound one `removed`, or
/// `rejected` when the remote server rejected it before the Block (Mastodon sends `Reject{Follow}` and `Undo{Follow}`
/// ahead of its `Block`; the app's own block call ends both as `removed`). `outbound` lists what is accepted.
let private assertFollowsEnded (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) (pair: Pair) (outbound: string list) : Task =
    task {
        let! followers = listed megafono $"/internal/actors/{pair.AlexId}/followers?status=removed"
        let! following = listed megafono $"/internal/actors/{pair.AlexId}/following?status=removed,rejected"
        Assert.Equal<(string * string) list>([ pair.CarolActorId, "removed" ], followers)
        Assert.Equal(1, following.Length)
        Assert.Equal(pair.CarolActorId, fst following.Head)
        Assert.Contains(snd following.Head, outbound)

        let! stillAccepted = listed megafono $"/internal/actors/{pair.AlexId}/followers?status=accepted,pending"
        Assert.Empty stillAccepted
        let! stillFollowing = listed megafono $"/internal/actors/{pair.AlexId}/following?status=accepted,pending"
        Assert.Empty stillFollowing

        let endings () =
            megafono.FakeApp.Received |> List.filter (fun e -> e.Type = "follow.removed" || e.Type = "follow.rejected")

        let! _ = pollFor "two webhooks for the ended follows" (fun () -> task { return (if (endings ()).Length >= 2 then Some() else None) })
        do! Task.Delay 1000
        let ended = endings ()
        Assert.Equal(2, ended.Length)
        let direction (e: FakeApp.ReceivedWebhook) = get e.Json "follow.direction"
        Assert.Equal<string list>([ "inbound"; "outbound" ], ended |> List.map direction |> List.sort)

        for e in ended do
            Assert.True e.SignatureValid
            Assert.Equal(pair.AlexId, get e.Json "user.id")
            Assert.Equal(pair.CarolActorId, get e.Json "actor.id")
            // `removed` for a follow of hers that she undid or a block ended, `rejected` for one she rejected
            Assert.Equal((if e.Type = "follow.removed" then "removed" else "rejected"), get e.Json "follow.status")

        let inbound = ended |> List.find (fun e -> direction e = "inbound")
        Assert.Equal("follow.removed", inbound.Type)
        let outboundEvent = ended |> List.find (fun e -> direction e = "outbound")
        Assert.Contains(get outboundEvent.Json "follow.status", outbound)
        observe "%s: the app heard %s for the inbound follow and %s for the outbound one" server.Domain inbound.Type outboundEvent.Type
    }

/// The account is blocked from replying, or the reply is posted and the app never hears of it. Returns what happened.
let private tryReply (carol: FediClient) (statusId: string) (handle: string) (marker: string) : Task<Result<Status, string>> =
    task {
        try
            let! reply = carol.PostReply(statusId, "public", $"@{handle}@megafono.test {marker} hello", None)
            return Ok reply
        with FediClientException(_, _, status, body) ->
            return Error $"{status} {body}"
    }

// (a) SC-001 ---------------------------------------------------------------------------------------------------------

let private userBlockScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        let! pair = mutualFollows server megafono
        use carol = pair.Carol
        let! postUrl = publishPublic megafono pair.AlexId ("hello-" + uniqueName "p")

        // carol's server already knows the post (she follows alex, and resolves it by URL to reply to it)
        let! post = pollFor "carol resolves alex's post" (fun () -> task { let! f = carol.SearchStatus postUrl in return List.tryHead f })

        // The app blocks carol by her handle: both follows end, a Block goes to carol's own inbox
        let clock = Stopwatch.StartNew()
        let! status, answer = internalCall megafono HttpMethod.Post $"/internal/actors/{pair.AlexId}/blocks" (Some(json {| actor = pair.CarolHandle |}))
        Assert.Equal(201, status)
        Assert.Equal(pair.CarolActorId, get answer "actor")

        // Megáfono ended both follows in the same transaction
        do! assertFollowsEnded server megafono pair [ "removed" ]

        let! _ =
            pollFor "the Block delivered" (fun () ->
                task {
                    let! summary = deliverySummary megafono
                    return (if summary.Contains "Block delivered" then Some summary else None)
                })

        let blockSent = clock.Elapsed.TotalSeconds

        // carol's server received the Block and dropped the follows on its side too, both ways
        let! _ =
            pollFor "neither follow remains on carol's server" (fun () ->
                task {
                    let! r = carol.Relationship pair.AlexRemote.Id
                    return (if not r.Following && not r.FollowedBy && not r.Requested then Some r else None)
                })

        observe "%s: Block delivered after %.1fs, carol's server dropped both follows after %.1fs" server.Domain blockSent clock.Elapsed.TotalSeconds
        let! after = deliverySummary megafono
        observe "%s: deliveries after the block: %s" server.Domain after

        // The blocked user replies to the public post: her server may refuse (it knows she is blocked), or send it
        // and Megáfono drops it. Either way no reply.received, and no webhook of any kind for the reply.
        let marker = uniqueName "blockedreply"
        let! reply = tryReply carol post.Id pair.AlexHandle marker

        match reply with
        | Error refused -> observe "%s: the blocked user's reply is refused by her own server: %s" server.Domain refused
        | Ok sent ->
            observe "%s: the blocked user's reply was posted (%s)" server.Domain sent.Uri
            // wait for the reply to arrive at the inbox, up to 30 s; Megáfono answers 202 and drops it
            let deadline = DateTime.UtcNow.AddSeconds 30.0

            while DateTime.UtcNow < deadline && not (megafono.InboxRequests |> List.exists (fun r -> r.Body.Contains marker)) do
                do! Task.Delay 500

            let arrived = megafono.InboxRequests |> List.filter (fun r -> r.Body.Contains marker)
            observe "%s: the reply reached the inbox %d time(s): %s" server.Domain arrived.Length (arrived |> List.map (fun r -> $"{r.Path} {r.Status}") |> String.concat ", ")
            Assert.All(arrived, fun r -> Assert.Contains(r.Status, [ 202; 403 ]))

        // a further quiet stretch, then nothing reached the app
        do! Task.Delay 5000
        Assert.Empty(events megafono "reply.received")
        dumpWire server megafono
    }

// (b) SC-002 ---------------------------------------------------------------------------------------------------------

let private instanceBlockScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        let alexHandle = uniqueName "alexi"
        let alexId = $"u-{alexHandle}"
        megafono.FakeApp.AddUser { FakeApp.user alexId alexHandle with Name = "Alex Instance"; Locked = false; ProfileUrl = $"https://megafono.test/u/{alexHandle}" }
        // an actor carol's server has never seen: its lookup is what the block must stop
        let freshHandle = uniqueName "fresh"
        megafono.FakeApp.AddUser { FakeApp.user $"u-{freshHandle}" freshHandle with Name = "Fresh"; Locked = false; ProfileUrl = $"https://megafono.test/u/{freshHandle}" }

        use! carol = server.CreateUser "carol"
        let! postUrl = publishPublic megafono alexId ("hello-" + uniqueName "p")
        // carol's server knows the post before the block (it is public and she resolves it to reply to it)
        let! post = pollFor "carol resolves alex's post" (fun () -> task { let! f = carol.SearchStatus postUrl in return List.tryHead f })

        let! status, _ = internalCall megafono HttpMethod.Post "/internal/blocks" (Some(json {| domain = server.Domain |}))
        Assert.Equal(201, status)

        // run the checks, then lift the block whatever happened: later tests in the shared network keep working
        // (`finally` cannot await in a task, so the failure is kept and rethrown after the lift)
        let mutable failure = None

        try
            // the reply is refused at the inbox with 403, before anything is parsed, and never reaches the app
            let marker = uniqueName "instancereply"
            let! reply = carol.PostReply(post.Id, "public", $"@{alexHandle}@megafono.test {marker} hello", None)
            observe "%s: carol posted the reply %s" server.Domain reply.Uri

            let! refused =
                pollFor "the reply refused at the inbox" (fun () ->
                    task { return megafono.InboxRequests |> List.tryFind (fun r -> r.Body.Contains marker) })

            observe "%s: the reply reached %s and was answered %d" server.Domain refused.Path refused.Status
            Assert.Equal(403, refused.Status)
            Assert.Empty(events megafono "reply.received")

            // the server's lookup of a local actor it has never seen fails: its signed fetch is answered 404
            let mutable found = None
            let attempts = 3

            for _ in 1..attempts do
                let! f = carol.SearchAccount($"{freshHandle}@megafono.test", true)
                if f.IsSome then found <- f
                do! Task.Delay 2000

            observe "%s: lookup of a fresh local actor by the blocked server: %s" server.Domain (match found with None -> "nothing" | Some a -> $"found {a.Acct}")
            Assert.True(found.IsNone, "the blocked server cannot look a local actor up")
            Assert.Empty(events megafono "reply.received")
            dumpWire server megafono
        with e ->
            failure <- Some e

        let! lifted, _ = internalCall megafono HttpMethod.Delete $"/internal/blocks?domain={server.Domain}" None
        Assert.Equal(204, lifted)
        failure |> Option.iter (fun e -> Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e).Throw())

        // after the lift the same server reaches the actor again: the 403 did not leave it unable to deliver or look up
        let! again = pollFor "carol finds a local actor after the lift" (fun () -> carol.SearchAccount($"{freshHandle}@megafono.test", true))
        observe "%s: after the lift the lookup finds %s" server.Domain again.Acct
    }

// (c) SC-003 ---------------------------------------------------------------------------------------------------------

let private remoteBlockScenario (server: IFediServer) (megafono: MegafonoInterop.InteropMegafono) : Task =
    task {
        let! pair = mutualFollows server megafono
        use carol = pair.Carol
        let clock = Stopwatch.StartNew()

        // carol blocks alex through the client API; her server drops the follows and sends a Block to alex's inbox
        let! blocked = carol.Block pair.AlexRemote.Id
        Assert.True blocked.Blocking

        let! _ =
            pollFor "both follows ended in Megáfono" (fun () ->
                task {
                    let! followers = listed megafono $"/internal/actors/{pair.AlexId}/followers?status=removed"
                    let! following = listed megafono $"/internal/actors/{pair.AlexId}/following?status=removed,rejected"
                    return (if followers.Length = 1 && following.Length = 1 then Some() else None)
                })

        observe "%s: Megáfono showed both follows ended %.1fs after the remote block" server.Domain clock.Elapsed.TotalSeconds
        // GoToSocial ends both follows with `Undo{Follow}` and `Block`; Mastodon also rejects alex's follow of carol first
        do! assertFollowsEnded server megafono pair [ "removed"; "rejected" ]

        // the Block itself (it may come after the Undo and Reject that came with it) is acknowledged
        let! _ = pollFor "the Block at the inbox" (fun () -> task { return megafono.InboxRequests |> List.tryFind (fun r -> activityType r.Body = "Block") })
        let blocks = megafono.InboxRequests |> List.filter (fun r -> activityType r.Body = "Block")
        Assert.All(blocks, fun r -> Assert.Equal(202, r.Status))
        observe "%s: the remote Block arrived at %s" server.Domain (blocks |> List.map (fun r -> $"{r.Path} {r.Status}") |> String.concat ", ")
        // nothing is sent back: no Block, Reject or Undo from Megáfono for a remote block
        let! deliveries = deliverySummary megafono
        observe "%s: deliveries after the remote block: %s" server.Domain deliveries
        Assert.DoesNotContain("Block", deliveries)

        // carol lifts the block: her server sends Undo{Block}, acknowledged and nothing restored
        let! _ = carol.Unblock pair.AlexRemote.Id

        let! undo =
            pollFor "Undo{Block} at the inbox" (fun () ->
                task {
                    return
                        megafono.InboxRequests
                        |> List.tryFind (fun r -> activityType r.Body = "Undo" && undoes r.Body = "Block")
                })

        observe "%s: Undo{Block} arrived at %s and was answered %d" server.Domain undo.Path undo.Status
        Assert.Equal(202, undo.Status)
        let! followers = listed megafono $"/internal/actors/{pair.AlexId}/followers?status=accepted,pending"
        Assert.Empty followers
        dumpWire server megafono
    }

let private flow (scenario: IFediServer -> MegafonoInterop.InteropMegafono -> Task) (server: IFediServer) (front: IContainer) (net: FederationNetwork) (pg: PostgresFixture) : Task =
    task {
        use! megafono = MegafonoInterop.start net pg [ server.Domain, front ]
        do! withExplanation server megafono (fun () -> scenario server megafono)
    }

type BlockTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``SC-001 GoToSocial drops both follows on the app's block of its user, and her reply never reaches the app`` () =
        task {
            let! gts = net.GoToSocial()
            do! flow userBlockScenario gts gts.Front net pg
        }

    [<Fact>]
    member _.``SC-002 GoToSocial: an instance block of its domain answers its replies 403 and fails its lookups`` () =
        task {
            let! gts = net.GoToSocial()
            do! flow instanceBlockScenario gts gts.Front net pg
        }

    [<Fact>]
    member _.``SC-003 GoToSocial: its user blocking the local actor ends both follows with two follow.removed`` () =
        task {
            let! gts = net.GoToSocial()
            do! flow remoteBlockScenario gts gts.Front net pg
        }

[<Trait("Suite", "mastodon")>]
type MastodonBlockTests(net: FederationNetwork, pg: PostgresFixture) =

    [<Fact>]
    member _.``SC-001 Mastodon drops both follows on the app's block of its user, and her reply never reaches the app`` () =
        task {
            let! mastodon = net.Mastodon()
            do! flow userBlockScenario mastodon mastodon.Front net pg
        }

    [<Fact>]
    member _.``SC-002 Mastodon: an instance block of its domain answers its replies 403 and fails its lookups`` () =
        task {
            let! mastodon = net.Mastodon()
            do! flow instanceBlockScenario mastodon mastodon.Front net pg
        }

    [<Fact>]
    member _.``SC-003 Mastodon: its user blocking the local actor ends both follows with two follow.removed`` () =
        task {
            let! mastodon = net.Mastodon()
            do! flow remoteBlockScenario mastodon mastodon.Front net pg
        }
