/// Posts from followed accounts (feature 005, data-model.md): the queries behind `feed_posts` (one row per remote post
/// and local user it was passed to) and `extension_namespaces` (the namespaces the app has published with). References
/// only: a post's content travels in the app event and is never stored here (FR-010).
///
/// Every function takes the caller's transaction. The users a post goes to are locked first, in `app_user_id` order
/// (R-57), and only then read again for follows and blocks, so a block or a deletion committed before the lock is seen
/// and one starting after it waits.
module Megafono.Infrastructure.FeedPosts

open System
open System.Threading.Tasks
open Npgsql

let private command (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) =
    let c = new NpgsqlCommand(sql, tx.Connection, tx)

    for name, value in parameters do
        c.Parameters.AddWithValue(name, value) |> ignore

    c

let private strings (command: NpgsqlCommand) : Task<string list> =
    task {
        use! reader = command.ExecuteReaderAsync()
        let found = ResizeArray<string>()

        while reader.Read() do
            found.Add(reader.GetString 0)

        return List.ofSeq found
    }

/// The live local users with an `accepted` outbound follow of the remote actor `signer`, sorted by id
let followerCandidates (tx: NpgsqlTransaction) (signer: string) : Task<string list> =
    task {
        use c =
            command
                tx
                """SELECT f.local_actor
                   FROM follows f JOIN local_actors a ON a.app_user_id = f.local_actor
                   WHERE f.direction = 'outbound' AND f.status = 'accepted' AND f.remote_actor = @signer
                     AND a.deleted_at IS NULL
                   ORDER BY f.local_actor"""
                [ "signer", box signer ]

        return! strings c
    }

/// Locks the rows of the live users among `users`, in `app_user_id` order (R-57), and returns the ones locked.
/// `FOR NO KEY UPDATE` is the mode `Outbox.enqueueEvent`'s update takes, so queueing an event upgrades nothing.
let lockUsers (tx: NpgsqlTransaction) (users: string list) : Task<string list> =
    task {
        use c =
            command
                tx
                """SELECT app_user_id FROM local_actors
                   WHERE app_user_id = ANY(@users) AND deleted_at IS NULL
                   ORDER BY app_user_id FOR NO KEY UPDATE"""
                [ "users", box (Array.ofList users) ]

        return! strings c
    }

/// The live users among `users` the post of `signer` (on `host`, `host[:port]`) may still go to, sorted: not covered by
/// a block of the user or the instance and, when `mustFollow`, still following the signer (`accepted`). Call it after
/// `lockUsers`, as a statement of its own, so it sees what committed before the lock.
let qualifying (tx: NpgsqlTransaction) (users: string list) (signer: string) (host: string) (mustFollow: bool) : Task<string list> =
    task {
        use c =
            command
                tx
                $"""SELECT a.app_user_id FROM local_actors a
                    WHERE a.app_user_id = ANY(@users) AND a.deleted_at IS NULL
                      AND (NOT @mustFollow OR EXISTS (SELECT 1 FROM follows f
                                                      WHERE f.direction = 'outbound' AND f.status = 'accepted'
                                                        AND f.local_actor = a.app_user_id AND f.remote_actor = @signer))
                      AND NOT {Blocks.coveredSql "a.app_user_id" "@signer" "@host"}
                    ORDER BY a.app_user_id"""
                [ "users", box (Array.ofList users)
                  "mustFollow", box mustFollow
                  "signer", box signer
                  "host", box host ]

        return! strings c
    }

/// Records the post for each of `users` that has no row for it yet and returns those users, sorted: the ones to
/// queue `post.received` for (R-55). `audience` is how the post arrived (`public`, `unlisted`, `followers`, `direct`).
/// `announcedBy` is the Group whose `Announce` brought it, or `None` for a post straight from a followed account
/// (specs/006-group-announce R-66).
let insertRows
    (tx: NpgsqlTransaction)
    (postId: string)
    (signer: string)
    (audience: string)
    (published: DateTimeOffset)
    (announcedBy: string option)
    (users: string list)
    : Task<string list> =
    task {
        let orNull (value: string option) : obj = match value with Some v -> box v | None -> box DBNull.Value

        use c =
            command
                tx
                """INSERT INTO feed_posts (id, local_actor, remote_actor, audience, published_at, announced_by)
                   SELECT @id, u, @signer, @audience, @published, @announced FROM unnest(@users::text[]) AS u ORDER BY u
                   ON CONFLICT DO NOTHING
                   RETURNING local_actor"""
                [ "id", box postId
                  "signer", box signer
                  "audience", box audience
                  "published", box published.UtcDateTime
                  "announced", orNull announcedBy
                  "users", box (Array.ofList users) ]

        let! inserted = strings c
        return List.sort inserted
    }

/// What a live row remembers of a post for one user
type FeedRow =
    { LocalUser: string
      Audience: string
      PublishedAt: DateTimeOffset }

/// The live rows of a post by `signer`, sorted by user: who was told about it, and how it arrived
let liveRows (tx: NpgsqlTransaction) (postId: string) (signer: string) : Task<FeedRow list> =
    task {
        use c =
            command
                tx
                """SELECT local_actor, audience, published_at FROM feed_posts
                   WHERE id = @id AND remote_actor = @signer AND state = 'live'
                   ORDER BY local_actor"""
                [ "id", box postId; "signer", box signer ]

        use! reader = c.ExecuteReaderAsync()
        let rows = ResizeArray<FeedRow>()

        while reader.Read() do
            rows.Add
                { LocalUser = reader.GetString 0
                  Audience = reader.GetString 1
                  PublishedAt = reader.GetFieldValue<DateTimeOffset> 2 }

        return List.ofSeq rows
    }

/// Notes the edit on the live rows of the post
let markUpdated (tx: NpgsqlTransaction) (time: TimeProvider) (postId: string) (signer: string) : Task =
    task {
        use c =
            command
                tx
                "UPDATE feed_posts SET updated_at = @now WHERE id = @id AND remote_actor = @signer AND state = 'live'"
                [ "now", box (time.GetUtcNow().UtcDateTime); "id", box postId; "signer", box signer ]

        let! _ = c.ExecuteNonQueryAsync()
        ()
    }

/// Turns the live rows of the post `deleted` (once: a deleted row is never live again) and returns their users, sorted
let markDeleted (tx: NpgsqlTransaction) (time: TimeProvider) (postId: string) (signer: string) : Task<string list> =
    task {
        use c =
            command
                tx
                """UPDATE feed_posts SET state = 'deleted', deleted_at = @now, updated_at = @now
                   WHERE id = @id AND remote_actor = @signer AND state = 'live'
                   RETURNING local_actor"""
                [ "now", box (time.GetUtcNow().UtcDateTime); "id", box postId; "signer", box signer ]

        let! deleted = strings c
        return List.sort deleted
    }

// ---- a followed Group's announces (feature 006, specs/006-group-announce R-66, R-67) -----------------------

/// The live rows of a post that this Group announced, sorted by user: who is to hear of its edit or its retraction
let announcedRows (tx: NpgsqlTransaction) (postId: string) (group: string) : Task<FeedRow list> =
    task {
        use c =
            command
                tx
                """SELECT local_actor, audience, published_at FROM feed_posts
                   WHERE id = @id AND announced_by = @group AND state = 'live'
                   ORDER BY local_actor"""
                [ "id", box postId; "group", box group ]

        use! reader = c.ExecuteReaderAsync()
        let rows = ResizeArray<FeedRow>()

        while reader.Read() do
            rows.Add
                { LocalUser = reader.GetString 0
                  Audience = reader.GetString 1
                  PublishedAt = reader.GetFieldValue<DateTimeOffset> 2 }

        return List.ofSeq rows
    }

/// Turns the live rows this Group announced `deleted` (the `Undo{Announce}` path) and returns their users, sorted
let markDeletedAnnounced (tx: NpgsqlTransaction) (time: TimeProvider) (postId: string) (group: string) : Task<string list> =
    task {
        use c =
            command
                tx
                """UPDATE feed_posts SET state = 'deleted', deleted_at = @now, updated_at = @now
                   WHERE id = @id AND announced_by = @group AND state = 'live'
                   RETURNING local_actor"""
                [ "now", box (time.GetUtcNow().UtcDateTime); "id", box postId; "group", box group ]

        let! deleted = strings c
        return List.sort deleted
    }

/// The live users among `users` an announced post may still go to, sorted: not covered by a block of the Group nor
/// of the author, optionally still following the Group (`mustFollowGroup`), and optionally excluding whoever has an
/// accepted outbound follow of the author (`excludeAuthorFollowers`: those hear from the author's own edit instead, R-67)
let qualifyingAnnounced
    (tx: NpgsqlTransaction)
    (users: string list)
    (group: string)
    (groupHost: string)
    (author: string)
    (authorHost: string)
    (mustFollowGroup: bool)
    (excludeAuthorFollowers: bool)
    : Task<string list> =
    task {
        use c =
            command
                tx
                $"""SELECT a.app_user_id FROM local_actors a
                    WHERE a.app_user_id = ANY(@users) AND a.deleted_at IS NULL
                      AND (NOT @mustFollowGroup OR EXISTS (SELECT 1 FROM follows f
                                                      WHERE f.direction = 'outbound' AND f.status = 'accepted'
                                                        AND f.local_actor = a.app_user_id AND f.remote_actor = @group))
                      AND (NOT @excludeAuthorFollowers OR NOT EXISTS (SELECT 1 FROM follows f
                                                      WHERE f.direction = 'outbound' AND f.status = 'accepted'
                                                        AND f.local_actor = a.app_user_id AND f.remote_actor = @author))
                      AND NOT {Blocks.coveredSql "a.app_user_id" "@group" "@groupHost"}
                      AND NOT {Blocks.coveredSql "a.app_user_id" "@author" "@authorHost"}
                    ORDER BY a.app_user_id"""
                [ "users", box (Array.ofList users)
                  "mustFollowGroup", box mustFollowGroup
                  "excludeAuthorFollowers", box excludeAuthorFollowers
                  "group", box group
                  "groupHost", box groupHost
                  "author", box author
                  "authorHost", box authorHost ]

        return! strings c
    }

// ---- the app's extension namespaces (R-58) ---------------------------------------------------------------

/// Remembers a namespace the app published with; a repeat changes nothing
let recordNamespace (tx: NpgsqlTransaction) (ns: string) : Task =
    task {
        use c = command tx "INSERT INTO extension_namespaces (namespace) VALUES (@ns) ON CONFLICT DO NOTHING" [ "ns", box ns ]
        let! _ = c.ExecuteNonQueryAsync()
        ()
    }

/// Which of the `candidates` the app has published with
let knownNamespaces (tx: NpgsqlTransaction) (candidates: string list) : Task<Set<string>> =
    task {
        if candidates.IsEmpty then
            return Set.empty
        else
            use c =
                command
                    tx
                    "SELECT namespace FROM extension_namespaces WHERE namespace = ANY(@candidates)"
                    [ "candidates", box (Array.ofList candidates) ]

            let! found = strings c
            return Set.ofList found
    }
