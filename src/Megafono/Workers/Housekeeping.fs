/// Periodic clean-up
module Megafono.Workers.Housekeeping

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql

let private keepProcessedFor = TimeSpan.FromDays 7.0

/// Deletes inbound activity ids older than 7 days (FR-014); returns how many
let pruneProcessedActivities (dataSource: NpgsqlDataSource) (time: TimeProvider) : Task<int> =
    task {
        use command = dataSource.CreateCommand "DELETE FROM processed_activities WHERE received_at < @cutoff"
        command.Parameters.AddWithValue("cutoff", (time.GetUtcNow() - keepProcessedFor).UtcDateTime) |> ignore
        return! command.ExecuteNonQueryAsync()
    }

/// Prunes once at start, then every hour on the injected clock
let hostedService (dataSource: NpgsqlDataSource) (time: TimeProvider) (log: ILogger) : IHostedService =
    let stop = new CancellationTokenSource()
    let worker = ref Task.CompletedTask

    let runOnce () =
        task {
            try
                let! removed = pruneProcessedActivities dataSource time
                if removed > 0 then log.LogInformation("Pruned {Count} processed activities", removed)
            with e ->
                log.LogWarning(e, "Pruning processed activities failed")
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
