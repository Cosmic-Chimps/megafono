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

/// What the app names: exactly one remote actor id (an absolute https URL, http only under the test switch, never
/// one of ours) or one domain (a host name, never the instance's own domain or one that covers it). Error: the
/// message of the 400.
let private parseTarget (ctx: HttpContext) (actor: string option) (domain: string option) : Result<Target, string> =
    let options = ctx.GetService<Config.MegafonoOptions>()

    match actor, domain with
    | Some _, Some _
    | None, None -> Error "Give exactly one of actor and domain"
    | Some actor, None ->
        let allowHttp = ctx.GetService<RemoteActors.Env>().AllowPrivateAddresses

        match Uri.TryCreate(actor.Trim(), UriKind.Absolute) with
        | true, uri when uri.Scheme = Uri.UriSchemeHttps || (allowHttp && uri.Scheme = Uri.UriSchemeHttp) ->
            let id = uri.AbsoluteUri

            if (localActorId options.Domain id).IsSome || RemoteActors.authority uri = options.Domain.ToLowerInvariant() then
                Error "This instance's own actors cannot be blocked"
            else
                Ok(ActorTarget id)
        | _ -> Error "actor must be an absolute https URL"
    | None, Some domain ->
        match Blocks.normalizeDomain domain with
        | None -> Error "domain must be a host name, without port, scheme or path"
        | Some name when Blocks.domainMatches name options.Domain -> Error "This instance's own domain cannot be blocked"
        | Some name -> Ok(DomainTarget name)

let private readBody (ctx: HttpContext) : Task<Result<Target, string>> =
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
        | Some o -> return parseTarget ctx (str o "actor") (str o "domain")
    }

let private readQuery (ctx: HttpContext) : Result<Target, string> =
    parseTarget ctx (ctx.TryGetQueryValue "actor") (ctx.TryGetQueryValue "domain")

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

let private blockForUser (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match! Megafono.Internal.Follows.localState ctx appUserId with
            | None -> return! userProblem ctx 404
            | Some false -> return! gone ctx
            | Some true ->
                match! readBody ctx with
                | Error message -> return! invalid ctx message
                | Ok target ->
                    let time = ctx.GetService<TimeProvider>()
                    let domain = ctx.GetService<Config.MegafonoOptions>().Domain

                    // The actor's document is had before the transaction opens (no network call while the row is locked).
                    // Failing to get it only means no Block is sent: the block itself and its local effects stand.
                    let! fetched =
                        task {
                            match target with
                            | ActorTarget actorId ->
                                match! RemoteActors.getActor (ctx.GetService<RemoteActors.Env>()) actorId with
                                | Ok remote -> return Some remote
                                | Error _ -> return None
                            | DomainTarget _ -> return None
                        }

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

let private liftForUser (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            match readQuery ctx with
            | Error message -> return! invalid ctx message
            | Ok target ->
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

let private blockForInstance: EndpointHandler =
    fun ctx ->
        task {
            match! readBody ctx with
            | Error message -> return! invalid ctx message
            | Ok target ->
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

let private liftForInstance: EndpointHandler =
    fun ctx ->
        task {
            match readQuery ctx with
            | Error message -> return! invalid ctx message
            | Ok target ->
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
