/// `PUT /internal/actors/{id}`: look the user up again and tell followers if the profile changed (FR-007)
module Megafono.Internal.Actors

open System
open System.Text.Json.Nodes
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

/// Stores an `Update{Person}` (the exact bytes, so every retry is identical) and queues it, in one transaction
let private announce (dataSource: NpgsqlDataSource) (time: TimeProvider) (domain: string) (actor: LocalActor) =
    task {
        let now = time.GetUtcNow()
        let activityGuid = Guid.NewGuid()
        let profile = Megafono.Federation.Actors.profileOf actor

        let body =
            ActivityStreams.updateActor domain actor.AppUserId (Ids.activityId domain activityGuid) now (ActivityStreams.actor domain profile)
            |> ActivityStreams.toBytes

        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! inboxes = followerInboxes tx actor.AppUserId

        if inboxes.IsEmpty then
            do! tx.RollbackAsync()
            return 0
        else
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
            return queued
    }

let private refresh (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match! ctx.GetService<AppClient>().Refresh appUserId with
            | RefreshNotFound -> return! error ctx 404 "not_found" "The app does not know this user"
            | RefreshUnavailable -> return! error ctx 503 "app_unavailable" "The app did not answer the actor lookup; nothing was changed"
            | Refreshed(actor, false) ->
                ctx.SetStatusCode 202
                return! ctx.WriteJson {| changed = false; deliveries = 0 |}
            | Refreshed(actor, true) ->
                let options = ctx.GetService<Config.MegafonoOptions>()
                let! queued = announce (ctx.GetService<NpgsqlDataSource>()) (ctx.GetService<TimeProvider>()) options.Domain actor
                ctx.SetStatusCode 202
                return! ctx.WriteJson {| changed = true; deliveries = queued |}
        }

let endpoints: Endpoint list = [ PUT [ routef "/internal/actors/{%s}" refresh ] ]
