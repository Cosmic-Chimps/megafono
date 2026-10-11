/// Threads (feature 002, data-model.md): what an `inReplyTo` points at, the record of every reply in one of the
/// app's threads, and the queries behind the `replies` and context collections. References only: a reply's
/// content never lands in a table here, it travels in the app event (research R-27).
module Megafono.Infrastructure.Threads

open System
open System.Threading.Tasks
open Npgsql
open Megafono.Domain

/// What a reply is a reply to
type Parent =
    { /// The parent's IRI exactly as `inReplyTo` names it
      Iri: string
      /// Set when the parent is one of the app's own objects (a root post or a reply of the app's)
      Object: Guid option
      /// The app user who published the parent, when it is ours
      LocalUser: string option
      /// The remote actor who wrote the parent, when it is a recorded remote reply
      RemoteAuthor: string option
      Root: Guid
      /// The app user who published the thread's root: the one who hears of the replies
      RootAuthor: string }

type Resolution =
    | Resolved of Parent
    /// The parent was deleted
    | Gone
    /// Not one of our objects and not a recorded reply
    | Unknown

let private objectPrefix (domain: string) = Ids.objectId domain Guid.Empty |> fun iri -> iri.Substring(0, iri.Length - 36)

/// The object id inside `https://{domain}/ap/objects/{id}`; anything else is not one of ours
let objectGuidOf (domain: string) (iri: string) : Guid option =
    let prefix = objectPrefix domain

    if iri.StartsWith(prefix, StringComparison.Ordinal) then
        match Guid.TryParseExact(iri.Substring prefix.Length, "D") with
        | true, g -> Some g
        | _ -> None
    else
        None

let private command (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) =
    let c = new NpgsqlCommand(sql, tx.Connection, tx)

    for name, value in parameters do
        c.Parameters.AddWithValue(name, value) |> ignore

    c

let private optionalString (reader: Data.Common.DbDataReader) (i: int) = if reader.IsDBNull i then None else Some(reader.GetString i)
let private optionalGuid (reader: Data.Common.DbDataReader) (i: int) = if reader.IsDBNull i then None else Some(reader.GetGuid i)

/// An `inReplyTo` resolved against our objects first, then the replies recorded in our threads
let resolveParent (tx: NpgsqlTransaction) (domain: string) (iri: string) : Task<Resolution> =
    task {
        match objectGuidOf domain iri with
        | Some objectGuid ->
            use c =
                command
                    tx
                    """SELECT o.state, o.local_actor, COALESCE(o.thread_root, o.id), r.local_actor
                       FROM objects o JOIN objects r ON r.id = COALESCE(o.thread_root, o.id)
                       WHERE o.id = @id"""
                    [ "id", box objectGuid ]

            use! reader = c.ExecuteReaderAsync()

            if not (reader.Read()) then
                return Unknown
            elif reader.GetString 0 = "deleted" then
                return Gone
            else
                return
                    Resolved
                        { Iri = iri
                          Object = Some objectGuid
                          LocalUser = Some(reader.GetString 1)
                          RemoteAuthor = None
                          Root = reader.GetGuid 2
                          RootAuthor = reader.GetString 3 }
        | None ->
            use c =
                command
                    tx
                    """SELECT rp.state, rp.remote_actor, rp.local_object, rp.thread_root, ro.local_actor
                       FROM replies rp JOIN objects ro ON ro.id = rp.thread_root
                       WHERE rp.id = @id"""
                    [ "id", box iri ]

            use! reader = c.ExecuteReaderAsync()

            if not (reader.Read()) then
                return Unknown
            elif reader.GetString 0 = "deleted" then
                return Gone
            else
                return
                    Resolved
                        { Iri = iri
                          Object = optionalGuid reader 2
                          LocalUser = None
                          RemoteAuthor = optionalString reader 1
                          Root = reader.GetGuid 3
                          RootAuthor = reader.GetString 4 }
    }

// ---- recording -------------------------------------------------------------------------------------------

let private insertReply
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (id: string)
    (localObject: Guid option)
    (remoteActor: string option)
    (parent: string)
    (parentObject: Guid option)
    (threadRoot: Guid)
    (audience: string)
    (publishedAt: DateTimeOffset)
    : Task<bool> =
    task {
        let now = time.GetUtcNow().UtcDateTime
        let orNull (value: 'a option) : obj = match value with Some v -> box v | None -> box DBNull.Value

        use c =
            command
                tx
                """INSERT INTO replies (id, local_object, remote_actor, parent, parent_object, thread_root, audience, state,
                                        published_at, first_seen_at, updated_at)
                   VALUES (@id, @local, @remote, @parent, @parentObject, @root, @audience, 'live', @published, @now, @now)
                   ON CONFLICT (id) DO NOTHING"""
                [ "id", box id
                  "local", orNull localObject
                  "remote", orNull remoteActor
                  "parent", box parent
                  "parentObject", orNull parentObject
                  "root", box threadRoot
                  "audience", box audience
                  "published", box publishedAt.UtcDateTime
                  "now", box now ]

        let! inserted = c.ExecuteNonQueryAsync()
        return inserted = 1
    }

/// Records a reply from a remote actor under `parent`. False when this reply id is already recorded.
let recordRemoteReply
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (replyId: string)
    (author: string)
    (parent: Parent)
    (audience: Audience.Received)
    (publishedAt: DateTimeOffset)
    : Task<bool> =
    insertReply tx time replyId None (Some author) parent.Iri parent.Object parent.Root (Audience.receivedName audience) publishedAt

/// Records a reply published by the app: `objectIri` is its own id (and the `id` of the row)
let recordLocalReply
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (objectGuid: Guid)
    (objectIri: string)
    (parent: Parent)
    (audience: Audience.Audience)
    (publishedAt: DateTimeOffset)
    : Task<bool> =
    insertReply tx time objectIri (Some objectGuid) None parent.Iri parent.Object parent.Root (Audience.name audience) publishedAt

/// A recorded reply with what the events about it need
type ReplyRow =
    { Id: string
      RemoteActor: string option
      LocalObject: Guid option
      Parent: string
      ParentObject: Guid option
      /// The app user who published the parent, when the parent is one of our objects
      ParentUser: string option
      ThreadRoot: Guid
      RootAuthor: string
      Audience: string
      State: string
      PublishedAt: DateTimeOffset }

/// The row of a reply, locked until the transaction ends
let findReply (tx: NpgsqlTransaction) (replyId: string) : Task<ReplyRow option> =
    task {
        use c =
            command
                tx
                """SELECT rp.id, rp.remote_actor, rp.local_object, rp.parent, rp.parent_object, po.local_actor, rp.thread_root, ro.local_actor,
                          rp.audience, rp.state, rp.published_at
                   FROM replies rp
                   JOIN objects ro ON ro.id = rp.thread_root
                   LEFT JOIN objects po ON po.id = rp.parent_object
                   WHERE rp.id = @id
                   FOR UPDATE OF rp"""
                [ "id", box replyId ]

        use! reader = c.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { Id = reader.GetString 0
                      RemoteActor = optionalString reader 1
                      LocalObject = optionalGuid reader 2
                      Parent = reader.GetString 3
                      ParentObject = optionalGuid reader 4
                      ParentUser = optionalString reader 5
                      ThreadRoot = reader.GetGuid 6
                      RootAuthor = reader.GetString 7
                      Audience = reader.GetString 8
                      State = reader.GetString 9
                      PublishedAt = reader.GetFieldValue<DateTimeOffset> 10 }
        else
            return None
    }

/// A live reply goes `deleted`; false when it already was (or does not exist)
let markDeleted (tx: NpgsqlTransaction) (time: TimeProvider) (replyId: string) : Task<bool> =
    task {
        let now = time.GetUtcNow().UtcDateTime

        use c =
            command
                tx
                "UPDATE replies SET state = 'deleted', deleted_at = @now, updated_at = @now WHERE id = @id AND state = 'live'"
                [ "id", box replyId; "now", box now ]

        let! changed = c.ExecuteNonQueryAsync()
        return changed = 1
    }

/// An edit of a live reply
let markUpdated (tx: NpgsqlTransaction) (time: TimeProvider) (replyId: string) : Task =
    task {
        use c =
            command tx "UPDATE replies SET updated_at = @now WHERE id = @id AND state = 'live'" [ "id", box replyId; "now", box (time.GetUtcNow().UtcDateTime) ]

        let! _ = c.ExecuteNonQueryAsync()
        ()
    }

// ---- collections -----------------------------------------------------------------------------------------

type CollectionPage =
    { Total: int64
      Items: string list
      /// More ids follow this page
      HasNext: bool }

// The app's own replies first, then remote ones, each oldest first (research R-20)
let private order = "ORDER BY (local_object IS NULL), published_at, id"
let private listable = "state = 'live' AND audience IN ('public', 'unlisted')"

let private ids (reader: Data.Common.DbDataReader) : string list =
    let items = ResizeArray<string>()

    while reader.Read() do
        items.Add(reader.GetString 0)

    List.ofSeq items

/// Page `number` (from 1) of the replies to `parentIri`: public and unlisted, live ones, at most 50 ids
let repliesPage (dataSource: NpgsqlDataSource) (parentIri: string) (number: int) : Task<CollectionPage> =
    task {
        let number = max 1 number
        use count = dataSource.CreateCommand $"SELECT count(*) FROM replies WHERE parent = @parent AND {listable}"
        count.Parameters.AddWithValue("parent", parentIri) |> ignore
        let! total = count.ExecuteScalarAsync()

        use page =
            dataSource.CreateCommand $"SELECT id FROM replies WHERE parent = @parent AND {listable} {order} LIMIT @limit OFFSET @offset"

        page.Parameters.AddWithValue("parent", parentIri) |> ignore
        page.Parameters.AddWithValue("limit", ActivityStreams.pageSize + 1) |> ignore
        page.Parameters.AddWithValue("offset", (number - 1) * ActivityStreams.pageSize) |> ignore
        use! reader = page.ExecuteReaderAsync()
        let items = ids reader
        return { Total = total :?> int64; Items = items |> List.truncate ActivityStreams.pageSize; HasNext = items.Length > ActivityStreams.pageSize }
    }

/// Page `number` (from 1) of the thread rooted at `root`: the root itself first, then every public and unlisted
/// live reply anywhere in the thread, in the same order as `repliesPage`; at most 50 ids a page
let contextPage (dataSource: NpgsqlDataSource) (domain: string) (root: Guid) (number: int) : Task<CollectionPage> =
    task {
        let number = max 1 number
        let size = ActivityStreams.pageSize
        use count = dataSource.CreateCommand $"SELECT count(*) FROM replies WHERE thread_root = @root AND {listable}"
        count.Parameters.AddWithValue("root", root) |> ignore
        let! total = count.ExecuteScalarAsync()

        // The first page spends one of its places on the root
        let offset, limit = if number = 1 then 0, size - 1 else size - 1 + (number - 2) * size, size

        use page =
            dataSource.CreateCommand $"SELECT id FROM replies WHERE thread_root = @root AND {listable} {order} LIMIT @limit OFFSET @offset"

        page.Parameters.AddWithValue("root", root) |> ignore
        page.Parameters.AddWithValue("limit", limit + 1) |> ignore
        page.Parameters.AddWithValue("offset", offset) |> ignore
        use! reader = page.ExecuteReaderAsync()
        let items = ids reader
        let replies = items |> List.truncate limit
        let rootIri = Ids.objectId domain root
        return { Total = 1L + (total :?> int64); Items = (if number = 1 then rootIri :: replies else replies); HasNext = items.Length > limit }
    }
