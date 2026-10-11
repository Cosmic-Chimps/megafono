/// Follows through the internal API (contracts/internal-api.md):
/// - `POST /internal/actors/{id}/follow-requests/{followId}/approve|reject` (FR-030): the row change, the Accept or
///   Reject to send and the app event commit in one transaction;
/// - `POST /internal/actors/{id}/following` and `DELETE …/following?actor=` (FR-034): a signed Follow or Undo{Follow}
///   queued with the row change;
/// - `GET …/followers` and `GET …/following` (FR-035): status filter, `limit`, an opaque cursor, and `mutual`.
module Megafono.Internal.Follows

open System
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

type private Answer =
    | Approve
    | Reject

let private error (ctx: HttpContext) (status: int) (code: string) (message: string) =
    ctx.SetStatusCode status
    ctx.WriteJson {| error = code; message = message |}

let private answer (kind: Answer) (appUserId: string) (followIdText: string) : EndpointHandler =
    fun ctx ->
        task {
            match Guid.TryParse followIdText with
            | false, _ -> return! error ctx 404 "not_found" "No such follow request"
            | true, followId ->
                let dataSource = ctx.GetService<NpgsqlDataSource>()
                let time = ctx.GetService<TimeProvider>()
                let domain = ctx.GetService<Config.MegafonoOptions>().Domain
                use! connection = dataSource.OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()

                use find =
                    new NpgsqlCommand(
                        "SELECT status, remote_actor, follow_activity_id FROM follows WHERE id = @id AND local_actor = @user AND direction = 'inbound' FOR UPDATE",
                        connection,
                        tx
                    )

                find.Parameters.AddWithValue("id", followId) |> ignore
                find.Parameters.AddWithValue("user", appUserId) |> ignore
                use! reader = find.ExecuteReaderAsync()
                let row = if reader.Read() then Some(reader.GetString 0, reader.GetString 1, reader.GetString 2) else None
                do! reader.CloseAsync()

                match row with
                | None ->
                    do! tx.RollbackAsync()
                    return! error ctx 404 "not_found" "No such follow request"
                | Some(current, remoteId, followActivityId) ->
                    let target, wanted = (match kind with Approve -> "accepted", "pending" | Reject -> "rejected", "pending")

                    if current = target then
                        // Repeating the same answer changes nothing and sends nothing
                        do! tx.RollbackAsync()
                        return! ctx.WriteJson {| followId = followId; status = target |}
                    elif current <> wanted then
                        do! tx.RollbackAsync()
                        return! error ctx 409 "conflict" $"The follow request is {current}, it cannot be answered with the other answer"
                    else
                        match! loadRemote tx remoteId with
                        | None ->
                            do! tx.RollbackAsync()
                            return! error ctx 404 "not_found" "The remote actor is unknown"
                        | Some remote ->
                            use update = new NpgsqlCommand("UPDATE follows SET status = @status, updated_at = @now WHERE id = @id", connection, tx)
                            update.Parameters.AddWithValue("status", target) |> ignore
                            update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
                            update.Parameters.AddWithValue("id", followId) |> ignore
                            let! _ = update.ExecuteNonQueryAsync()

                            let reply, eventType =
                                match kind with
                                | Approve -> AcceptReply, Events.FollowAccepted
                                | Reject -> RejectReply, Events.FollowRejected

                            do! queueReply tx time domain reply appUserId remote followActivityId

                            let body: Events.EventBody =
                                { Type = eventType
                                  Actor = Some(actorSummary remote)
                                  Follow = Some { Id = followId; Direction = "inbound"; Status = target }
                                  Extra = [] }

                            let! _ = Outbox.enqueueEvent tx time appUserId body
                            do! tx.CommitAsync()
                            return! ctx.WriteJson {| followId = followId; status = target |}
        }

// ---- follow and unfollow a remote actor ------------------------------------------------------------------

let private localExists (ctx: HttpContext) (appUserId: string) : Task<bool> =
    task {
        match! ctx.GetService<AppClient.AppClient>().ById appUserId with
        | AppClient.Found _
        | AppClient.UnavailableWithFallback _ -> return true
        | _ -> return false
    }

let private followResponse (rowId: Guid) (status: string) (remote: RemoteActor) =
    {| followId = rowId; status = status; actor = Megafono.Internal.RemoteActors.actorView remote |}

/// Stores a Follow or Undo activity (the exact bytes every retry sends) and queues it for the remote actor's own inbox
let private queueActivity (tx: NpgsqlTransaction) (time: TimeProvider) (kind: string) (localUser: string) (activityGuid: Guid) (document: JsonNode) (remote: RemoteActor) =
    task {
        use insert =
            new NpgsqlCommand(
                $"""INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                    VALUES (@id, @actor, '{kind}', 'direct', @body, @now)""",
                tx.Connection,
                tx
            )

        insert.Parameters.AddWithValue("id", activityGuid) |> ignore
        insert.Parameters.AddWithValue("actor", localUser) |> ignore
        insert.Parameters.AddWithValue("body", ActivityStreams.toBytes document) |> ignore
        insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
        let! _ = insert.ExecuteNonQueryAsync()
        let! _ = Outbox.enqueueDeliveries tx time activityGuid [ remote.Inbox ]
        ()
    }

let private readTarget (ctx: HttpContext) : Task<string option> =
    task {
        try
            match! JsonNode.ParseAsync ctx.Request.Body with
            | :? JsonObject as o -> return str o "target" |> Option.filter (fun t -> t.Trim() <> "")
            | _ -> return None
        with _ ->
            return None
    }

let private follow (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! known = localExists ctx appUserId

            if not known then
                return! error ctx 404 "not_found" "The app does not know this user"
            else
                match! readTarget ctx with
                | None -> return! error ctx 400 "invalid_request" "The body must be {\"target\": \"@user@host\" or an actor URL}"
                | Some target ->
                    match! RemoteActors.resolveTarget (ctx.GetService<RemoteActors.Env>()) target with
                    | Error failure -> return! Megafono.Internal.RemoteActors.resolutionError ctx failure
                    | Ok remote ->
                        let time = ctx.GetService<TimeProvider>()
                        let domain = ctx.GetService<Config.MegafonoOptions>().Domain
                        use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
                        use! tx = connection.BeginTransactionAsync()
                        let activityGuid = Guid.NewGuid()
                        let followIri = Ids.activityId domain activityGuid
                        let now = time.GetUtcNow().UtcDateTime

                        // A new request, or a new one after a rejection or an unfollow. If the row is already pending or
                        // accepted (also against a concurrent identical call) the conflict clause leaves it alone.
                        use upsert =
                            new NpgsqlCommand(
                                """INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status, created_at, updated_at)
                                   VALUES (gen_random_uuid(), 'outbound', @local, @remote, @follow, 'pending', @now, @now)
                                   ON CONFLICT (direction, local_actor, remote_actor) DO UPDATE
                                     SET follow_activity_id = EXCLUDED.follow_activity_id, status = 'pending', updated_at = EXCLUDED.updated_at
                                     WHERE follows.status IN ('rejected', 'removed')
                                   RETURNING id""",
                                connection,
                                tx
                            )

                        upsert.Parameters.AddWithValue("local", appUserId) |> ignore
                        upsert.Parameters.AddWithValue("remote", remote.Id) |> ignore
                        upsert.Parameters.AddWithValue("follow", followIri) |> ignore
                        upsert.Parameters.AddWithValue("now", now) |> ignore
                        let! created = upsert.ExecuteScalarAsync()

                        match created with
                        | :? Guid as rowId ->
                            let document = ActivityStreams.follow followIri (Ids.actorId domain appUserId) remote.Id
                            do! queueActivity tx time "Follow" appUserId activityGuid document remote
                            do! tx.CommitAsync()
                            ctx.SetStatusCode 202
                            return! ctx.WriteJson(followResponse rowId "pending" remote)
                        | _ ->
                            // Already following (or asked): the current status, nothing sent
                            use current =
                                new NpgsqlCommand(
                                    "SELECT id, status FROM follows WHERE direction = 'outbound' AND local_actor = @local AND remote_actor = @remote",
                                    connection,
                                    tx
                                )

                            current.Parameters.AddWithValue("local", appUserId) |> ignore
                            current.Parameters.AddWithValue("remote", remote.Id) |> ignore
                            use! reader = current.ExecuteReaderAsync()
                            reader.Read() |> ignore
                            let rowId, status = reader.GetGuid 0, reader.GetString 1
                            do! reader.CloseAsync()
                            do! tx.RollbackAsync()
                            ctx.SetStatusCode 202
                            return! ctx.WriteJson(followResponse rowId status remote)
        }

let private unfollow (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match ctx.TryGetQueryValue "actor" with
            | None -> return! error ctx 400 "invalid_request" "Give the remote actor id as ?actor="
            | Some remoteId ->
                let time = ctx.GetService<TimeProvider>()
                let domain = ctx.GetService<Config.MegafonoOptions>().Domain
                use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()

                use find =
                    new NpgsqlCommand(
                        """SELECT id, follow_activity_id FROM follows
                           WHERE direction = 'outbound' AND local_actor = @local AND remote_actor = @remote AND status IN ('pending', 'accepted')
                           FOR UPDATE""",
                        connection,
                        tx
                    )

                find.Parameters.AddWithValue("local", appUserId) |> ignore
                find.Parameters.AddWithValue("remote", remoteId) |> ignore
                use! reader = find.ExecuteReaderAsync()
                let row = if reader.Read() then Some(reader.GetGuid 0, reader.GetString 1) else None
                do! reader.CloseAsync()

                match row with
                | None ->
                    do! tx.RollbackAsync()
                    return! error ctx 404 "not_found" "That actor is not followed"
                | Some(rowId, followIri) ->
                    match! loadRemote tx remoteId with
                    | None ->
                        do! tx.RollbackAsync()
                        return! error ctx 404 "not_found" "That actor is not followed"
                    | Some remote ->
                        use update = new NpgsqlCommand("UPDATE follows SET status = 'removed', updated_at = @now WHERE id = @id", connection, tx)
                        update.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
                        update.Parameters.AddWithValue("id", rowId) |> ignore
                        let! _ = update.ExecuteNonQueryAsync()

                        // Undo embeds the original Follow, with the id it was sent under
                        let activityGuid = Guid.NewGuid()
                        let local = Ids.actorId domain appUserId

                        let original: ActivityStreams.FollowRef =
                            { Id = followIri
                              Actor = local
                              Object = remote.Id }

                        let document = ActivityStreams.undoFollow (Ids.activityId domain activityGuid) local original
                        do! queueActivity tx time "Undo" appUserId activityGuid document remote
                        do! tx.CommitAsync()
                        ctx.SetStatusCode 202
                        return! ctx.WriteJson {| followId = rowId; status = "removed" |}
        }

// ---- lists --------------------------------------------------------------------------------------------------

let private defaultLimit = 50
let private maxLimit = 200
let private knownStatuses = set [ "pending"; "accepted"; "rejected"; "removed" ]

/// With no filter: every relationship still in play. `removed` ones are tombstones and only listed when asked for.
let private defaultStatuses = [| "pending"; "accepted"; "rejected" |]

let private encodeCursor (createdAt: DateTime) (id: Guid) : string =
    Convert.ToBase64String(Encoding.UTF8.GetBytes $"{createdAt.Ticks}|{id}").TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private decodeCursor (cursor: string) : (DateTime * Guid) option =
    try
        let padded = cursor.Replace('-', '+').Replace('_', '/')
        let padded = padded + String('=', (4 - padded.Length % 4) % 4)
        let parts = Encoding.UTF8.GetString(Convert.FromBase64String padded).Split('|')
        Some(DateTime(int64 parts[0], DateTimeKind.Utc), Guid.Parse parts[1])
    with _ ->
        None

let private list (direction: string) (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let statuses =
                match ctx.TryGetQueryValue "status" with
                | None -> Some defaultStatuses
                | Some text ->
                    let wanted = text.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
                    if wanted.Length > 0 && wanted |> Array.forall knownStatuses.Contains then Some wanted else None

            let limit =
                match ctx.TryGetQueryValue "limit" with
                | None -> Some defaultLimit
                | Some text ->
                    match Int32.TryParse text with
                    | true, n when n >= 1 -> Some(min n maxLimit)
                    | _ -> None

            let cursor =
                match ctx.TryGetQueryValue "after" with
                | None -> Some None
                | Some text -> decodeCursor text |> Option.map Some

            match statuses, limit, cursor with
            | None, _, _ -> return! error ctx 400 "invalid_request" "status must be pending, accepted, rejected or removed (comma separated)"
            | _, None, _ -> return! error ctx 400 "invalid_request" "limit must be a positive integer"
            | _, _, None -> return! error ctx 400 "invalid_request" "after is not a cursor this API gave out"
            | Some statuses, Some limit, Some cursor ->
                let dataSource = ctx.GetService<NpgsqlDataSource>()
                use exists = dataSource.CreateCommand "SELECT 1 FROM local_actors WHERE app_user_id = @id"
                exists.Parameters.AddWithValue("id", appUserId) |> ignore

                match! exists.ExecuteScalarAsync() with
                | null -> return! error ctx 404 "not_found" "The app does not know this user"
                | _ ->
                    let opposite = if direction = "inbound" then "outbound" else "inbound"

                    use query =
                        dataSource.CreateCommand
                            """SELECT f.id, f.status, f.created_at, r.id, r.handle, r.name, r.icon_url, r.profile_url,
                                      (f.status = 'accepted' AND EXISTS (
                                          SELECT 1 FROM follows o
                                          WHERE o.local_actor = f.local_actor AND o.remote_actor = f.remote_actor
                                            AND o.direction = @opposite AND o.status = 'accepted')) AS mutual
                               FROM follows f JOIN remote_actors r ON r.id = f.remote_actor
                               WHERE f.local_actor = @user AND f.direction = @direction AND f.status = ANY(@statuses)
                                 AND (NOT @hasCursor OR (f.created_at, f.id) < (@cursorTime, @cursorId))
                               ORDER BY f.created_at DESC, f.id DESC
                               LIMIT @limit"""

                    query.Parameters.AddWithValue("opposite", opposite) |> ignore
                    query.Parameters.AddWithValue("user", appUserId) |> ignore
                    query.Parameters.AddWithValue("direction", direction) |> ignore
                    query.Parameters.AddWithValue("statuses", statuses) |> ignore
                    query.Parameters.AddWithValue("hasCursor", cursor.IsSome) |> ignore
                    query.Parameters.AddWithValue("cursorTime", (match cursor with Some(t, _) -> t | None -> DateTime.UtcNow)) |> ignore
                    query.Parameters.AddWithValue("cursorId", (match cursor with Some(_, i) -> i | None -> Guid.Empty)) |> ignore
                    query.Parameters.AddWithValue("limit", limit + 1) |> ignore
                    use! reader = query.ExecuteReaderAsync()
                    let rows = ResizeArray<{| Id: Guid; Status: string; Since: DateTime; Actor: obj; Mutual: bool |}>()

                    while reader.Read() do
                        let optional (i: int) : string = if reader.IsDBNull i then null else reader.GetString i
                        let actorId = reader.GetString 3
                        let handle = optional 4

                        let actor =
                            {| id = actorId
                               handle = (if isNull handle then actorId else "@" + handle)
                               name = (match optional 5 with null -> (if isNull handle then actorId else handle) | n -> n)
                               iconUrl = optional 6
                               profileUrl = optional 7 |}

                        rows.Add
                            {| Id = reader.GetGuid 0
                               Status = reader.GetString 1
                               Since = reader.GetFieldValue<DateTime> 2
                               Actor = box actor
                               Mutual = reader.GetBoolean 8 |}

                    do! reader.CloseAsync()
                    let page = rows |> Seq.truncate limit |> List.ofSeq
                    let next = if rows.Count > limit then (let last = List.last page in encodeCursor last.Since last.Id) else null

                    let items =
                        page
                        |> List.map (fun r ->
                            {| followId = r.Id
                               status = r.Status
                               mutual = r.Mutual
                               since = r.Since.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")
                               actor = r.Actor |})

                    return! ctx.WriteJson {| items = items; next = next |}
        }

let endpoints: Endpoint list =
    [ POST
          [ routef "/internal/actors/{%s}/follow-requests/{%s}/approve" (fun (user: string) (follow: string) -> answer Approve user follow)
            routef "/internal/actors/{%s}/follow-requests/{%s}/reject" (fun (user: string) (follow: string) -> answer Reject user follow)
            routef "/internal/actors/{%s}/following" follow ]
      DELETE [ routef "/internal/actors/{%s}/following" unfollow ]
      GET
          [ routef "/internal/actors/{%s}/followers" (list "inbound")
            routef "/internal/actors/{%s}/following" (list "outbound") ] ]
