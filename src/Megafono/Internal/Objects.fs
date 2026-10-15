/// Publishing: `POST /internal/actors/{id}/objects`, `PUT` and `DELETE /internal/objects/{id}` and the delivery
/// status of an object (contracts/internal-api.md; FR-040 to FR-044, FR-046, FR-050, FR-054)
module Megafono.Internal.Objects

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open NpgsqlTypes
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient

/// Request bodies larger than this are refused (data-model.md validation rules)
let maxBodyBytes = 256 * 1024
let maxIdempotencyKeyLength = 200
let private prefixPattern = Regex(@"^[a-z][a-z0-9]{0,15}\z", RegexOptions.Compiled)

let private error (ctx: HttpContext) (status: int) (code: string) (message: string) =
    ctx.SetStatusCode status
    ctx.WriteJson {| error = code; message = message |}

// ---- validation ------------------------------------------------------------------------------------------

type private Parsed =
    { Audience: Audience.Audience option
      Content: ActivityStreams.ObjectContent
      Extensions: ActivityStreams.Extensions option }

let private isUrl (allowHttp: bool) (text: string) =
    match Uri.TryCreate(text, UriKind.Absolute) with
    | true, uri -> uri.Scheme = Uri.UriSchemeHttps || (allowHttp && uri.Scheme = Uri.UriSchemeHttp)
    | _ -> false

/// A string property: absent or null is None, any other type is an error
let private optionalText (o: JsonObject) (name: string) : Result<string option, string> =
    match o[name] with
    | null -> Ok None
    | :? JsonValue as v when v.GetValueKind() = Text.Json.JsonValueKind.String -> Ok(Some(v.GetValue<string>()))
    | _ -> Error $"'{name}' must be a string"

let private optionalUrl (allowHttp: bool) (o: JsonObject) (name: string) : Result<string option, string> =
    match optionalText o name with
    | Ok(Some url) when not (isUrl allowHttp url) -> Error $"'{name}' must be an absolute https URL"
    | other -> other

let private collect (results: Result<'a, string> list) : Result<'a list, string> =
    results
    |> List.fold
        (fun acc r ->
            match acc, r with
            | Error e, _ -> Error e
            | _, Error e -> Error e
            | Ok items, Ok item -> Ok(item :: items))
        (Ok [])
    |> Result.map List.rev

let private parseAttachment (allowHttp: bool) (node: JsonNode) : Result<ActivityStreams.Attachment, string> =
    match node with
    | :? JsonObject as o ->
        match optionalUrl allowHttp o "url", optionalText o "mediaType", optionalText o "name" with
        | Ok(Some url), Ok mediaType, Ok name -> Ok { Url = url; MediaType = mediaType; Name = name }
        | Ok None, _, _ -> Error "every attachment needs an absolute https 'url'"
        | Error e, _, _
        | _, Error e, _
        | _, _, Error e -> Error e
    | _ -> Error "'attachment' entries must be objects"

let private parseExtensions (node: JsonNode) : Result<ActivityStreams.Extensions option, string> =
    match node with
    | null -> Ok None
    | :? JsonObject as e ->
        match optionalText e "namespace", optionalText e "prefix", e["data"] with
        | Ok(Some ns), Ok(Some prefix), (:? JsonObject as data) ->
            if not (Uri.IsWellFormedUriString(ns, UriKind.Absolute)) then
                Error "extensions.namespace must be an absolute IRI"
            elif not (prefixPattern.IsMatch prefix) then
                Error "extensions.prefix must match ^[a-z][a-z0-9]{0,15}$"
            elif data |> Seq.exists (fun kv -> kv.Key.StartsWith '@' || kv.Key.Contains ':') then
                Error "extensions.data keys must not start with '@' or contain ':'"
            else
                Ok(Some { Namespace = ns; Prefix = prefix; Data = data })
        | _ -> Error "extensions needs a 'namespace', a 'prefix' and a 'data' object"
    | _ -> Error "'extensions' must be an object"

/// The `object` part (and `extensions`, `audience`) of a publish or update body. An update may send the object
/// itself as the body, with `extensions` beside it.
let private parseBody (allowHttp: bool) (root: JsonNode) : Result<Parsed, string> =
    match root with
    | :? JsonObject as top ->
        let objectNode = if isNull top["object"] then root else top["object"]

        match objectNode with
        | :? JsonObject as o ->
            let audience =
                match optionalText top "audience" with
                | Ok None -> Ok None
                | Ok(Some text) ->
                    match Audience.parse text with
                    | Some a -> Ok(Some a)
                    | None -> Error "'audience' must be public, unlisted or followers"
                | Error e -> Error e

            let objectType =
                match optionalText o "type" with
                | Ok(Some("Note" | "Article" as t)) -> Ok t
                | _ -> Error "object.type must be Note or Article"

            let content =
                match optionalText o "content" with
                | Ok(Some text) when text.Trim() <> "" -> Ok text
                | _ -> Error "object.content must be a non-empty string"

            let sensitive =
                match o["sensitive"] with
                | null -> Ok false
                | :? JsonValue as v when v.GetValueKind() = Text.Json.JsonValueKind.True -> Ok true
                | :? JsonValue as v when v.GetValueKind() = Text.Json.JsonValueKind.False -> Ok false
                | _ -> Error "'sensitive' must be true or false"

            let attachments =
                match o["attachment"] with
                | null -> Ok []
                | :? JsonArray as items -> items |> Seq.map (parseAttachment allowHttp) |> List.ofSeq |> collect
                | _ -> Error "'attachment' must be an array"

            match audience, objectType, content, sensitive, attachments with
            | Error e, _, _, _, _
            | _, Error e, _, _, _
            | _, _, Error e, _, _
            | _, _, _, Error e, _
            | _, _, _, _, Error e -> Error e
            | Ok audience, Ok objectType, Ok content, Ok sensitive, Ok attachments ->
                match optionalUrl allowHttp o "url", optionalUrl allowHttp o "inReplyTo", optionalText o "summary", parseExtensions top["extensions"] with
                | Ok url, Ok inReplyTo, Ok summary, Ok extensions ->
                    Ok
                        { Audience = audience
                          Content =
                            { ObjectType = objectType
                              Content = content
                              Url = url
                              InReplyTo = inReplyTo
                              Summary = summary
                              Sensitive = sensitive
                              Attachments = attachments }
                          Extensions = extensions }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e
        | _ -> Error "'object' must be an object"
    | _ -> Error "the body must be a JSON object"

/// Object properties sorted by name at every level, so the same request in another property order hashes the same
let rec private canonicalize (node: JsonNode) : JsonNode =
    match node with
    | :? JsonObject as o ->
        let sorted = JsonObject()

        for entry in o |> Seq.sortWith (fun a b -> String.CompareOrdinal(a.Key, b.Key)) do
            sorted[entry.Key] <- canonicalize entry.Value

        sorted
    | :? JsonArray as a -> JsonArray(a |> Seq.map canonicalize |> Array.ofSeq)
    | null -> null
    | other -> other.DeepClone()

let requestHash (root: JsonNode) : byte[] =
    SHA256.HashData(Encoding.UTF8.GetBytes((canonicalize root).ToJsonString ActivityStreams.jsonOptions))

/// The body, or None when it is over the limit
let private readBody (ctx: HttpContext) : Task<byte[] option> =
    task {
        if ctx.Request.ContentLength.HasValue && ctx.Request.ContentLength.Value > int64 maxBodyBytes then
            return None
        else
            use buffer = new MemoryStream()
            let chunk = Array.zeroCreate<byte> 8192
            let mutable fin = false
            let mutable tooLarge = false

            while not fin do
                let! read = ctx.Request.Body.ReadAsync(chunk, 0, chunk.Length)

                if read = 0 then
                    fin <- true
                else
                    buffer.Write(chunk, 0, read)

                    if buffer.Length > int64 maxBodyBytes then
                        tooLarge <- true
                        fin <- true

            return if tooLarge then None else Some(buffer.ToArray())
    }

// ---- storage ---------------------------------------------------------------------------------------------

/// The distinct inboxes of the author's accepted followers right now, the shared inbox where there is one (R-08)
let private recipientInboxes (tx: NpgsqlTransaction) (appUserId: string) : Task<string list> =
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

/// Stores the activity bytes and queues one delivery per recipient inbox, in the caller's transaction
let private storeActivity
    (tx: NpgsqlTransaction)
    (time: TimeProvider)
    (appUserId: string)
    (objectGuid: Guid)
    (activityGuid: Guid)
    (activityType: string)
    (audience: Audience.Audience)
    (body: byte[])
    (extraInboxes: string list)
    =
    task {
        use insert =
            new NpgsqlCommand(
                """INSERT INTO activities (id, local_actor, object, activity_type, audience, body, created_at)
                   VALUES (@id, @actor, @object, @type, @audience, @body, @now)""",
                tx.Connection,
                tx
            )

        insert.Parameters.AddWithValue("id", activityGuid) |> ignore
        insert.Parameters.AddWithValue("actor", appUserId) |> ignore
        insert.Parameters.AddWithValue("object", objectGuid) |> ignore
        insert.Parameters.AddWithValue("type", activityType) |> ignore
        insert.Parameters.AddWithValue("audience", Audience.name audience) |> ignore
        insert.Parameters.AddWithValue("body", body) |> ignore
        insert.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore
        let! _ = insert.ExecuteNonQueryAsync()
        let! inboxes = recipientInboxes tx appUserId
        // Enqueued once per distinct inbox: a parent's author who also follows is not sent the same request twice
        let! _ = Outbox.enqueueDeliveries tx time activityGuid (inboxes @ extraInboxes)
        ()
    }

/// What a reply adds to its object and its recipients (research R-23): the parent's remote author is addressed in
/// `cc`, mentioned, and gets the activity at their own inbox. A parent of ours, or one of a local user's, adds nothing.
type private ReplyPlan =
    { Thread: ActivityStreams.ThreadInfo
      PersonalInboxes: string list }

let private planReply (tx: NpgsqlTransaction) (root: Guid) (parentRemoteAuthor: string option) : Task<ReplyPlan> =
    task {
        let plain = { Thread = { Root = root; ExtraCc = []; Mentions = [] }; PersonalInboxes = [] }

        match parentRemoteAuthor with
        | None -> return plain
        | Some actorId ->
            match! Megafono.Federation.InboxCommon.loadRemote tx actorId with
            | None -> return plain
            | Some author ->
                let name = author.Handle |> Option.map (fun h -> "@" + h) |> Option.defaultValue author.Id

                return
                    { Thread = { Root = root; ExtraCc = [ author.Id ]; Mentions = [ { Href = author.Id; Name = name } ] }
                      PersonalInboxes = [ author.Inbox ] }
    }

/// The thread an object belongs to: its root's id (its own when it is a root)
let private threadOf (domain: string) (objectGuid: Guid) (threadRoot: Guid option) =
    let root = defaultArg threadRoot objectGuid
    {| objectId = root.ToString "D"; id = Ids.objectId domain root |}

let private publishResponse (domain: string) (objectGuid: Guid) (threadRoot: Guid option) =
    {| id = objectGuid.ToString "D"
       objectUrl = Ids.objectId domain objectGuid
       deliveriesUrl = $"/internal/objects/{objectGuid:D}/deliveries"
       thread = threadOf domain objectGuid threadRoot |}

let private jsonb (text: string) = NpgsqlParameter("doc", NpgsqlDbType.Jsonb, Value = text)

// ---- publish ---------------------------------------------------------------------------------------------

let private publish (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            let time = ctx.GetService<TimeProvider>()
            let dataSource = ctx.GetService<NpgsqlDataSource>()

            match! readBody ctx with
            | None -> return! error ctx 413 "invalid_request" $"The request body is over {maxBodyBytes / 1024} KB"
            | Some body ->
                let key =
                    match ctx.Request.Headers["Idempotency-Key"].ToString() with
                    | "" -> None
                    | k -> Some k

                let root =
                    try
                        JsonNode.Parse(ReadOnlySpan body)
                    with :? Text.Json.JsonException ->
                        null

                let! lookup = if Ids.isValidAppUserId appUserId then ctx.GetService<AppClient>().ById appUserId else Task.FromResult NotFound

                match lookup with
                | NotFound -> return! error ctx 404 "not_found" "The app does not know this user"
                | Deleted -> return! error ctx 410 "gone" "The actor was deleted"
                | UnavailableNoFallback -> return! error ctx 503 "app_unavailable" "The app did not answer the actor lookup"
                | Found actor
                | UnavailableWithFallback actor ->
                    match key, parseBody options.Federation.AllowPrivateAddresses root with
                    | Some k, _ when k.Length > maxIdempotencyKeyLength ->
                        return! error ctx 400 "invalid_request" $"Idempotency-Key is over {maxIdempotencyKeyLength} characters"
                    | _, Error message -> return! error ctx 400 "invalid_request" message
                    | _, Ok parsed when parsed.Audience.IsNone -> return! error ctx 400 "invalid_request" "'audience' is required"
                    | _, Ok parsed when parsed.Content.InReplyTo.IsSome && parsed.Audience = Some Audience.Followers ->
                        return! error ctx 400 "invalid_request" "A reply must be public or unlisted"
                    | _, Ok parsed ->
                        let audience = parsed.Audience.Value
                        let now = time.GetUtcNow()
                        let objectGuid = Guid.NewGuid()
                        let activityGuid = Guid.NewGuid()
                        use! connection = dataSource.OpenConnectionAsync()
                        use! tx = connection.BeginTransactionAsync()
                        // Serialized with the actor's deletion: nothing is created after its Delete (R-34)
                        let! live = Outbox.lockLiveActor tx actor.AppUserId

                        // A reply is to one of our objects or to a reply recorded in one of our threads (FR-010)
                        let! resolution =
                            task {
                                match parsed.Content.InReplyTo with
                                | Some iri ->
                                    let! resolved = Threads.resolveParent tx options.Domain iri
                                    return Some resolved
                                | None -> return None
                            }

                        match resolution with
                        | _ when not live ->
                            do! tx.RollbackAsync()
                            return! error ctx 410 "gone" "The actor was deleted"
                        | Some Threads.Unknown -> return! error ctx 400 "invalid_request" "unknown inReplyTo"
                        | Some Threads.Gone -> return! error ctx 410 "gone" "The object replied to was deleted"
                        | _ ->
                            let parent =
                                match resolution with
                                | Some(Threads.Resolved p) -> Some p
                                | _ -> None

                            let threadRoot = parent |> Option.map (fun p -> p.Root)
                            let! plan = planReply tx (defaultArg threadRoot objectGuid) (parent |> Option.bind (fun p -> p.RemoteAuthor))

                            let document =
                                ActivityStreams.noteObjectIn options.Domain actor.AppUserId objectGuid audience now None parsed.Content parsed.Extensions plan.Thread

                            let activity =
                                ActivityStreams.create options.Domain actor.AppUserId (Ids.activityId options.Domain activityGuid) now audience parsed.Extensions document
                                |> ActivityStreams.toBytes

                            use insert =
                                new NpgsqlCommand(
                                    """INSERT INTO objects (id, local_actor, audience, object_type, document, idempotency_key, request_hash, in_reply_to, thread_root, created_at, updated_at)
                                       VALUES (@id, @actor, @audience, @type, @doc, @key, @hash, @inReplyTo, @root, @now, @now)
                                       ON CONFLICT (local_actor, idempotency_key) DO NOTHING""",
                                    connection,
                                    tx
                                )

                            insert.Parameters.AddWithValue("id", objectGuid) |> ignore
                            insert.Parameters.AddWithValue("actor", actor.AppUserId) |> ignore
                            insert.Parameters.AddWithValue("audience", Audience.name audience) |> ignore
                            insert.Parameters.AddWithValue("type", parsed.Content.ObjectType) |> ignore
                            insert.Parameters.Add(jsonb (document.ToJsonString ActivityStreams.jsonOptions)) |> ignore
                            insert.Parameters.AddWithValue("key", (match key with Some k -> box k | None -> box DBNull.Value)) |> ignore
                            insert.Parameters.AddWithValue("hash", requestHash root) |> ignore
                            insert.Parameters.AddWithValue("inReplyTo", (match parent with Some p -> box p.Iri | None -> box DBNull.Value)) |> ignore
                            insert.Parameters.AddWithValue("root", (match threadRoot with Some r -> box r | None -> box DBNull.Value)) |> ignore
                            insert.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
                            let! inserted = insert.ExecuteNonQueryAsync()

                            if inserted = 1 then
                                match parent with
                                | Some p ->
                                    let! _ = Threads.recordLocalReply tx time objectGuid (Ids.objectId options.Domain objectGuid) p audience now
                                    ()
                                | None -> ()

                                do! storeActivity tx time actor.AppUserId objectGuid activityGuid "Create" audience activity plan.PersonalInboxes
                                do! tx.CommitAsync()
                                ctx.SetStatusCode 201
                                return! ctx.WriteJson(publishResponse options.Domain objectGuid threadRoot)
                            else
                                // The key was used before (possibly by a request still committing, which the insert waited for)
                                do! tx.RollbackAsync()

                                // On the connection already held: a second one could starve a pool full of waiting twins
                                use existing =
                                    new NpgsqlCommand("SELECT id, request_hash, thread_root FROM objects WHERE local_actor = @actor AND idempotency_key = @key", connection)

                                existing.Parameters.AddWithValue("actor", actor.AppUserId) |> ignore
                                existing.Parameters.AddWithValue("key", key.Value) |> ignore
                                use! reader = existing.ExecuteReaderAsync()
                                reader.Read() |> ignore
                                let originalId = reader.GetGuid 0
                                let originalHash = reader.GetFieldValue<byte[]> 1
                                let originalRoot = if reader.IsDBNull 2 then None else Some(reader.GetGuid 2)

                                if CryptographicOperations.FixedTimeEquals(originalHash, requestHash root) then
                                    return! ctx.WriteJson(publishResponse options.Domain originalId originalRoot)
                                else
                                    return! error ctx 409 "conflict" "This Idempotency-Key was used with a different request"
        }

// ---- update and delete -----------------------------------------------------------------------------------

type private StoredObject =
    { AppUserId: string
      Audience: Audience.Audience
      ObjectType: string
      Deleted: bool
      Document: JsonNode
      CreatedAt: DateTimeOffset
      /// Set on a reply: what it answers and the root of its thread
      InReplyTo: string option
      ThreadRoot: Guid option }

let private parseGuid (text: string) : Guid option =
    match Guid.TryParseExact(text, "D") with
    | true, g -> Some g
    | _ -> None

/// The object row, locked until the transaction ends
let private loadObject (tx: NpgsqlTransaction) (objectGuid: Guid) : Task<StoredObject option> =
    task {
        use command =
            new NpgsqlCommand(
                "SELECT local_actor, audience, object_type, state, document::text, created_at, in_reply_to, thread_root FROM objects WHERE id = @id FOR UPDATE",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", objectGuid) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { AppUserId = reader.GetString 0
                      Audience = (Audience.parse (reader.GetString 1)).Value
                      ObjectType = reader.GetString 2
                      Deleted = (reader.GetString 3 = "deleted")
                      Document = JsonNode.Parse(reader.GetString 4)
                      CreatedAt = reader.GetFieldValue<DateTimeOffset> 5
                      InReplyTo = (if reader.IsDBNull 6 then None else Some(reader.GetString 6))
                      ThreadRoot = (if reader.IsDBNull 7 then None else Some(reader.GetGuid 7)) }
        else
            return None
    }

/// A reply's addressing and recipients, rebuilt for its edits and deletion: the same as when it was published
let private planFor (tx: NpgsqlTransaction) (objectGuid: Guid) (stored: StoredObject) : Task<ReplyPlan> =
    task {
        match stored.InReplyTo with
        | None -> return { Thread = { Root = objectGuid; ExtraCc = []; Mentions = [] }; PersonalInboxes = [] }
        | Some parentIri ->
            let! author = Threads.remoteAuthorOf tx parentIri
            return! planReply tx (defaultArg stored.ThreadRoot objectGuid) author
    }

let private gone (ctx: HttpContext) = error ctx 410 "gone" "The object was deleted"

/// Takes the share lock on the live actor row that owns the object BEFORE the object row is locked, the order the
/// deletion of the actor uses, so the two cannot deadlock. False when the actor was deleted.
let private lockAuthor (tx: NpgsqlTransaction) (objectGuid: Guid) : Task<bool> =
    task {
        use command = new NpgsqlCommand("SELECT local_actor FROM objects WHERE id = @id", tx.Connection, tx)
        command.Parameters.AddWithValue("id", objectGuid) |> ignore

        match! command.ExecuteScalarAsync() with
        | :? string as author -> return! Outbox.lockLiveActor tx author
        | _ -> return true
    }

let private update (objectId: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            let time = ctx.GetService<TimeProvider>()
            let dataSource = ctx.GetService<NpgsqlDataSource>()

            match parseGuid objectId with
            | None -> return! error ctx 404 "not_found" "No such object"
            | Some objectGuid ->
                match! readBody ctx with
                | None -> return! error ctx 413 "invalid_request" $"The request body is over {maxBodyBytes / 1024} KB"
                | Some body ->
                    let root =
                        try
                            JsonNode.Parse(ReadOnlySpan body)
                        with :? Text.Json.JsonException ->
                            null

                    use! connection = dataSource.OpenConnectionAsync()
                    use! tx = connection.BeginTransactionAsync()
                    let! live = lockAuthor tx objectGuid

                    match! loadObject tx objectGuid with
                    | None -> return! error ctx 404 "not_found" "No such object"
                    | Some _ when not live -> return! error ctx 410 "gone" "The actor was deleted"
                    | Some stored when stored.Deleted -> return! gone ctx
                    | Some stored ->
                        match parseBody options.Federation.AllowPrivateAddresses root with
                        | Error message -> return! error ctx 400 "invalid_request" message
                        | Ok parsed when parsed.Audience |> Option.exists (fun a -> a <> stored.Audience) ->
                            return! error ctx 400 "invalid_request" "The audience of an object cannot change"
                        | Ok parsed when parsed.Content.ObjectType <> stored.ObjectType ->
                            return! error ctx 400 "invalid_request" "The type of an object cannot change"
                        | Ok parsed when parsed.Content.InReplyTo |> Option.exists (fun r -> Some r <> stored.InReplyTo) ->
                            return! error ctx 400 "invalid_request" "inReplyTo cannot change"
                        | Ok parsed ->
                            let now = time.GetUtcNow()
                            let activityGuid = Guid.NewGuid()
                            let! plan = planFor tx objectGuid stored
                            // Whatever the body says, a reply stays a reply to what it answered
                            let content = { parsed.Content with InReplyTo = stored.InReplyTo }

                            let document =
                                ActivityStreams.noteObjectIn
                                    options.Domain
                                    stored.AppUserId
                                    objectGuid
                                    stored.Audience
                                    stored.CreatedAt
                                    (Some now)
                                    content
                                    parsed.Extensions
                                    plan.Thread

                            let activity =
                                ActivityStreams.update
                                    options.Domain
                                    stored.AppUserId
                                    (Ids.activityId options.Domain activityGuid)
                                    now
                                    stored.Audience
                                    parsed.Extensions
                                    document
                                |> ActivityStreams.toBytes

                            use command = new NpgsqlCommand("UPDATE objects SET document = @doc, updated_at = @now WHERE id = @id", connection, tx)
                            command.Parameters.Add(jsonb (document.ToJsonString ActivityStreams.jsonOptions)) |> ignore
                            command.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
                            command.Parameters.AddWithValue("id", objectGuid) |> ignore
                            let! _ = command.ExecuteNonQueryAsync()
                            do! storeActivity tx time stored.AppUserId objectGuid activityGuid "Update" stored.Audience activity plan.PersonalInboxes
                            do! tx.CommitAsync()
                            return! ctx.WriteJson(publishResponse options.Domain objectGuid stored.ThreadRoot)
        }

let private delete (objectId: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            let time = ctx.GetService<TimeProvider>()
            let dataSource = ctx.GetService<NpgsqlDataSource>()

            match parseGuid objectId with
            | None -> return! error ctx 404 "not_found" "No such object"
            | Some objectGuid ->
                use! connection = dataSource.OpenConnectionAsync()
                use! tx = connection.BeginTransactionAsync()
                // Not live means the actor was deleted, and every object of it is deleted: the repeat answer below
                let! _ = lockAuthor tx objectGuid

                match! loadObject tx objectGuid with
                | None -> return! error ctx 404 "not_found" "No such object"
                | Some stored when stored.Deleted ->
                    // A repeat answers like the first call; nothing more is sent
                    ctx.SetStatusCode 202
                    return! ctx.WriteJson {| id = objectGuid.ToString "D"; deliveriesUrl = $"/internal/objects/{objectGuid:D}/deliveries" |}
                | Some stored ->
                    let now = time.GetUtcNow()
                    let activityGuid = Guid.NewGuid()

                    let! plan = planFor tx objectGuid stored

                    // The same `to` and `cc` as the object had, so the Delete reaches everyone the object did
                    let strings (name: string) =
                        match stored.Document[name] with
                        | :? JsonArray as items -> [ for item in items -> item.GetValue<string>() ]
                        | _ -> []

                    let activity =
                        ActivityStreams.deleteObjectAddressed
                            options.Domain
                            stored.AppUserId
                            (Ids.activityId options.Domain activityGuid)
                            now
                            { To = strings "to"; Cc = strings "cc" }
                            objectGuid
                        |> ActivityStreams.toBytes

                    use command =
                        new NpgsqlCommand("UPDATE objects SET state = 'deleted', deleted_at = @now, updated_at = @now WHERE id = @id", connection, tx)

                    command.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
                    command.Parameters.AddWithValue("id", objectGuid) |> ignore
                    let! _ = command.ExecuteNonQueryAsync()
                    // A reply leaves its parent's `replies` collection with it
                    let! _ = Threads.markDeleted tx time (Ids.objectId options.Domain objectGuid)
                    do! storeActivity tx time stored.AppUserId objectGuid activityGuid "Delete" stored.Audience activity plan.PersonalInboxes
                    do! tx.CommitAsync()
                    ctx.SetStatusCode 202
                    return! ctx.WriteJson {| id = objectGuid.ToString "D"; deliveriesUrl = $"/internal/objects/{objectGuid:D}/deliveries" |}
        }

// ---- delivery status -------------------------------------------------------------------------------------

let private iso (time: DateTimeOffset) = time.UtcDateTime.ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"

let private deliveries (objectId: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            let dataSource = ctx.GetService<NpgsqlDataSource>()

            match parseGuid objectId with
            | None -> return! error ctx 404 "not_found" "No such object"
            | Some objectGuid ->
                use exists = dataSource.CreateCommand "SELECT 1 FROM objects WHERE id = @id"
                exists.Parameters.AddWithValue("id", objectGuid) |> ignore

                match! exists.ExecuteScalarAsync() with
                | null -> return! error ctx 404 "not_found" "No such object"
                | _ ->
                    use command =
                        dataSource.CreateCommand
                            """SELECT a.id, a.activity_type, d.host, d.inbox_url, d.status, d.attempts, d.last_error, d.next_attempt_at
                               FROM activities a LEFT JOIN deliveries d ON d.activity = a.id
                               WHERE a.object = @id ORDER BY a.created_at, a.id, d.host, d.inbox_url"""

                    command.Parameters.AddWithValue("id", objectGuid) |> ignore
                    use! reader = command.ExecuteReaderAsync()
                    let activities = JsonArray()
                    let mutable current: Guid option = None
                    let mutable currentList: JsonArray = null

                    while reader.Read() do
                        let activityGuid = reader.GetGuid 0

                        if current <> Some activityGuid then
                            let node = JsonObject()
                            node["activityId"] <- JsonValue.Create(Ids.activityId options.Domain activityGuid)
                            node["type"] <- JsonValue.Create(reader.GetString 1)
                            currentList <- JsonArray()
                            node["deliveries"] <- currentList
                            activities.Add node
                            current <- Some activityGuid

                        if not (reader.IsDBNull 2) then
                            let status = reader.GetString 4
                            let node = JsonObject()
                            node["host"] <- JsonValue.Create(reader.GetString 2)
                            node["inbox"] <- JsonValue.Create(reader.GetString 3)
                            node["status"] <- JsonValue.Create status
                            node["attempts"] <- JsonValue.Create(reader.GetInt32 5)
                            node["lastError"] <- (if reader.IsDBNull 6 then null else JsonValue.Create(reader.GetString 6))

                            node["nextAttemptAt"] <-
                                (if status = "pending" || status = "sending" then JsonValue.Create(iso (reader.GetFieldValue<DateTimeOffset> 7)) else null)

                            currentList.Add node

                    let doc = JsonObject()
                    doc["objectId"] <- JsonValue.Create(objectGuid.ToString "D")
                    doc["activities"] <- activities
                    ctx.SetStatusCode 200
                    ctx.SetContentType "application/json"
                    return! ctx.WriteBytes(ActivityStreams.toBytes doc)
        }

let endpoints: Endpoint list =
    [ POST [ routef "/internal/actors/{%s}/objects" publish ]
      PUT [ routef "/internal/objects/{%s}" update ]
      DELETE [ routef "/internal/objects/{%s}" delete ]
      GET [ routef "/internal/objects/{%s}/deliveries" deliveries ] ]
