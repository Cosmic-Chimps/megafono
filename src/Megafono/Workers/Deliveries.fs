/// Sends stored activities to remote inboxes (FR-050, FR-051). The bytes sent are the bytes stored when the
/// activity was created, so every retry is identical and receivers can deduplicate by activity id.
module Megafono.Workers.Deliveries

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Workers.Dispatcher

/// Test-only: see `Dispatcher.create`. Register an instance in the test host's services to use it.
type DeliveryFaults =
    { BeforeCompletionWrite: (string -> Task) option }

/// Decrypted actor keys by app user id. One instance is shared by the delivery worker and housekeeping, which
/// evicts the keys it clears (specs/003-actor-deletion R-32).
type KeyCache() =
    let keys = ConcurrentDictionary<string, RSA>()

    member _.TryGet(appUserId: string) : RSA option =
        match keys.TryGetValue appUserId with
        | true, key -> Some key
        | _ -> None

    member _.GetOrAdd(appUserId: string, key: RSA) : RSA = keys.GetOrAdd(appUserId, key)

    /// Drops the keys of these actors and disposes them
    member _.Evict(appUserIds: string seq) : unit =
        for id in appUserIds do
            match keys.TryRemove id with
            | true, key -> key.Dispose()
            | _ -> ()

type Env =
    { DataSource: NpgsqlDataSource
      Http: SafeHttp.SafeHttpClient
      Keys: KeyVault.MasterKeys
      Domain: string
      Time: TimeProvider
      /// Decrypted author keys, by app user id
      KeyCache: KeyCache }

let private loadKey (env: Env) (appUserId: string) : Task<RSA> =
    task {
        match env.KeyCache.TryGet appUserId with
        | Some key -> return key
        | None ->
            use command = env.DataSource.CreateCommand "SELECT enc_private_key FROM local_actors WHERE app_user_id = @id"
            command.Parameters.AddWithValue("id", appUserId) |> ignore
            let! blob = command.ExecuteScalarAsync()
            let key = KeyVault.loadPrivateKey env.Keys (Ids.actorId env.Domain appUserId) (blob :?> byte[])
            return env.KeyCache.GetOrAdd(appUserId, key)
    }

/// A host that failed every delivery for this long is unreachable (FR-052)
let private unreachableAfter = TimeSpan.FromDays 7.0
/// While unreachable, one delivery per this interval is still tried, so the host can be found again
let private probeEvery = TimeSpan.FromHours 1.0

let private hostCommand (env: Env) (sql: string) (host: string) =
    let command = env.DataSource.CreateCommand sql
    command.Parameters.AddWithValue("host", host) |> ignore
    command.Parameters.AddWithValue("now", env.Time.GetUtcNow().UtcDateTime) |> ignore
    command

/// Some(reason) when the host is unreachable and this delivery is not the hourly probe
let private skipReason (env: Env) (host: string) : Task<string option> =
    task {
        use state = hostCommand env "SELECT unreachable_since FROM remote_hosts WHERE host = @host" host

        match! state.ExecuteScalarAsync() with
        | :? DateTime as since ->
            use probe =
                hostCommand
                    env
                    """UPDATE remote_hosts SET last_probe_at = @now
                       WHERE host = @host AND unreachable_since IS NOT NULL
                         AND (last_probe_at IS NULL OR last_probe_at <= @now - interval '1 hour') RETURNING host"""
                    host

            match! probe.ExecuteScalarAsync() with
            | null -> return Some $"{host} has been unreachable since {since:u}"
            | _ -> return None
        | _ -> return None
    }

/// One answer from the host: success clears everything; a failure starts the clock and, 7 days into an unbroken
/// run of failures, marks the host unreachable
let private recordHostResult (env: Env) (host: string) (succeeded: bool) : Task =
    task {
        let sql =
            if succeeded then
                """INSERT INTO remote_hosts (host, last_success_at) VALUES (@host, @now)
                   ON CONFLICT (host) DO UPDATE SET last_success_at = @now, first_failure_at = NULL, unreachable_since = NULL, last_probe_at = NULL"""
            else
                """INSERT INTO remote_hosts (host, first_failure_at) VALUES (@host, @now)
                   ON CONFLICT (host) DO UPDATE SET
                     first_failure_at = COALESCE(remote_hosts.first_failure_at, @now),
                     unreachable_since = CASE WHEN remote_hosts.unreachable_since IS NULL
                          AND COALESCE(remote_hosts.first_failure_at, @now) <= @now - @limit THEN @now ELSE remote_hosts.unreachable_since END,
                     last_probe_at = CASE WHEN remote_hosts.unreachable_since IS NULL
                          AND COALESCE(remote_hosts.first_failure_at, @now) <= @now - @limit THEN @now ELSE remote_hosts.last_probe_at END"""

        use command = hostCommand env sql host
        if not succeeded then command.Parameters.AddWithValue("limit", unreachableAfter) |> ignore
        let! _ = command.ExecuteNonQueryAsync()
        ()
    }

let private process' (env: Env) (job: Job) : Task<Outcome> =
    task {
        use command =
            env.DataSource.CreateCommand
                """SELECT d.inbox_url, d.created_at, a.body, a.local_actor, d.host
                   FROM deliveries d JOIN activities a ON a.id = d.activity WHERE d.id = @id::uuid"""

        command.Parameters.AddWithValue("id", job.Id) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if not (reader.Read()) then
            return GaveUp "the delivery row disappeared"
        else
            let inbox = reader.GetString 0
            let createdAt = reader.GetFieldValue<DateTimeOffset> 1
            let body = reader.GetFieldValue<byte[]> 2
            let author = reader.GetString 3
            let host = reader.GetString 4
            do! reader.CloseAsync()

            // Past the 48 h deadline a failure ends the delivery instead of scheduling another attempt
            let failure (message: string) =
                if Backoff.isPastDeadline createdAt (env.Time.GetUtcNow()) then GaveUp message else Failed message

            // An instance block that came after the enqueue (R-45): nothing is sent to a blocked server
            let! blocked = Blocks.instanceBlocksDelivery env.DataSource inbox host

            match! (if blocked then Task.FromResult(Some "blocked") else skipReason env host) with
            | Some reason -> return Skipped reason
            | None ->
                let! key = loadKey env author
                let! result = SafeHttp.signedPost env.Http (Ids.actorKeyId env.Domain author) key (Uri inbox) body

                match result with
                | Ok response when response.Status >= 200 && response.Status <= 299 ->
                    do! recordHostResult env host true
                    return Succeeded
                | Ok response ->
                    do! recordHostResult env host false
                    return failure $"HTTP {response.Status}"
                | Error error ->
                    do! recordHostResult env host false
                    return failure (SafeHttp.describeError error)
    }

/// Tell the app a delivery ended without being delivered (FR-051, FR-052). Runs in the transaction that writes
/// the `failed` or `skipped` status, so the event exists exactly when the status does.
let private reportFailure (env: Env) (job: Job) (status: string) (error: string) (tx: NpgsqlTransaction) : Task =
    task {
        use command =
            new NpgsqlCommand(
                """SELECT a.local_actor, a.object, d.host, d.attempts
                   FROM deliveries d JOIN activities a ON a.id = d.activity WHERE d.id = @id::uuid""",
                tx.Connection,
                tx
            )

        command.Parameters.AddWithValue("id", job.Id) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            let user = reader.GetString 0
            let objectId = if reader.IsDBNull 1 then None else Some(reader.GetGuid 1)
            let delivery = JsonObject()
            delivery["host"] <- JsonValue.Create(reader.GetString 2)
            delivery["attempts"] <- JsonValue.Create(reader.GetInt32 3)
            delivery["lastError"] <- JsonValue.Create error
            do! reader.CloseAsync()

            let objectField =
                objectId
                |> Option.map (fun id ->
                    let node = JsonObject()
                    node["id"] <- JsonValue.Create(Ids.objectId env.Domain id)
                    "object", node :> JsonNode)
                |> Option.toList

            let body: Events.EventBody =
                { Type = Events.DeliveryFailed
                  Actor = None
                  Follow = None
                  Extra = objectField @ [ "delivery", delivery :> JsonNode ] }

            let! _ = Outbox.enqueueEvent tx env.Time user body
            ()
    }

/// Per-host limit 2 and global limit 32 by default (FR-053)
let spec (env: Env) (globalLimit: int) (perHost: int) : QueueSpec =
    { Name = "deliveries"
      Table = "deliveries"
      IdType = "uuid"
      Channel = Outbox.deliveriesChannel
      GroupColumn = Some "host"
      TerminalStatus = Some "failed"
      SkippedStatus = Some "skipped"
      OnTerminal = Some(reportFailure env)
      GlobalLimit = globalLimit
      GroupLimit = perHost
      Process = process' env }
