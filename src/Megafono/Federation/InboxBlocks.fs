/// Inbound Block (specs/004-blocks FR-011, FR-012, research R-49): a remote actor blocking one of our users. Its server
/// has already ended the follows on its side, so Megáfono catches up: every follow between the two ends, both ways, and
/// the app hears of each. Nothing is sent back and nothing is kept: a later Follow from either side is an ordinary one
/// (the blocker's server refuses it while its block lasts). `Undo{Block}` goes through `InboxFollows.handleUndo`, which
/// acknowledges it and restores nothing.
module Megafono.Federation.InboxBlocks

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

/// Ends every `pending`/`accepted` follow between `localUser` and the remote actor, in both directions, and queues one
/// `follow.removed` for each; the rows are locked first, in a fixed order
let private removeFollows (env: Env) (tx: NpgsqlTransaction) (localUser: string) (remote: RemoteActor) : Task =
    task {
        use find =
            new NpgsqlCommand(
                """SELECT id, direction FROM follows
                   WHERE local_actor = @local AND remote_actor = @remote AND status IN ('pending', 'accepted')
                   ORDER BY direction, id FOR UPDATE""",
                tx.Connection,
                tx
            )

        find.Parameters.AddWithValue("local", localUser) |> ignore
        find.Parameters.AddWithValue("remote", remote.Id) |> ignore
        use! reader = find.ExecuteReaderAsync()
        let rows = ResizeArray<Guid * string>()

        while reader.Read() do
            rows.Add(reader.GetGuid 0, reader.GetString 1)

        do! reader.CloseAsync()

        for rowId, direction in rows do
            use update = new NpgsqlCommand("UPDATE follows SET status = 'removed', updated_at = @now WHERE id = @id", tx.Connection, tx)
            update.Parameters.AddWithValue("id", rowId) |> ignore
            update.Parameters.AddWithValue("now", env.Time.GetUtcNow().UtcDateTime) |> ignore
            let! _ = update.ExecuteNonQueryAsync()

            let body: Events.EventBody =
                { Type = Events.FollowRemoved
                  Actor = Some(actorSummary remote)
                  Follow = Some { Id = rowId; Direction = direction; Status = "removed" }
                  Extra = [] }

            let! _ = Outbox.enqueueEvent tx env.Time localUser body
            ()
    }

/// A Block by `signer` of one of our actors. Taken only if its `actor` is the signer, its id is on the signer's host and
/// its `object` is a live local actor; anything else is answered 202 and dropped, like every activity that is not for us.
let handleBlock (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let activityId = str activity "id"
        let actor = idOf activity["actor"]
        let target = idOf activity["object"] |> Option.bind (localActorId env.Domain)

        match activityId, actor, target with
        | Some blockId, Some actorId, Some targetUser when actorId = signer.Id && isOnSignerHost signer blockId ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            // Serialized with the actor's deletion, as every write for the actor is: no event after its Delete (R-34)
            let! live = Outbox.lockLiveActor tx targetUser

            if not live then
                do! tx.RollbackAsync()
                return Ignored "the Block is not of a live local actor"
            else
                let! fresh = markProcessed tx env.Time blockId

                if not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "already processed"
                else
                    do! removeFollows env tx targetUser signer
                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Block is not by the signer of one of our actors"
    }
