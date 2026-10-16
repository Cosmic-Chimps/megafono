/// Writing to the queues. Both helpers take the caller's open transaction, so the queue rows commit together
/// with whatever caused them (FR-050) and the NOTIFY is only delivered once they are visible.
module Megafono.Infrastructure.Outbox

open System
open System.Threading.Tasks
open Npgsql
open NpgsqlTypes
open Megafono.Domain

let deliveriesChannel = "megafono_deliveries"
let eventsChannel = "megafono_app_events"

let private notify (tx: NpgsqlTransaction) (channel: string) =
    task {
        use command = new NpgsqlCommand("SELECT pg_notify(@channel, '')", tx.Connection, tx)
        command.Parameters.AddWithValue("channel", channel) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

/// The host key a delivery is limited by: lowercase host, plus the port when it is not the default
let hostOf (inbox: string) : string = Uri(inbox).Authority.ToLowerInvariant()

let private insertDeliveries (tx: NpgsqlTransaction) (time: TimeProvider) (activityId: Guid) (inboxes: string list) (filtered: bool) : Task<int> =
    task {
        let distinct = inboxes |> List.distinct |> Array.ofList

        if distinct.Length = 0 then
            return 0
        else
            let now = time.GetUtcNow()

            // The inboxes a block covers for the activity's author are left out (R-45 step 2)
            let covered =
                if filtered then
                    "WHERE NOT " + Blocks.deliveryCoveredSql "(SELECT a.local_actor FROM activities a WHERE a.id = @activity)" "i.inbox" "i.host"
                else
                    ""

            use command =
                new NpgsqlCommand(
                    $"""INSERT INTO deliveries (id, activity, inbox_url, host, status, attempts, next_attempt_at, deadline_at, created_at)
                        SELECT gen_random_uuid(), @activity, i.inbox, i.host, 'pending', 0, @now, @deadline, @now
                        FROM unnest(@inboxes, @hosts) AS i(inbox, host)
                        {covered}
                        ON CONFLICT (activity, inbox_url) DO NOTHING""",
                    tx.Connection,
                    tx
                )

            command.Parameters.AddWithValue("activity", activityId) |> ignore
            command.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
            command.Parameters.AddWithValue("deadline", (now + Backoff.deadline).UtcDateTime) |> ignore
            command.Parameters.Add(NpgsqlParameter("inboxes", NpgsqlDbType.Array ||| NpgsqlDbType.Text, Value = distinct)) |> ignore
            command.Parameters.Add(NpgsqlParameter("hosts", NpgsqlDbType.Array ||| NpgsqlDbType.Text, Value = (distinct |> Array.map hostOf))) |> ignore
            let! inserted = command.ExecuteNonQueryAsync()
            do! notify tx deliveriesChannel
            return inserted
    }

/// One `deliveries` row per distinct inbox, due now, with the 48 h deadline, leaving out the inboxes that a block
/// covers for the activity's author (instance blocks and the author's own: a domain block by host, an actor block
/// only for that actor's own inbox). Rows that already exist for the same (activity, inbox) are left alone, so a
/// retried caller creates no second delivery. The activity row must exist. Returns the number of new rows.
let enqueueDeliveries (tx: NpgsqlTransaction) (time: TimeProvider) (activityId: Guid) (inboxes: string list) : Task<int> =
    insertDeliveries tx time activityId inboxes true

/// `enqueueDeliveries` without the block filter, for the block's own `Block`, `Reject` and `Undo` (R-45)
let enqueueDeliveriesUnfiltered (tx: NpgsqlTransaction) (time: TimeProvider) (activityId: Guid) (inboxes: string list) : Task<int> =
    insertDeliveries tx time activityId inboxes false

/// Takes a share lock on the live row of a local actor, for the rest of the transaction. False when the actor is
/// deleted (or unknown): the caller answers 410 and writes nothing. The delete transaction locks the same row
/// `FOR UPDATE`, so an activity of the actor is never created after its `Delete` (specs/003-actor-deletion R-34).
let lockLiveActor (tx: NpgsqlTransaction) (userId: string) : Task<bool> =
    task {
        use command = new NpgsqlCommand("SELECT 1 FROM local_actors WHERE app_user_id = @user AND deleted_at IS NULL FOR SHARE", tx.Connection, tx)
        command.Parameters.AddWithValue("user", userId) |> ignore
        let! found = command.ExecuteScalarAsync()
        return not (isNull found)
    }

/// True when Megáfono knows the actor and it was deleted (to tell a 410 from a 404 after `lockLiveActor` said no)
let isDeletedActor (tx: NpgsqlTransaction) (userId: string) : Task<bool> =
    task {
        use command = new NpgsqlCommand("SELECT 1 FROM local_actors WHERE app_user_id = @user AND deleted_at IS NOT NULL", tx.Connection, tx)
        command.Parameters.AddWithValue("user", userId) |> ignore
        let! found = command.ExecuteScalarAsync()
        return not (isNull found)
    }

/// Queues an app event for `userId`: takes the user's next `seq` (FR-057), stores the full envelope and
/// NOTIFYs. Returns the event id, or None (nothing queued, no `seq` taken) when the actor is deleted: the app
/// hears nothing more about a user it deleted (FR-009 of 003).
let enqueueEvent (tx: NpgsqlTransaction) (time: TimeProvider) (userId: string) (body: Events.EventBody) : Task<string option> =
    task {
        let now = time.GetUtcNow()

        use bump =
            new NpgsqlCommand(
                """UPDATE local_actors SET event_seq = event_seq + CASE WHEN deleted_at IS NULL THEN 1 ELSE 0 END
                   WHERE app_user_id = @user RETURNING event_seq, handle, deleted_at IS NOT NULL""",
                tx.Connection,
                tx
            )

        bump.Parameters.AddWithValue("user", userId) |> ignore
        use! reader = bump.ExecuteReaderAsync()

        if not (reader.Read()) then
            invalidOp $"No local actor '{userId}' to queue an event for"

        let seq = reader.GetInt64 0
        let handle = reader.GetString 1
        let deleted = reader.GetBoolean 2
        do! reader.CloseAsync()

        if deleted then
            return None
        else
            let eventId = Events.newEventId now
            let payload = Events.envelope eventId now seq userId handle body

            use insert =
                new NpgsqlCommand(
                    """INSERT INTO app_events (id, local_actor, seq, event_type, payload, status, attempts, next_attempt_at, created_at)
                       VALUES (@id, @user, @seq, @type, @payload, 'pending', 0, @now, @now)""",
                    tx.Connection,
                    tx
                )

            insert.Parameters.AddWithValue("id", eventId) |> ignore
            insert.Parameters.AddWithValue("user", userId) |> ignore
            insert.Parameters.AddWithValue("seq", seq) |> ignore
            insert.Parameters.AddWithValue("type", Events.typeName body.Type) |> ignore
            insert.Parameters.Add(NpgsqlParameter("payload", NpgsqlDbType.Jsonb, Value = payload)) |> ignore
            insert.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
            let! _ = insert.ExecuteNonQueryAsync()
            do! notify tx eventsChannel
            return Some eventId
    }
