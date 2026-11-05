/// Group actors announce the replies they receive (feature 006, research R-62 to R-64): the outbound side.
/// `groupAnnounces` tells whether a thread's root author is an announcing Group; `queueAnnounce`, `queueAnnounceUpdate`
/// and `queueUndoAnnounce` write the `activities` row and its deliveries in the caller's transaction, exactly like
/// `Internal/Objects.fs`'s `storeActivity`, so the announce commits with the reply it is about or not at all.
module Megafono.Infrastructure.Announces

open System
open System.Threading.Tasks
open Npgsql
open Megafono.Domain

/// True when `groupId` is a live `Group` the app marked `announcesReplies` (R-61). `FOR KEY SHARE`: a deletion of the
/// Group in flight (`FOR UPDATE`) is waited for, so nothing is ever queued for a Group whose deletion just committed
/// (R-64).
let groupAnnounces (tx: NpgsqlTransaction) (groupId: string) : Task<bool> =
    task {
        use command =
            new NpgsqlCommand(
                "SELECT 1 FROM local_actors WHERE app_user_id = @g AND deleted_at IS NULL AND actor_type = 'Group' AND announces_replies FOR KEY SHARE",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("g", groupId) |> ignore
        let! found = command.ExecuteScalarAsync()
        return not (isNull found)
    }

/// The audience an earlier announce activity was given, so its edit and its retraction can address the same
/// recipients (FR-003): the only thing of its body ever read back.
let private audienceOfAnnounce (tx: NpgsqlTransaction) (announceGuid: Guid) : Task<Audience.Received> =
    task {
        use command = new NpgsqlCommand("SELECT audience FROM activities WHERE id = @id", tx.Connection, tx)
        command.Parameters.AddWithValue("id", announceGuid) |> ignore
        let! value = command.ExecuteScalarAsync()
        return (Audience.receivedFromName (value :?> string)).Value
    }

/// Stores the announce's activity bytes and queues one delivery per the Group's follower inbox, plus the reply's
/// author's own inbox when it is remote (R-62). Shared by `queueAnnounce`, `queueAnnounceUpdate` and `queueUndoAnnounce`.
let private insertAndDeliver
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (activityGuid: Guid)
    (activityType: string)
    (audience: Audience.Received)
    (group: string)
    (authorInbox: string option)
    (body: Text.Json.Nodes.JsonNode)
    : Task =
    task {
        use insert =
            new NpgsqlCommand(
                """INSERT INTO activities (id, local_actor, object, activity_type, audience, body, created_at)
                   VALUES (@id, @actor, NULL, @type, @audience, @body, @now)""",
                tx.Connection,
                tx
            )

        insert.Parameters.AddWithValue("id", activityGuid) |> ignore
        insert.Parameters.AddWithValue("actor", group) |> ignore
        insert.Parameters.AddWithValue("type", activityType) |> ignore
        insert.Parameters.AddWithValue("audience", Audience.receivedName audience) |> ignore
        insert.Parameters.AddWithValue("body", ActivityStreams.toBytes body) |> ignore
        insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
        let! _ = insert.ExecuteNonQueryAsync()
        let! followers = Outbox.followerInboxes tx group
        let! _ = Outbox.enqueueDeliveries tx time activityGuid (followers @ Option.toList authorInbox)
        ()
    }

/// Queues the `Announce` of a new reply (R-62): `author` is the reply's author id and, for a remote reply, its own
/// inbox (`None` for a local reply: the author is addressed through the Group's followers only). Returns the new
/// activity's id, to be remembered on the reply row (`Threads.setAnnounce`) so its edit and deletion can follow it.
let queueAnnounce
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (domain: string)
    (group: string)
    (replyIri: string)
    (audience: Audience.Received)
    (author: string * string option)
    : Task<Guid> =
    task {
        let authorId, authorInbox = author
        let addressing = ActivityStreams.announceAddressing (Ids.actorFollowers domain group) (Some authorId) audience
        let activityGuid = Guid.NewGuid()
        let body = ActivityStreams.announce domain group (Ids.activityId domain activityGuid) (time.GetUtcNow()) addressing replyIri
        do! insertAndDeliver tx time activityGuid "Announce" audience group authorInbox body
        return activityGuid
    }

/// Queues the `Announce{Update}` that follows an edit of an announced reply (R-63). `updateId` is the edit's own
/// activity id (the author's, or ours for a local reply); `announceGuid` is the original `Announce`'s id
/// (`replies.announce_activity`), read back only for the audience it was given.
let queueAnnounceUpdate
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (domain: string)
    (group: string)
    (updateId: string)
    (authorId: string)
    (authorInbox: string option)
    (replyIri: string)
    (announceGuid: Guid)
    : Task<Guid> =
    task {
        let! audience = audienceOfAnnounce tx announceGuid
        let addressing = ActivityStreams.announceAddressing (Ids.actorFollowers domain group) (Some authorId) audience
        let activityGuid = Guid.NewGuid()
        let body = ActivityStreams.announceUpdate domain group (Ids.activityId domain activityGuid) (time.GetUtcNow()) addressing updateId authorId replyIri
        do! insertAndDeliver tx time activityGuid "Announce" audience group authorInbox body
        return activityGuid
    }

/// Queues the `Undo{Announce}` that retracts the boost of a deleted reply (R-63), embedding the original `Announce`
/// by the id it was first sent with.
let queueUndoAnnounce
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (domain: string)
    (group: string)
    (announceGuid: Guid)
    (authorId: string)
    (authorInbox: string option)
    (replyIri: string)
    : Task<Guid> =
    task {
        let! audience = audienceOfAnnounce tx announceGuid
        let addressing = ActivityStreams.announceAddressing (Ids.actorFollowers domain group) (Some authorId) audience
        let activityGuid = Guid.NewGuid()
        let announceIri = Ids.activityId domain announceGuid
        let body = ActivityStreams.undoAnnounce domain group (Ids.activityId domain activityGuid) (time.GetUtcNow()) addressing announceIri replyIri
        do! insertAndDeliver tx time activityGuid "Undo" audience group authorInbox body
        return activityGuid
    }
