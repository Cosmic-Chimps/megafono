/// `PUT /internal/actors/{id}`: look the user up again and tell followers if the profile changed (FR-007).
/// `DELETE /internal/actors/{id}`: delete the actor, tell the servers that know it, erase what was stored (US6)
module Megafono.Internal.Actors

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient

let private error (ctx: HttpContext) (status: int) (code: string) (message: string) =
    ctx.SetStatusCode status
    ctx.WriteJson {| error = code; message = message |}

/// The distinct inboxes of the user's accepted followers, shared inbox first (one request per server)
let private followerInboxes (tx: NpgsqlTransaction) (appUserId: string) =
    task {
        use command =
            new NpgsqlCommand(
                """SELECT DISTINCT COALESCE(r.shared_inbox, r.inbox)
                   FROM follows f JOIN remote_actors r ON r.id = f.remote_actor
                   WHERE f.direction = 'inbound' AND f.local_actor = @id AND f.status = 'accepted'""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", appUserId) |> ignore
        use! reader = command.ExecuteReaderAsync()
        let inboxes = ResizeArray<string>()

        while reader.Read() do
            inboxes.Add(reader.GetString 0)

        return List.ofSeq inboxes
    }

/// Stores the new profile and queues the `Update{Person}` for the followers' servers in ONE transaction: either
/// both are saved or neither is, so a crash cannot leave a stored change that was never announced (Principle V).
/// The Update carries the exact bytes every retry will send. None when the actor was deleted.
let private storeAndAnnounce
    (dataSource: NpgsqlDataSource)
    (time: TimeProvider)
    (domain: string)
    (app: AppClient)
    (profile: AppProfile)
    : Task<(LocalActor * bool * int) option> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        // Serialized with the actor's deletion (R-34); a deleted actor is never updated or announced
        let! live = Outbox.lockLiveActor tx profile.Id
        // No live row is either a deleted actor or one Megáfono has not stored yet (this call may be its first sight)
        let! deleted = if live then Task.FromResult false else Outbox.isDeletedActor tx profile.Id
        let! stored = if deleted then Task.FromResult None else app.StoreIn tx profile

        match stored with
        | None ->
            do! tx.RollbackAsync()
            return None
        | Some(actor, false) ->
            do! tx.CommitAsync()
            return Some(actor, false, 0)
        | Some(actor, true) ->
            let! inboxes = followerInboxes tx actor.AppUserId

            if inboxes.IsEmpty then
                do! tx.CommitAsync()
                return Some(actor, true, 0)
            else
                let now = time.GetUtcNow()
                let activityGuid = Guid.NewGuid()
                let document = ActivityStreams.actor domain (Megafono.Federation.Actors.profileOf actor)

                let body =
                    ActivityStreams.updateActor domain actor.AppUserId (Ids.activityId domain activityGuid) now document
                    |> ActivityStreams.toBytes

                use insert =
                    new NpgsqlCommand(
                        """INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                           VALUES (@id, @actor, 'Update', 'direct', @body, @now)""",
                        connection,
                        tx
                    )

                insert.Parameters.AddWithValue("id", activityGuid) |> ignore
                insert.Parameters.AddWithValue("actor", actor.AppUserId) |> ignore
                insert.Parameters.AddWithValue("body", body) |> ignore
                insert.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
                let! _ = insert.ExecuteNonQueryAsync()
                let! queued = Outbox.enqueueDeliveries tx time activityGuid inboxes
                do! tx.CommitAsync()
                return Some(actor, true, queued)
    }

let private gone (ctx: HttpContext) = error ctx 410 "gone" "The actor was deleted"

let private isDeleted (dataSource: NpgsqlDataSource) (appUserId: string) : Task<bool> =
    task {
        use command = dataSource.CreateCommand "SELECT 1 FROM local_actors WHERE app_user_id = @id AND deleted_at IS NOT NULL"
        command.Parameters.AddWithValue("id", appUserId) |> ignore
        let! found = command.ExecuteScalarAsync()
        return not (isNull found)
    }

let private refresh (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let dataSource = ctx.GetService<NpgsqlDataSource>()
            let! deleted = isDeleted dataSource appUserId

            if deleted then
                return! gone ctx
            else
                match! ctx.GetService<AppClient>().Fetch appUserId with
                | FetchNotFound -> return! error ctx 404 "not_found" "The app does not know this user"
                | FetchUnavailable -> return! error ctx 503 "app_unavailable" "The app did not answer the actor lookup; nothing was changed"
                | Fetched profile ->
                    let options = ctx.GetService<Config.MegafonoOptions>()

                    try
                        match! storeAndAnnounce dataSource (ctx.GetService<TimeProvider>()) options.Domain (ctx.GetService<AppClient>()) profile with
                        | None -> return! gone ctx
                        | Some(_, changed, queued) ->
                            ctx.SetStatusCode 202
                            return! ctx.WriteJson {| changed = changed; deliveries = queued |}
                    with :? PostgresException as e when e.SqlState = PostgresErrorCodes.UniqueViolation ->
                        // The new handle is held by another live user
                        return! error ctx 409 "conflict" "That handle belongs to another user"
        }

// ---- delete (US6) ----------------------------------------------------------------------------------------

let private iso (time: DateTimeOffset) = time.UtcDateTime.ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"

/// One inbox per server for the `Delete{actor}` (R-31): the inboxes of the remote actors with a pending or accepted
/// follow of the actor in either direction (shared inbox where they have one), then per host the shared inbox of
/// one of its actors if any, else the smallest URL. A `Delete{actor}` is not addressed to a recipient, so any inbox
/// of the server processes it.
let private deleteInboxes (tx: NpgsqlTransaction) (appUserId: string) : Task<string list> =
    task {
        use command =
            new NpgsqlCommand(
                """SELECT DISTINCT COALESCE(r.shared_inbox, r.inbox), r.shared_inbox IS NOT NULL
                   FROM follows f JOIN remote_actors r ON r.id = f.remote_actor
                   WHERE f.local_actor = @id AND f.status IN ('pending', 'accepted')""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", appUserId) |> ignore
        use! reader = command.ExecuteReaderAsync()
        let candidates = ResizeArray<string * bool>()

        while reader.Read() do
            candidates.Add((reader.GetString 0, reader.GetBoolean 1))

        return
            candidates
            |> Seq.groupBy (fun (inbox, _) -> Outbox.hostOf inbox)
            |> Seq.map (fun (_, inboxes) ->
                inboxes
                |> Seq.sortBy (fun (inbox, shared) -> (not shared, inbox))
                |> Seq.head
                |> fst)
            |> List.ofSeq
    }

let private execute (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) : Task<int> =
    task {
        use command = new NpgsqlCommand(sql, tx.Connection, tx)

        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore

        return! command.ExecuteNonQueryAsync()
    }

let private deleteActor (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            let time = ctx.GetService<TimeProvider>()

            if not (Ids.isValidAppUserId appUserId) then
                return! error ctx 404 "not_found" "Megafono has no actor for this id"
            else
                use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()

                // The row lock serializes this with every other write for the actor (R-34)
                use find = new NpgsqlCommand("SELECT deleted_at FROM local_actors WHERE app_user_id = @id FOR UPDATE", connection, tx)
                find.Parameters.AddWithValue("id", appUserId) |> ignore
                use! reader = find.ExecuteReaderAsync()
                let row = if reader.Read() then Some(if reader.IsDBNull 0 then None else Some(reader.GetFieldValue<DateTimeOffset> 0)) else None
                do! reader.CloseAsync()

                match row with
                | None ->
                    do! tx.RollbackAsync()
                    return! error ctx 404 "not_found" "Megafono has no actor for this id"
                | Some(Some deletedAt) ->
                    // A repeat answers like the first call; nothing is queued again
                    use count =
                        new NpgsqlCommand(
                            """SELECT count(*) FROM deliveries d JOIN activities a ON a.id = d.activity
                               WHERE a.local_actor = @id AND a.activity_type = 'Delete' AND a.object IS NULL""",
                            connection,
                            tx
                        )

                    count.Parameters.AddWithValue("id", appUserId) |> ignore
                    let! deliveries = count.ExecuteScalarAsync()
                    do! tx.RollbackAsync()
                    ctx.SetStatusCode 202
                    return! ctx.WriteJson {| id = appUserId; deletedAt = iso deletedAt; deliveries = int (deliveries :?> int64) |}
                | Some None ->
                    let now = time.GetUtcNow()
                    let at = now.UtcDateTime
                    let id = ("id", box appUserId)
                    let! inboxes = deleteInboxes tx appUserId

                    // The Delete, signed later by the worker with the actor's key, queued in the same transaction
                    let activityGuid = Guid.NewGuid()

                    let body =
                        ActivityStreams.deleteActor options.Domain appUserId (Ids.activityId options.Domain activityGuid) now
                        |> ActivityStreams.toBytes

                    let! _ =
                        execute
                            tx
                            """INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                               VALUES (@activity, @id, 'Delete', 'public', @body, @now)"""
                            [ id; "activity", box activityGuid; "body", box body; "now", box at ]

                    let! queued = Outbox.enqueueDeliveries tx time activityGuid inboxes

                    // Nothing else of the actor goes out: its queued deliveries end here, and a worker holding a lease writes nothing
                    let! _ =
                        execute
                            tx
                            """UPDATE deliveries d SET status = 'skipped', last_error = 'actor deleted', lease_until = NULL, lease_token = NULL
                               FROM activities a
                               WHERE a.id = d.activity AND a.local_actor = @id AND a.id <> @activity AND d.status IN ('pending', 'sending')"""
                            [ id; "activity", box activityGuid ]

                    // The app made the call: it hears nothing more about the user
                    let! _ = execute tx "DELETE FROM app_events WHERE local_actor = @id AND status IN ('pending', 'sending')" [ id ]

                    // Erasure (R-35): objects deleted with empty documents, replies out of their threads, activity bodies emptied
                    let! _ =
                        execute
                            tx
                            """UPDATE objects SET deleted_at = CASE WHEN state = 'live' THEN @now ELSE deleted_at END,
                                   updated_at = CASE WHEN state = 'live' THEN @now ELSE updated_at END,
                                   state = 'deleted', document = '{}'::jsonb
                               WHERE local_actor = @id"""
                            [ id; "now", box at ]

                    let! _ =
                        execute
                            tx
                            """UPDATE replies SET state = 'deleted', deleted_at = @now, updated_at = @now
                               WHERE state = 'live' AND local_object IN (SELECT id FROM objects WHERE local_actor = @id)"""
                            [ id; "now", box at ]

                    let! _ =
                        execute tx "UPDATE activities SET body = ''::bytea WHERE local_actor = @id AND activity_type IN ('Create', 'Update')" [ id ]

                    let! _ = execute tx "DELETE FROM follows WHERE local_actor = @id" [ id ]

                    let! _ =
                        execute
                            tx
                            "UPDATE local_actors SET deleted_at = @now, name = '', summary = NULL, icon_url = NULL, profile_url = '' WHERE app_user_id = @id"
                            [ id; "now", box at ]

                    do! tx.CommitAsync()
                    ctx.SetStatusCode 202
                    return! ctx.WriteJson {| id = appUserId; deletedAt = iso now; deliveries = queued |}
        }

let endpoints: Endpoint list =
    [ PUT [ routef "/internal/actors/{%s}" refresh ]
      DELETE [ routef "/internal/actors/{%s}" deleteActor ] ]
