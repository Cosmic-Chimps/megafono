/// Periodic clean-up
module Megafono.Workers.Housekeeping

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Megafono.Workers

let private keepProcessedFor = TimeSpan.FromDays 7.0

/// Deletes inbound activity ids older than 7 days (FR-014); returns how many
let pruneProcessedActivities (dataSource: NpgsqlDataSource) (time: TimeProvider) : Task<int> =
    task {
        use command = dataSource.CreateCommand "DELETE FROM processed_activities WHERE received_at < @cutoff"
        command.Parameters.AddWithValue("cutoff", (time.GetUtcNow() - keepProcessedFor).UtcDateTime) |> ignore
        return! command.ExecuteNonQueryAsync()
    }

/// Erases the key pair of every deleted actor whose `Delete` deliveries (and every other delivery) have ended:
/// delivered, given up or skipped (specs/003-actor-deletion R-32). Returns the ids cleared; the caller evicts them
/// from the delivery key cache.
let clearDeletedActorKeys (dataSource: NpgsqlDataSource) : Task<string list> =
    task {
        use command =
            dataSource.CreateCommand
                """UPDATE local_actors l SET public_key_pem = NULL, enc_private_key = NULL
                   WHERE l.deleted_at IS NOT NULL AND l.enc_private_key IS NOT NULL
                     AND NOT EXISTS (SELECT 1 FROM deliveries d JOIN activities a ON a.id = d.activity
                                     WHERE a.local_actor = l.app_user_id AND d.status IN ('pending', 'sending'))
                   RETURNING l.app_user_id"""

        use! reader = command.ExecuteReaderAsync()
        let ids = ResizeArray<string>()

        while reader.Read() do
            ids.Add(reader.GetString 0)

        return List.ofSeq ids
    }

/// Prunes and clears keys once at start, then every hour on the injected clock
let hostedService (dataSource: NpgsqlDataSource) (time: TimeProvider) (keyCache: Deliveries.KeyCache) (log: ILogger) : IHostedService =
    let stop = new CancellationTokenSource()
    let worker = ref Task.CompletedTask

    let runOnce () =
        task {
            try
                let! removed = pruneProcessedActivities dataSource time
                if removed > 0 then log.LogInformation("Pruned {Count} processed activities", removed)
            with e ->
                log.LogWarning(e, "Pruning processed activities failed")

            try
                let! cleared = clearDeletedActorKeys dataSource
                keyCache.Evict cleared
                if not cleared.IsEmpty then log.LogInformation("Cleared the keys of {Count} deleted actors", cleared.Length)
            with e ->
                log.LogWarning(e, "Clearing the keys of deleted actors failed")
        }

    let loop () =
        task {
            do! runOnce ()
            use timer = new PeriodicTimer(TimeSpan.FromHours 1.0, time)

            try
                while! timer.WaitForNextTickAsync stop.Token do
                    do! runOnce ()
            with :? OperationCanceledException ->
                ()
        }

    { new IHostedService with
        member _.StartAsync _ =
            worker.Value <- Task.Run(fun () -> loop () :> Task)
            Task.CompletedTask

        member _.StopAsync _ =
            stop.Cancel()
            worker.Value }
