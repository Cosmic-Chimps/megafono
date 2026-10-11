/// `POST /internal/actors/{id}/follow-requests/{followId}/approve|reject` (contracts/internal-api.md, FR-030).
/// The row change, the Accept or Reject to send and the app event commit in one transaction.
module Megafono.Internal.Follows

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
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

let endpoints: Endpoint list =
    [ POST
          [ routef "/internal/actors/{%s}/follow-requests/{%s}/approve" (fun (user: string) (follow: string) -> answer Approve user follow)
            routef "/internal/actors/{%s}/follow-requests/{%s}/reject" (fun (user: string) (follow: string) -> answer Reject user follow) ] ]
