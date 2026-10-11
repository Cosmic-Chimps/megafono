/// `PUT /internal/actors/{id}`: look the user up again and tell followers if the profile changed (FR-007)
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
/// The Update carries the exact bytes every retry will send.
let private storeAndAnnounce
    (dataSource: NpgsqlDataSource)
    (time: TimeProvider)
    (domain: string)
    (app: AppClient)
    (profile: AppProfile)
    : Task<LocalActor * bool * int> =
    task {
        use! connection = dataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! actor, changed = app.StoreIn tx profile

        if not changed then
            do! tx.CommitAsync()
            return actor, false, 0
        else
            let! inboxes = followerInboxes tx actor.AppUserId

            if inboxes.IsEmpty then
                do! tx.CommitAsync()
                return actor, true, 0
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
                return actor, true, queued
    }

let private refresh (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match! ctx.GetService<AppClient>().Fetch appUserId with
            | FetchNotFound -> return! error ctx 404 "not_found" "The app does not know this user"
            | FetchUnavailable -> return! error ctx 503 "app_unavailable" "The app did not answer the actor lookup; nothing was changed"
            | Fetched profile ->
                let options = ctx.GetService<Config.MegafonoOptions>()

                try
                    let! _, changed, queued =
                        storeAndAnnounce (ctx.GetService<NpgsqlDataSource>()) (ctx.GetService<TimeProvider>()) options.Domain (ctx.GetService<AppClient>()) profile

                    ctx.SetStatusCode 202
                    return! ctx.WriteJson {| changed = changed; deliveries = queued |}
                with :? PostgresException as e when e.SqlState = PostgresErrorCodes.UniqueViolation ->
                    // The new handle is held by another live user
                    return! error ctx 409 "conflict" "That handle belongs to another user"
        }

let endpoints: Endpoint list = [ PUT [ routef "/internal/actors/{%s}" refresh ] ]
