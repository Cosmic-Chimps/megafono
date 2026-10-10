/// Sends stored activities to remote inboxes (FR-050, FR-051). The bytes sent are the bytes stored when the
/// activity was created, so every retry is identical and receivers can deduplicate by activity id.
module Megafono.Workers.Deliveries

open System
open System.Collections.Concurrent
open System.Security.Cryptography
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Workers.Dispatcher

/// Test-only: see `Dispatcher.create`. Register an instance in the test host's services to use it.
type DeliveryFaults =
    { BeforeCompletionWrite: (string -> Task) option }

type Env =
    { DataSource: NpgsqlDataSource
      Http: SafeHttp.SafeHttpClient
      Keys: KeyVault.MasterKeys
      Domain: string
      Time: TimeProvider
      /// Decrypted author keys, by app user id
      KeyCache: ConcurrentDictionary<string, RSA> }

let private loadKey (env: Env) (appUserId: string) : Task<RSA> =
    task {
        match env.KeyCache.TryGetValue appUserId with
        | true, key -> return key
        | _ ->
            use command = env.DataSource.CreateCommand "SELECT enc_private_key FROM local_actors WHERE app_user_id = @id"
            command.Parameters.AddWithValue("id", appUserId) |> ignore
            let! blob = command.ExecuteScalarAsync()
            let key = KeyVault.loadPrivateKey env.Keys (Ids.actorId env.Domain appUserId) (blob :?> byte[])
            return env.KeyCache.GetOrAdd(appUserId, key)
    }

let private process' (env: Env) (job: Job) : Task<Outcome> =
    task {
        use command =
            env.DataSource.CreateCommand
                """SELECT d.inbox_url, d.created_at, a.body, a.local_actor
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
            do! reader.CloseAsync()

            // Past the 48 h deadline a failure ends the delivery instead of scheduling another attempt
            let failure (message: string) =
                if Backoff.isPastDeadline createdAt (env.Time.GetUtcNow()) then GaveUp message else Failed message

            let! key = loadKey env author
            let! result = SafeHttp.signedPost env.Http (Ids.actorKeyId env.Domain author) key (Uri inbox) body

            match result with
            | Ok response when response.Status >= 200 && response.Status <= 299 -> return Succeeded
            | Ok response -> return failure $"HTTP {response.Status}"
            | Error error -> return failure (SafeHttp.describeError error)
    }

/// Per-host limit 2 and global limit 32 by default (FR-053)
let spec (env: Env) (globalLimit: int) (perHost: int) : QueueSpec =
    { Name = "deliveries"
      Table = "deliveries"
      IdType = "uuid"
      Channel = Outbox.deliveriesChannel
      GroupColumn = Some "host"
      TerminalStatus = Some "failed"
      GlobalLimit = globalLimit
      GroupLimit = perHost
      Process = process' env }
