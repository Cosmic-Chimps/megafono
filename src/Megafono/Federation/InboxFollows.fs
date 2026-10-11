/// Inbound Follow and Undo{Follow} (FR-030 to FR-032, research R-05, data-model.md "Inbound transitions").
/// Each activity is handled in one transaction: the processed marker, the follow row, the Accept to send and the
/// app event commit together or not at all.
module Megafono.Federation.InboxFollows

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let private followEvent (eventType: Events.EventType) (remote: RemoteActor) (followId: Guid) (status: string) : Events.EventBody =
    { Type = eventType
      Actor = Some(actorSummary remote)
      Follow = Some { Id = followId; Direction = "inbound"; Status = status }
      Extra = [] }

let private lockedRow (tx: NpgsqlTransaction) (localUser: string) (remoteId: string) =
    task {
        use command =
            new NpgsqlCommand(
                "SELECT id, status FROM follows WHERE direction = 'inbound' AND local_actor = @local AND remote_actor = @remote FOR UPDATE",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("local", localUser) |> ignore
        command.Parameters.AddWithValue("remote", remoteId) |> ignore
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(reader.GetGuid 0, reader.GetString 1) else None
    }

let private execute (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) =
    task {
        use command = new NpgsqlCommand(sql, tx.Connection, tx)

        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore

        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

/// A Follow from `signer` for one of our actors. Taken only if its `actor` is the signer (nobody can follow on
/// someone else's behalf) and its `object` is a live local actor; the target comes from `object`, never from the
/// inbox it arrived at.
let handleFollow (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let activityId = str activity "id"
        let actor = idOf activity["actor"]
        let target = idOf activity["object"] |> Option.bind (localActorId env.Domain)

        match activityId, actor, target with
        | Some followId, Some actorId, Some targetUser when actorId = signer.Id && isOnSignerHost signer followId ->
            match! env.App.ById targetUser with
            | AppClient.LookupResult.NotFound
            | AppClient.LookupResult.UnavailableNoFallback -> return Refused 404
            | Found local
            | UnavailableWithFallback local ->
                use! connection = env.DataSource.OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()
                let! fresh = markProcessed tx env.Time followId

                if not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "already processed"
                else
                    let now = env.Time.GetUtcNow().UtcDateTime
                    let newStatus = if local.Locked then "pending" else "accepted"
                    let! existing = lockedRow tx local.AppUserId signer.Id

                    // What to do about the row: the id of the row, its status afterwards, whether the app hears of it,
                    // and whether an Accept goes out
                    let! rowId, status, announce, sendAccept =
                        task {
                            match existing with
                            | None ->
                                let rowId = Guid.NewGuid()

                                do!
                                    execute
                                        tx
                                        """INSERT INTO follows (id, direction, local_actor, remote_actor, follow_activity_id, status, created_at, updated_at)
                                           VALUES (@id, 'inbound', @local, @remote, @follow, @status, @now, @now)"""
                                        [ "id", box rowId; "local", box local.AppUserId; "remote", box signer.Id; "follow", box followId
                                          "status", box newStatus; "now", box now ]

                                return rowId, newStatus, true, not local.Locked
                            | Some(rowId, ("rejected" | "removed")) ->
                                // A new request after a rejection or an unfollow
                                do!
                                    execute
                                        tx
                                        "UPDATE follows SET follow_activity_id = @follow, status = @status, updated_at = @now WHERE id = @id"
                                        [ "id", box rowId; "follow", box followId; "status", box newStatus; "now", box now ]

                                return rowId, newStatus, true, not local.Locked
                            | Some(rowId, current) ->
                                // A repeated Follow: one row, the latest id. A pending request stays pending (the app has
                                // not answered), an accepted one is confirmed again with an Accept that carries the new id.
                                do!
                                    execute
                                        tx
                                        "UPDATE follows SET follow_activity_id = @follow, updated_at = @now WHERE id = @id"
                                        [ "id", box rowId; "follow", box followId; "now", box now ]

                                return rowId, current, false, (current = "accepted")
                        }

                    if sendAccept then
                        do! queueReply tx env.Time env.Domain AcceptReply local.AppUserId signer followId

                    if announce then
                        let eventType = if status = "accepted" then Events.FollowAccepted else Events.FollowRequested
                        let! _ = Outbox.enqueueEvent tx env.Time local.AppUserId (followEvent eventType signer rowId status)
                        ()

                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Follow is not from the signer for one of our actors"
    }

/// Undo{Follow}: the object is the Follow itself or its id. The follow is found among the signer's own rows, by id
/// or, for an embedded Follow, by the local actor it names; nothing in the embedded document is trusted beyond that.
let handleUndo (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let activityId = str activity "id"
        let actor = idOf activity["actor"]
        let inner = activity["object"]

        // An embedded object must be a Follow; a bare string might be the id of anything, the lookup decides
        let isFollowOrId =
            match inner with
            | :? JsonObject as o -> str o "type" = Some "Follow"
            | :? JsonValue -> true
            | _ -> false

        match activityId, actor with
        | Some undoId, Some actorId when actorId = signer.Id && isOnSignerHost signer undoId && isFollowOrId ->
            let followId = idOf inner
            let target = (match inner with :? JsonObject as o -> idOf o["object"] | _ -> None) |> Option.bind (localActorId env.Domain)
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! fresh = markProcessed tx env.Time undoId

            if not fresh then
                do! tx.RollbackAsync()
                return Ignored "already processed"
            else
                use find =
                    new NpgsqlCommand(
                        """SELECT id, local_actor FROM follows
                           WHERE direction = 'inbound' AND remote_actor = @remote AND status IN ('pending', 'accepted')
                             AND (follow_activity_id = @follow OR local_actor = @target)
                           ORDER BY (follow_activity_id = @follow) DESC LIMIT 1 FOR UPDATE""",
                        connection,
                        tx
                    )

                find.Parameters.AddWithValue("remote", signer.Id) |> ignore
                find.Parameters.AddWithValue("follow", (match followId with Some f -> box f | None -> box DBNull.Value)) |> ignore
                find.Parameters.AddWithValue("target", (match target with Some t -> box t | None -> box DBNull.Value)) |> ignore
                use! reader = find.ExecuteReaderAsync()
                let row = if reader.Read() then Some(reader.GetGuid 0, reader.GetString 1) else None
                do! reader.CloseAsync()

                match row with
                | None ->
                    do! tx.RollbackAsync()
                    return Ignored "no such follow"
                | Some(rowId, localUser) ->
                    do!
                        execute
                            tx
                            "UPDATE follows SET status = 'removed', updated_at = @now WHERE id = @id"
                            [ "id", box rowId; "now", box (env.Time.GetUtcNow().UtcDateTime) ]

                    let! _ = Outbox.enqueueEvent tx env.Time localUser (followEvent Events.FollowRemoved signer rowId "removed")
                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Undo is not a Follow undone by its signer"
    }

// ---- answers to our own follows (User Story 4, FR-034, research R-05) ------------------------------------

type private Answer =
    | Accepted
    | Rejected

/// Accept{Follow} or Reject{Follow} from the actor we asked to follow. Only the target can answer: the signer must
/// be the actor of the answer. The follow is found among our outbound rows for that signer, by the id of the Follow
/// (embedded or bare) or, failing that, by the local actor the embedded Follow names; a reject also ends a follow
/// that was already accepted (the remote side revoked it).
let private handleAnswer (answer: Answer) (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let activityId = str activity "id"
        let actor = idOf activity["actor"]
        let inner = activity["object"]

        let isFollowOrId =
            match inner with
            | :? JsonObject as o -> str o "type" = Some "Follow"
            | :? JsonValue -> true
            | _ -> false

        match activityId, actor with
        | Some answerId, Some actorId when actorId = signer.Id && isOnSignerHost signer answerId && isFollowOrId ->
            let followId = idOf inner
            // The embedded Follow says who followed: one of our actors
            let localUser = (match inner with :? JsonObject as o -> idOf o["actor"] | _ -> None) |> Option.bind (localActorId env.Domain)
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! fresh = markProcessed tx env.Time answerId

            if not fresh then
                do! tx.RollbackAsync()
                return Ignored "already processed"
            else
                use find =
                    new NpgsqlCommand(
                        """SELECT id, local_actor, status FROM follows
                           WHERE direction = 'outbound' AND remote_actor = @remote
                             AND (follow_activity_id = @follow OR local_actor = @local)
                           ORDER BY (follow_activity_id = @follow) DESC, created_at DESC LIMIT 1 FOR UPDATE""",
                        connection,
                        tx
                    )

                find.Parameters.AddWithValue("remote", signer.Id) |> ignore
                find.Parameters.AddWithValue("follow", (match followId with Some f -> box f | None -> box DBNull.Value)) |> ignore
                find.Parameters.AddWithValue("local", (match localUser with Some u -> box u | None -> box DBNull.Value)) |> ignore
                use! reader = find.ExecuteReaderAsync()
                let row = if reader.Read() then Some(reader.GetGuid 0, reader.GetString 1, reader.GetString 2) else None
                do! reader.CloseAsync()

                // What the row becomes, if anything: pending -> accepted or rejected, accepted -> rejected (revoked)
                let transition =
                    match answer, row with
                    | Accepted, Some(_, _, "pending") -> Some "accepted"
                    | Rejected, Some(_, _, ("pending" | "accepted")) -> Some "rejected"
                    | _ -> None

                match row, transition with
                | Some(rowId, user, _), Some target ->
                    do!
                        execute
                            tx
                            "UPDATE follows SET status = @status, updated_at = @now WHERE id = @id"
                            [ "id", box rowId; "status", box target; "now", box (env.Time.GetUtcNow().UtcDateTime) ]

                    let eventType = if target = "accepted" then Events.FollowAccepted else Events.FollowRejected

                    let body: Events.EventBody =
                        { Type = eventType
                          Actor = Some(actorSummary signer)
                          Follow = Some { Id = rowId; Direction = "outbound"; Status = target }
                          Extra = [] }

                    let! _ = Outbox.enqueueEvent tx env.Time user body
                    do! tx.CommitAsync()
                    return Handled
                | _ ->
                    // A repeat, a stale answer for a follow we withdrew, or one that is not ours: nothing changes
                    do! tx.RollbackAsync()
                    return Ignored "no outbound follow to apply this answer to"
        | _ -> return Ignored "the answer is not a Follow answered by its target"
    }

let handleAccept (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> = handleAnswer Accepted env signer activity

let handleReject (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> = handleAnswer Rejected env signer activity
