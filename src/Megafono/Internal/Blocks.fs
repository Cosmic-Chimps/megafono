/// Blocks through the internal API (specs/004-blocks, contracts/internal-api.md):
/// - `POST|GET|DELETE /internal/actors/{id}/blocks`: a user's blocks of a remote actor or domain;
/// - `POST|GET|DELETE /internal/blocks`: the instance's.
/// Creating a block ends the follows it covers (with a `follow.removed` event each), cancels the matching waiting
/// deliveries and queues what the remote side is owed (R-40, R-41), all in one transaction; a user block locks the
/// user's row first, like the delete of 003, so no other write of the user races it (R-46).
module Megafono.Internal.Blocks

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open NpgsqlTypes
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let private error (ctx: HttpContext) (status: int) (code: string) (message: string) =
    ctx.SetStatusCode status
    ctx.WriteJson {| error = code; message = message |}

let private gone (ctx: HttpContext) = error ctx 410 "gone" "The actor was deleted"
let private invalid (ctx: HttpContext) (message: string) = error ctx 400 "invalid_request" message

type private Target =
    | ActorTarget of string
    | DomainTarget of string

let private iso (time: DateTime) = time.ToString "yyyy-MM-dd'T'HH:mm:ss'Z'"

let private blockView (actor: string) (domain: string) (createdAt: DateTime) =
    {| actor = actor; domain = domain; createdAt = iso createdAt |}

let private targetView (target: Target) (createdAt: DateTime) =
    match target with
    | ActorTarget actor -> blockView actor null createdAt
    | DomainTarget domain -> blockView null domain createdAt

/// What the app named, before a handle is resolved (R-48)
type private Named =
    | NamedUrl of string
    | NamedHandle of string
    | NamedDomain of string

/// Why naming a target failed: the app's mistake (400) or what resolving a handle met on the way
type private Failure =
    | BadRequest of string
    | Remote of RemoteError

/// A target ready for the transaction, with the actor's document when it was had (the inbox for the `Block`)
type private Resolved = { Target: Target; Fetched: RemoteActor option }

/// An id on this instance's own domain: never blockable
let private isOwn (options: Config.MegafonoOptions) (actorId: string) =
    (localActorId options.Domain actorId).IsSome
    || (match Uri.TryCreate(actorId, UriKind.Absolute) with
        | true, uri -> RemoteActors.authority uri = options.Domain.ToLowerInvariant()
        | _ -> false)

let private ownMessage = "This instance's own actors cannot be blocked"

/// What the app names: exactly one remote actor (an absolute https URL, http only under the test switch, or a handle,
/// never one of ours) or one domain (a host name, never the instance's own domain or one that covers it). Error: the
/// message of the 400.
let private parseNamed (ctx: HttpContext) (actor: string option) (domain: string option) : Result<Named, string> =
    let options = ctx.GetService<Config.MegafonoOptions>()

    match actor, domain with
    | Some _, Some _
    | None, None -> Error "Give exactly one of actor and domain"
    | Some actor, None ->
        let text = actor.Trim()
        let allowHttp = ctx.GetService<RemoteActors.Env>().AllowPrivateAddresses

        if text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
            match Uri.TryCreate(text, UriKind.Absolute) with
            | true, uri when uri.Scheme = Uri.UriSchemeHttps || (allowHttp && uri.Scheme = Uri.UriSchemeHttp) ->
                if isOwn options uri.AbsoluteUri then Error ownMessage else Ok(NamedUrl uri.AbsoluteUri)
            | _ -> Error "actor must be an absolute https URL or a handle (@user@host)"
        else
            match RemoteActors.parseHandle text with
            | Some(_, host) when host = options.Domain.ToLowerInvariant() -> Error ownMessage
            | Some _ -> Ok(NamedHandle text)
            | None -> Error "actor must be an absolute https URL or a handle (@user@host)"
    | None, Some domain ->
        match Blocks.normalizeDomain domain with
        | None -> Error "domain must be a host name, without port, scheme or path"
        | Some name when Blocks.domainMatches name options.Domain -> Error "This instance's own domain cannot be blocked"
        | Some name -> Ok(NamedDomain name)

let private readBody (ctx: HttpContext) : Task<Result<Named, string>> =
    task {
        let! body =
            task {
                try
                    match! JsonNode.ParseAsync ctx.Request.Body with
                    | :? JsonObject as o -> return Some o
                    | _ -> return None
                with _ ->
                    return None
            }

        match body with
        | None -> return Error "The body must be {\"actor\": ...} or {\"domain\": ...}"
        | Some o -> return parseNamed ctx (str o "actor") (str o "domain")
    }

let private readQuery (ctx: HttpContext) : Result<Named, string> =
    parseNamed ctx (ctx.TryGetQueryValue "actor") (ctx.TryGetQueryValue "domain")

/// The id of a cached actor known by this handle (`user@host`, any case), without any request
let private cachedByHandle (ctx: HttpContext) (handle: string) : Task<string option> =
    task {
        match RemoteActors.parseHandle handle with
        | None -> return None
        | Some(user, host) ->
            use command = ctx.GetService<NpgsqlDataSource>().CreateCommand "SELECT id FROM remote_actors WHERE lower(handle) = lower(@handle)"
            command.Parameters.AddWithValue("handle", $"{user}@{host}") |> ignore

            match! command.ExecuteScalarAsync() with
            | :? string as id -> return Some id
            | _ -> return None
    }

/// Before any transaction (no network call while a row is locked): a handle is resolved, an actor URL is used as given
/// but its canonical id replaces it when the actor's document can be had, so a block by handle and a block by URL are
/// the same block (R-48). A URL whose document cannot be had stays as given: blocking works while the server is down,
/// only no `Block` can be sent. When lifting, a handle is looked up in the cache before the remote server is asked.
let private resolve (ctx: HttpContext) (lifting: bool) (named: Named) : Task<Result<Resolved, Failure>> =
    task {
        let options = ctx.GetService<Config.MegafonoOptions>()
        let env = ctx.GetService<RemoteActors.Env>()

        let canonical (remote: RemoteActor) =
            if isOwn options remote.Id then Error(BadRequest ownMessage) else Ok { Target = ActorTarget remote.Id; Fetched = Some remote }

        match named with
        | NamedDomain domain -> return Ok { Target = DomainTarget domain; Fetched = None }
        | NamedUrl url ->
            match! RemoteActors.getActor env url with
            | Ok remote -> return canonical remote
            | Error _ -> return Ok { Target = ActorTarget url; Fetched = None }
        | NamedHandle handle ->
            let! cached = if lifting then cachedByHandle ctx handle else Task.FromResult None

            match cached with
            | Some id -> return Ok { Target = ActorTarget id; Fetched = None }
            | None ->
                match! RemoteActors.resolveTarget env handle with
                | Ok remote -> return canonical remote
                | Error failure -> return Error(Remote failure)
    }

let private failed (ctx: HttpContext) (failure: Failure) : Task =
    match failure with
    | BadRequest message -> invalid ctx message
    | Remote error -> Megafono.Internal.RemoteActors.resolutionError ctx error

// ---- the effects of a block ---------------------------------------------------------------------------------

let private execute (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) : Task<int> =
    task {
        use command = new NpgsqlCommand(sql, tx.Connection, tx)

        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore

        return! command.ExecuteNonQueryAsync()
    }

type private EndedFollow =
    { Id: Guid
      Direction: string
      LocalUser: string
      FollowIri: string
      Remote: RemoteActor }

let private targetValue (target: Target) =
    match target with
    | ActorTarget actor -> actor
    | DomainTarget domain -> domain

/// The pending or accepted follows the block covers: of one user (None: of every user) with the target actor, or with
/// the actors of the target domain, locked for the update
let private findFollows (tx: NpgsqlTransaction) (user: string option) (target: Target) : Task<EndedFollow list> =
    task {
        let match' =
            match target with
            | ActorTarget _ -> "f.remote_actor = @target"
            | DomainTarget _ -> Blocks.hostInDomainSql "@target" "r.host"

        let owner = if user.IsSome then "AND f.local_actor = @user" else ""

        use command =
            new NpgsqlCommand(
                $"""SELECT f.id, f.direction, f.local_actor, f.follow_activity_id, f.remote_actor
                    FROM follows f JOIN remote_actors r ON r.id = f.remote_actor
                    WHERE f.status IN ('pending', 'accepted') AND {match'} {owner}
                    ORDER BY f.local_actor, f.id
                    FOR UPDATE OF f""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("target", targetValue target) |> ignore
        user |> Option.iter (fun u -> command.Parameters.AddWithValue("user", u) |> ignore)
        use! reader = command.ExecuteReaderAsync()
        let rows = ResizeArray<Guid * string * string * string * string>()

        while reader.Read() do
            rows.Add((reader.GetGuid 0, reader.GetString 1, reader.GetString 2, reader.GetString 3, reader.GetString 4))

        do! reader.CloseAsync()
        let ended = ResizeArray<EndedFollow>()

        for id, direction, localUser, followIri, remoteId in rows do
            match! loadRemote tx remoteId with
            | Some remote ->
                ended.Add
                    { Id = id
                      Direction = direction
                      LocalUser = localUser
                      FollowIri = followIri
                      Remote = remote }
            | None -> ()

        return List.ofSeq ended
    }

/// Sets the follows to `removed` and queues a `follow.removed` event per row, to its user (R-41, FR-003)
let private removeFollows (tx: NpgsqlTransaction) (time: TimeProvider) (follows: EndedFollow list) : Task =
    task {
        for f in follows do
            let! _ = execute tx "UPDATE follows SET status = 'removed', updated_at = @now WHERE id = @id" [ "now", box (time.GetUtcNow().UtcDateTime); "id", box f.Id ]

            let body: Events.EventBody =
                { Type = Events.FollowRemoved
                  Actor = Some(actorSummary f.Remote)
                  Follow = Some { Id = f.Id; Direction = f.Direction; Status = "removed" }
                  Extra = [] }

            let! _ = Outbox.enqueueEvent tx time f.LocalUser body
            ()
    }

/// Stores an activity of the block (the exact bytes every retry sends) and queues it for the remote actor's own inbox,
/// past the block filter: the block's own `Block`, `Reject` and `Undo` are what it owes the other side (R-45)
let private queueOwn (tx: NpgsqlTransaction) (time: TimeProvider) (kind: string) (localUser: string) (activityGuid: Guid) (document: JsonNode) (inbox: string) =
    task {
        let! _ =
            execute
                tx
                $"""INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                    VALUES (@id, @actor, '{kind}', 'direct', @body, @now)"""
                [ "id", box activityGuid
                  "actor", box localUser
                  "body", box (ActivityStreams.toBytes document)
                  "now", box (time.GetUtcNow().UtcDateTime) ]

        let! _ = Outbox.enqueueDeliveriesUnfiltered tx time activityGuid [ inbox ]
        ()
    }

/// `Reject` of an ended inbound follow, `Undo{Follow}` of an ended outbound one, to the remote actor's own inbox (R-41).
/// Returns the activity id so the skip of waiting deliveries leaves it alone.
let private queueFollowEnd (tx: NpgsqlTransaction) (time: TimeProvider) (domain: string) (f: EndedFollow) : Task<Guid> =
    task {
        let activityGuid = Guid.NewGuid()
        let activityId = Ids.activityId domain activityGuid
        let local = Ids.actorId domain f.LocalUser

        let document, kind =
            match f.Direction with
            | "inbound" ->
                let followed: ActivityStreams.FollowRef = { Id = f.FollowIri; Actor = f.Remote.Id; Object = local }
                ActivityStreams.reject activityId local followed, "Reject"
            | _ ->
                let followed: ActivityStreams.FollowRef = { Id = f.FollowIri; Actor = local; Object = f.Remote.Id }
                ActivityStreams.undoFollow activityId local followed, "Undo"

        do! queueOwn tx time kind f.LocalUser activityGuid document f.Remote.Inbox
        return activityGuid
    }

/// Cancels the waiting deliveries the target covers (R-45 step 1): the user's own (None: everyone's) to the actor's own
/// inbox or to the domain's hosts, except the activities just queued; a worker that holds a lease writes nothing more
let private skipDeliveries (tx: NpgsqlTransaction) (user: string option) (target: Target) (keep: Guid list) : Task<int> =
    task {
        let covered =
            match target with
            | ActorTarget _ -> "EXISTS (SELECT 1 FROM remote_actors r WHERE r.id = @target AND r.inbox = d.inbox_url)"
            | DomainTarget _ -> Blocks.hostInDomainSql "@target" "d.host"

        let owner = if user.IsSome then "AND a.local_actor = @user" else ""

        use command =
            new NpgsqlCommand(
                $"""UPDATE deliveries d SET status = 'skipped', last_error = 'blocked', lease_until = NULL, lease_token = NULL
                    FROM activities a
                    WHERE a.id = d.activity AND d.status IN ('pending', 'sending') {owner}
                      AND NOT (d.activity = ANY(@keep)) AND {covered}""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("target", targetValue target) |> ignore
        command.Parameters.Add(NpgsqlParameter("keep", NpgsqlDbType.Array ||| NpgsqlDbType.Uuid, Value = Array.ofList keep)) |> ignore
        user |> Option.iter (fun u -> command.Parameters.AddWithValue("user", u) |> ignore)
        return! command.ExecuteNonQueryAsync()
    }

/// Inserts the block row. Some(created) for a new block; None when the same block exists (the caller answers it)
let private insertBlock (tx: NpgsqlTransaction) (time: TimeProvider) (user: string option) (target: Target) : Task<DateTime option> =
    task {
        use command =
            new NpgsqlCommand(
                """INSERT INTO blocks (id, local_actor, actor, domain, created_at) VALUES (gen_random_uuid(), @user, @actor, @domain, @now)
                   ON CONFLICT DO NOTHING RETURNING created_at""",
                tx.Connection,
                tx
            )

        let optional (name: string) (value: string option) =
            command.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Text, Value = (match value with Some v -> box v | None -> box DBNull.Value))) |> ignore

        optional "user" user
        optional "actor" (match target with ActorTarget a -> Some a | _ -> None)
        optional "domain" (match target with DomainTarget d -> Some d | _ -> None)
        command.Parameters.AddWithValue("now", time.GetUtcNow().UtcDateTime) |> ignore

        match! command.ExecuteScalarAsync() with
        | :? DateTime as created -> return Some created
        | _ -> return None
    }

let private existingBlock (tx: NpgsqlTransaction) (user: string option) (target: Target) : Task<DateTime> =
    task {
        use command =
            new NpgsqlCommand(
                """SELECT created_at FROM blocks
                   WHERE local_actor IS NOT DISTINCT FROM @user AND actor IS NOT DISTINCT FROM @actor AND domain IS NOT DISTINCT FROM @domain""",
                tx.Connection,
                tx
            )

        let optional (name: string) (value: string option) =
            command.Parameters.Add(NpgsqlParameter(name, NpgsqlDbType.Text, Value = (match value with Some v -> box v | None -> box DBNull.Value))) |> ignore

        optional "user" user
        optional "actor" (match target with ActorTarget a -> Some a | _ -> None)
        optional "domain" (match target with DomainTarget d -> Some d | _ -> None)
        let! created = command.ExecuteScalarAsync()
        return created :?> DateTime
    }

// ---- user blocks --------------------------------------------------------------------------------------------

/// Locks the user's live row (the serialization of R-46). 404: Megáfono does not know the user; 410: it was deleted.
let private lockUser (tx: NpgsqlTransaction) (appUserId: string) : Task<Result<unit, int>> =
    task {
        use command = new NpgsqlCommand("SELECT deleted_at IS NOT NULL FROM local_actors WHERE app_user_id = @id FOR UPDATE", tx.Connection, tx)
        command.Parameters.AddWithValue("id", appUserId) |> ignore

        match! command.ExecuteScalarAsync() with
        | :? bool as deleted when deleted -> return Error 410
        | :? bool -> return Ok()
        | _ -> return Error 404
    }

let private userProblem (ctx: HttpContext) (status: int) =
    if status = 410 then gone ctx else error ctx 404 "not_found" "The app does not know this user"

/// The transaction of data-model.md "The user block transaction", after the target is resolved
let private createUserBlock (appUserId: string) (resolved: Resolved) (ctx: HttpContext) : Task =
    task {
        let target, fetched = resolved.Target, resolved.Fetched
        let time = ctx.GetService<TimeProvider>()
        let domain = ctx.GetService<Config.MegafonoOptions>().Domain

        use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()

        match! lockUser tx appUserId with
        | Error status ->
            do! tx.RollbackAsync()
            return! userProblem ctx status
        | Ok() ->
            match! insertBlock tx time (Some appUserId) target with
            | None ->
                let! created = existingBlock tx (Some appUserId) target
                do! tx.RollbackAsync()
                return! ctx.WriteJson(targetView target created)
            | Some created ->
                let! follows = findFollows tx (Some appUserId) target
                let kept = ResizeArray<Guid>()

                match target with
                | ActorTarget actorId ->
                    // One Block, to the actor's own inbox; its id is kept so the block can be undone (R-40)
                    let! remote = match fetched with Some r -> Task.FromResult(Some r) | None -> loadRemote tx actorId

                    match remote with
                    | Some remote ->
                        let activityGuid = Guid.NewGuid()
                        let local = Ids.actorId domain appUserId
                        let document = ActivityStreams.block (Ids.activityId domain activityGuid) local actorId
                        do! queueOwn tx time "Block" appUserId activityGuid document remote.Inbox
                        let! _ = execute tx "UPDATE blocks SET block_activity = @activity WHERE local_actor = @user AND actor = @actor" [ "activity", box activityGuid; "user", box appUserId; "actor", box actorId ]
                        kept.Add activityGuid
                    | None -> ()
                | DomainTarget _ ->
                    // No Block for a domain: each ended follow gets its Reject or Undo (R-41)
                    for f in follows do
                        let! activityGuid = queueFollowEnd tx time domain f
                        kept.Add activityGuid

                do! removeFollows tx time follows
                let! _ = skipDeliveries tx (Some appUserId) target (List.ofSeq kept)
                do! tx.CommitAsync()
                ctx.SetStatusCode 201
                return! ctx.WriteJson(targetView target created)
    }

let private blockForUser (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match! Megafono.Internal.Follows.localState ctx appUserId with
            | None -> return! userProblem ctx 404
            | Some false -> return! gone ctx
            | Some true ->
                match! readBody ctx with
                | Error message -> return! invalid ctx message
                | Ok named ->
                    match! resolve ctx false named with
                    | Error failure -> return! failed ctx failure
                    | Ok resolved -> return! createUserBlock appUserId resolved ctx
        }

let private listBlocks (user: string option) (ctx: HttpContext) : Task =
    task {
        use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()

        let! known =
            task {
                match user with
                | None -> return Ok()
                | Some appUserId ->
                    use exists = new NpgsqlCommand("SELECT deleted_at IS NOT NULL FROM local_actors WHERE app_user_id = @id", connection)
                    exists.Parameters.AddWithValue("id", appUserId) |> ignore

                    match! exists.ExecuteScalarAsync() with
                    | :? bool as deleted when deleted -> return Error 410
                    | :? bool -> return Ok()
                    | _ -> return Error 404
            }

        match known with
        | Error status -> return! userProblem ctx status
        | Ok() ->
            use query =
                new NpgsqlCommand(
                    "SELECT actor, domain, created_at FROM blocks WHERE local_actor IS NOT DISTINCT FROM @user ORDER BY created_at DESC, id DESC",
                    connection
                )

            query.Parameters.Add(NpgsqlParameter("user", NpgsqlDbType.Text, Value = (match user with Some u -> box u | None -> box DBNull.Value))) |> ignore
            use! reader = query.ExecuteReaderAsync()
            let items = ResizeArray<obj>()

            while reader.Read() do
                let optional (i: int) : string = if reader.IsDBNull i then null else reader.GetString i
                items.Add(box (blockView (optional 0) (optional 1) (reader.GetFieldValue<DateTime> 2)))

            return! ctx.WriteJson {| items = items |}
    }

/// Deletes the user's block and queues the `Undo` of the `Block` it had sent (data-model.md "Lifting")
let private liftUserBlock (appUserId: string) (target: Target) (ctx: HttpContext) : Task =
    task {
        let time = ctx.GetService<TimeProvider>()
        let domain = ctx.GetService<Config.MegafonoOptions>().Domain
        use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()

        match! lockUser tx appUserId with
        | Error status ->
            do! tx.RollbackAsync()
            return! userProblem ctx status
        | Ok() ->
            use delete =
                new NpgsqlCommand(
                    """DELETE FROM blocks WHERE local_actor = @user AND actor IS NOT DISTINCT FROM @actor AND domain IS NOT DISTINCT FROM @domain
                       RETURNING block_activity""",
                    connection,
                    tx
                )

            delete.Parameters.AddWithValue("user", appUserId) |> ignore
            delete.Parameters.Add(NpgsqlParameter("actor", NpgsqlDbType.Text, Value = (match target with ActorTarget a -> box a | _ -> box DBNull.Value))) |> ignore
            delete.Parameters.Add(NpgsqlParameter("domain", NpgsqlDbType.Text, Value = (match target with DomainTarget d -> box d | _ -> box DBNull.Value))) |> ignore
            use! reader = delete.ExecuteReaderAsync()

            let row =
                if reader.Read() then Some(if reader.IsDBNull 0 then None else Some(reader.GetGuid 0)) else None

            do! reader.CloseAsync()

            match row, target with
            | None, _ ->
                do! tx.RollbackAsync()
                return! error ctx 404 "not_found" "No such block"
            | Some(Some blockActivity), ActorTarget actorId ->
                // Undo of the Block that was sent, embedding it; the block is gone, so the normal filter lets it through
                match! loadRemote tx actorId with
                | Some remote ->
                    let activityGuid = Guid.NewGuid()
                    let local = Ids.actorId domain appUserId

                    let sent: ActivityStreams.BlockRef =
                        { Id = Ids.activityId domain blockActivity
                          Actor = local
                          Object = actorId }

                    let document = ActivityStreams.undoBlock (Ids.activityId domain activityGuid) local sent

                    let! _ =
                        execute
                            tx
                            """INSERT INTO activities (id, local_actor, activity_type, audience, body, created_at)
                               VALUES (@id, @actor, 'Undo', 'direct', @body, @now)"""
                            [ "id", box activityGuid
                              "actor", box appUserId
                              "body", box (ActivityStreams.toBytes document)
                              "now", box (time.GetUtcNow().UtcDateTime) ]

                    let! _ = Outbox.enqueueDeliveries tx time activityGuid [ remote.Inbox ]
                    ()
                | None -> ()

                do! tx.CommitAsync()
                ctx.SetStatusCode 204
            | Some _, _ ->
                do! tx.CommitAsync()
                ctx.SetStatusCode 204
    }

let private liftForUser (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match readQuery ctx with
            | Error message -> return! invalid ctx message
            | Ok named ->
                match! resolve ctx true named with
                | Error failure -> return! failed ctx failure
                | Ok resolved -> return! liftUserBlock appUserId resolved.Target ctx
        }

// ---- instance blocks ----------------------------------------------------------------------------------------

/// The users with a follow the target covers, in a fixed order: locking their rows first (as every other write does)
/// before the follows, so two blocks and the handlers cannot deadlock
let private lockAffectedUsers (tx: NpgsqlTransaction) (target: Target) : Task =
    task {
        let covered =
            match target with
            | ActorTarget _ -> "f.remote_actor = @target"
            | DomainTarget _ -> Blocks.hostInDomainSql "@target" "r.host"

        use command =
            new NpgsqlCommand(
                $"""SELECT app_user_id FROM local_actors
                    WHERE deleted_at IS NULL AND app_user_id IN (
                        SELECT f.local_actor FROM follows f JOIN remote_actors r ON r.id = f.remote_actor
                        WHERE f.status IN ('pending', 'accepted') AND {covered})
                    ORDER BY app_user_id FOR UPDATE""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("target", targetValue target) |> ignore
        use! reader = command.ExecuteReaderAsync()
        while reader.Read() do ()
    }

let private createInstanceBlock (target: Target) (ctx: HttpContext) : Task =
    task {
        let time = ctx.GetService<TimeProvider>()
        use! connection = ctx.GetService<NpgsqlDataSource>().OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()

        match! insertBlock tx time None target with
        | None ->
            let! created = existingBlock tx None target
            do! tx.RollbackAsync()
            return! ctx.WriteJson(targetView target created)
        | Some created ->
            // Nothing is sent to the blocked server (R-41); every follow it covers ends, every waiting delivery is cancelled
            do! lockAffectedUsers tx target
            let! follows = findFollows tx None target
            do! removeFollows tx time follows
            let! _ = skipDeliveries tx None target []
            do! tx.CommitAsync()
            ctx.SetStatusCode 201
            return! ctx.WriteJson(targetView target created)
    }

let private blockForInstance: EndpointHandler =
    fun ctx ->
        task {
            match! readBody ctx with
            | Error message -> return! invalid ctx message
            | Ok named ->
                match! resolve ctx false named with
                | Error failure -> return! failed ctx failure
                | Ok resolved -> return! createInstanceBlock resolved.Target ctx
        }

let private liftInstanceBlock (target: Target) (ctx: HttpContext) : Task =
    task {
        use delete =
            (ctx.GetService<NpgsqlDataSource>()).CreateCommand
                "DELETE FROM blocks WHERE local_actor IS NULL AND actor IS NOT DISTINCT FROM @actor AND domain IS NOT DISTINCT FROM @domain"

        delete.Parameters.Add(NpgsqlParameter("actor", NpgsqlDbType.Text, Value = (match target with ActorTarget a -> box a | _ -> box DBNull.Value))) |> ignore
        delete.Parameters.Add(NpgsqlParameter("domain", NpgsqlDbType.Text, Value = (match target with DomainTarget d -> box d | _ -> box DBNull.Value))) |> ignore
        let! deleted = delete.ExecuteNonQueryAsync()

        if deleted = 0 then
            return! error ctx 404 "not_found" "No such block"
        else
            ctx.SetStatusCode 204
    }

let private liftForInstance: EndpointHandler =
    fun ctx ->
        task {
            match readQuery ctx with
            | Error message -> return! invalid ctx message
            | Ok named ->
                match! resolve ctx true named with
                | Error failure -> return! failed ctx failure
                | Ok resolved -> return! liftInstanceBlock resolved.Target ctx
        }

let endpoints: Endpoint list =
    [ POST
          [ routef "/internal/actors/{%s}/blocks" blockForUser
            route "/internal/blocks" blockForInstance ]
      GET
          [ routef "/internal/actors/{%s}/blocks" (fun (user: string) -> listBlocks (Some user))
            route "/internal/blocks" (listBlocks None) ]
      DELETE
          [ routef "/internal/actors/{%s}/blocks" liftForUser
            route "/internal/blocks" liftForInstance ] ]
