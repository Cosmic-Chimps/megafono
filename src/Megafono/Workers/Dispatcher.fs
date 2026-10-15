/// One claim loop for every queue table (research R-14): rows are claimed with `FOR UPDATE SKIP LOCKED` and a
/// 60 s lease, run under a global limit and an optional per-group limit (the remote host), and finished with a
/// write guarded by the lease token. Wake-ups come from `LISTEN`/`NOTIFY` plus a 5 s poll; shutdown drains.
module Megafono.Workers.Dispatcher

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Npgsql
open Megafono.Domain

/// A claimed row. The queue's `Process` loads whatever else it needs by `Id`.
type Job =
    { Id: string
      Token: Guid
      /// Attempts including this one
      Attempts: int
      Group: string option }

type Outcome =
    | Succeeded
    /// Try again later, with backoff
    | Failed of error: string
    /// Stop trying: the row ends in the queue's terminal status (queues without one retry instead)
    | GaveUp of error: string
    /// Do not try at all: the row ends in the queue's skipped status (queues without one retry instead)
    | Skipped of reason: string

type QueueSpec =
    { Name: string
      Table: string
      /// SQL type of the `id` column: `uuid` or `text`
      IdType: string
      /// NOTIFY channel that wakes the loop
      Channel: string
      /// Column whose value is limited per value (the remote host); None for no per-group limit
      GroupColumn: string option
      /// Status a `GaveUp` ends in
      TerminalStatus: string option
      /// Status a `Skipped` ends in
      SkippedStatus: string option
      /// Runs in the same transaction as the write of a terminal or skipped status, with the job, the status and
      /// the error; only when this write won (the lease was still ours)
      OnTerminal: (Job -> string -> string -> NpgsqlTransaction -> Task) option
      GlobalLimit: int
      GroupLimit: int
      Process: Job -> Task<Outcome> }

type Settings =
    { LeaseFor: TimeSpan
      /// How often the loop looks for due rows without a NOTIFY; real time, not the injected clock
      PollEvery: TimeSpan
      /// Jitter source for the backoff, in [0, 1)
      Rand: unit -> float }

let defaultSettings =
    { LeaseFor = TimeSpan.FromSeconds 60.0
      PollEvery = TimeSpan.FromSeconds 5.0
      Rand = Random.Shared.NextDouble }

type Dispatcher =
    { Service: IHostedService
      /// Look for due rows now (tests move the clock, then call this)
      Wake: unit -> unit
      InFlight: unit -> int }

let private maxClaim = 64

let private dueCondition =
    "((status = 'pending' AND next_attempt_at <= @now) OR (status = 'sending' AND lease_until <= @now))"

let private claimSql (spec: QueueSpec) : string =
    match spec.GroupColumn with
    | None ->
        $"""WITH picked AS (
              SELECT id FROM {spec.Table}
              WHERE {dueCondition} AND id::text <> ALL(@inflight)
              ORDER BY next_attempt_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE {spec.Table} t SET status = 'sending', attempts = t.attempts + 1, lease_until = @lease, lease_token = gen_random_uuid()
            FROM picked WHERE t.id = picked.id
            RETURNING t.id::text, t.lease_token, t.attempts, NULL::text"""
    | Some group ->
        // One candidate per group per round (oldest first) so a busy host cannot take every slot; DISTINCT ON cannot
        // be combined with FOR UPDATE, so the candidates are locked in a second step that re-checks they are still due
        $"""WITH candidates AS (
              SELECT DISTINCT ON ({group}) id FROM {spec.Table}
              WHERE {dueCondition} AND {group} <> ALL(@saturated) AND id::text <> ALL(@inflight)
              ORDER BY {group}, next_attempt_at),
            picked AS (
              SELECT id FROM {spec.Table}
              WHERE id IN (SELECT id FROM candidates) AND {dueCondition}
              ORDER BY next_attempt_at LIMIT @limit FOR UPDATE SKIP LOCKED)
            UPDATE {spec.Table} t SET status = 'sending', attempts = t.attempts + 1, lease_until = @lease, lease_token = gen_random_uuid()
            FROM picked WHERE t.id = picked.id
            RETURNING t.id::text, t.lease_token, t.attempts, t.{group}"""

/// `beforeCompletion` is the test-only fault hook: it runs after `Process` returned and before the result is
/// written. If it throws, the write is skipped, as if the process had died there; the row stays `sending` until
/// its lease expires.
let create
    (spec: QueueSpec)
    (settings: Settings)
    (dataSource: NpgsqlDataSource)
    (time: TimeProvider)
    (log: ILogger)
    (beforeCompletion: (string -> Task) option)
    : Dispatcher =
    let wakeUps = Channel.CreateBounded<unit>(BoundedChannelOptions(1, FullMode = BoundedChannelFullMode.DropWrite))
    let wake () = wakeUps.Writer.TryWrite() |> ignore
    let stop = new CancellationTokenSource()
    let gate = obj ()
    let inFlight = Dictionary<string, string option>()
    let perGroup = Dictionary<string, int>()
    let running = HashSet<Task>()
    let loopTask = ref Task.CompletedTask
    let listenTask = ref Task.CompletedTask

    let snapshot () =
        lock gate (fun () ->
            let saturated = perGroup |> Seq.filter (fun kv -> kv.Value >= spec.GroupLimit) |> Seq.map (fun kv -> kv.Key) |> Array.ofSeq
            inFlight.Count, saturated, Array.ofSeq inFlight.Keys)

    let claim (limit: int) (saturated: string[]) (inFlightIds: string[]) : Task<Job list> =
        task {
            let now = time.GetUtcNow()
            use command = dataSource.CreateCommand(claimSql spec)
            command.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
            command.Parameters.AddWithValue("lease", (now + settings.LeaseFor).UtcDateTime) |> ignore
            command.Parameters.AddWithValue("limit", limit) |> ignore
            command.Parameters.AddWithValue("inflight", inFlightIds) |> ignore
            if spec.GroupColumn.IsSome then command.Parameters.AddWithValue("saturated", saturated) |> ignore
            use! reader = command.ExecuteReaderAsync()
            let jobs = ResizeArray<Job>()

            while reader.Read() do
                jobs.Add
                    { Id = reader.GetString 0
                      Token = reader.GetGuid 1
                      Attempts = reader.GetInt32 2
                      Group = if reader.IsDBNull 3 then None else Some(reader.GetString 3) }

            return List.ofSeq jobs
        }

    /// Terminal and skipped statuses are written in a transaction, so `OnTerminal` commits with them
    let completeTerminal (job: Job) (status: string) (error: string) : Task =
        task {
            use! connection = dataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            use command =
                new NpgsqlCommand(
                    $"UPDATE {spec.Table} SET status = @status, last_error = @error, lease_until = NULL, lease_token = NULL WHERE id = @id::{spec.IdType} AND lease_token = @token",
                    connection,
                    tx
                )

            command.Parameters.AddWithValue("id", job.Id) |> ignore
            command.Parameters.AddWithValue("token", job.Token) |> ignore
            command.Parameters.AddWithValue("status", status) |> ignore
            command.Parameters.AddWithValue("error", error) |> ignore
            let! rows = command.ExecuteNonQueryAsync()

            match spec.OnTerminal with
            | Some callback when rows = 1 -> do! callback job status error tx
            | _ -> ()

            do! tx.CommitAsync()
        }

    let completeSimple (job: Job) (outcome: Outcome) : Task =
        task {
            let now = time.GetUtcNow()

            let sql, error, status, delay =
                match outcome with
                | Succeeded ->
                    $"UPDATE {spec.Table} SET status = 'delivered', delivered_at = @now, last_error = NULL, lease_until = NULL, lease_token = NULL WHERE id = @id::{spec.IdType} AND lease_token = @token",
                    null,
                    null,
                    TimeSpan.Zero
                | Failed error
                | GaveUp error
                | Skipped error ->
                    $"UPDATE {spec.Table} SET status = 'pending', next_attempt_at = @next, last_error = @error, lease_until = NULL, lease_token = NULL WHERE id = @id::{spec.IdType} AND lease_token = @token",
                    error,
                    null,
                    Backoff.nextDelay job.Attempts settings.Rand

            use command = dataSource.CreateCommand sql
            command.Parameters.AddWithValue("id", job.Id) |> ignore
            command.Parameters.AddWithValue("token", job.Token) |> ignore
            command.Parameters.AddWithValue("now", now.UtcDateTime) |> ignore
            command.Parameters.AddWithValue("next", (now + delay).UtcDateTime) |> ignore
            command.Parameters.AddWithValue("error", (if isNull error then box DBNull.Value else box error)) |> ignore
            command.Parameters.AddWithValue("status", (if isNull status then box DBNull.Value else box status)) |> ignore
            let! _ = command.ExecuteNonQueryAsync()
            ()
        }

    let complete (job: Job) (outcome: Outcome) : Task =
        match outcome with
        | GaveUp error when spec.TerminalStatus.IsSome -> completeTerminal job spec.TerminalStatus.Value error
        | Skipped reason when spec.SkippedStatus.IsSome -> completeTerminal job spec.SkippedStatus.Value reason
        | _ -> completeSimple job outcome

    let runJob (job: Job) : Task =
        task {
            let! outcome =
                task {
                    try
                        return! spec.Process job
                    with e ->
                        log.LogWarning(e, "{Queue} {Id}: processing threw", spec.Name, job.Id)
                        return Failed $"{e.GetType().Name}: {e.Message}"
                }

            let! crashed =
                task {
                    match beforeCompletion with
                    | None -> return false
                    | Some hook ->
                        try
                            do! hook job.Id
                            return false
                        with e ->
                            log.LogWarning("{Queue} {Id}: fault hook fired ({Reason}); the result is not written", spec.Name, job.Id, e.Message)
                            return true
                }

            if not crashed then
                try
                    do! complete job outcome
                with e ->
                    // The row stays `sending` and is reclaimed when its lease expires
                    log.LogError(e, "{Queue} {Id}: could not record the result", spec.Name, job.Id)
        }

    let start (job: Job) =
        lock gate (fun () ->
            inFlight[job.Id] <- job.Group

            job.Group
            |> Option.iter (fun g -> perGroup[g] <- (match perGroup.TryGetValue g with | true, n -> n + 1 | _ -> 1)))

        let t =
            Task.Run(fun () ->
                task {
                    try
                        do! runJob job
                    finally
                        lock gate (fun () ->
                            inFlight.Remove job.Id |> ignore

                            job.Group
                            |> Option.iter (fun g ->
                                match perGroup.TryGetValue g with
                                | true, n when n > 1 -> perGroup[g] <- n - 1
                                | _ -> perGroup.Remove g |> ignore))

                        wake ()
                } :> Task)

        lock gate (fun () -> running.Add t |> ignore)

        t.ContinueWith(fun (_: Task) -> lock gate (fun () -> running.Remove t |> ignore)) |> ignore

    let loop () =
        task {
            while not stop.IsCancellationRequested do
                try
                    let mutable more = true

                    while more && not stop.IsCancellationRequested do
                        let count, saturated, ids = snapshot ()
                        let free = spec.GlobalLimit - count

                        if free <= 0 then
                            more <- false
                        else
                            let! jobs = claim (min free maxClaim) saturated ids
                            jobs |> List.iter start
                            more <- not jobs.IsEmpty
                with e ->
                    log.LogError(e, "{Queue}: claim failed", spec.Name)

                try
                    use waitFor = CancellationTokenSource.CreateLinkedTokenSource stop.Token
                    waitFor.CancelAfter settings.PollEvery
                    let! _ = wakeUps.Reader.WaitToReadAsync waitFor.Token
                    ()
                with :? OperationCanceledException ->
                    ()

                while wakeUps.Reader.TryRead() |> fst do
                    ()
        }

    let listen () =
        task {
            while not stop.IsCancellationRequested do
                try
                    use! connection = dataSource.OpenConnectionAsync stop.Token
                    connection.Notification.Add(fun _ -> wake ())
                    use command = new NpgsqlCommand($"LISTEN {spec.Channel}", connection)
                    let! _ = command.ExecuteNonQueryAsync stop.Token
                    wake () // anything queued before the LISTEN took effect

                    while not stop.IsCancellationRequested do
                        do! connection.WaitAsync stop.Token
                with
                | :? OperationCanceledException -> ()
                | e ->
                    log.LogWarning(e, "{Queue}: LISTEN connection lost, retrying", spec.Name)

                    try
                        do! Task.Delay(TimeSpan.FromSeconds 1.0, stop.Token)
                    with :? OperationCanceledException ->
                        ()
        }

    let service =
        { new IHostedService with
            member _.StartAsync _ =
                loopTask.Value <- Task.Run(fun () -> loop () :> Task)
                listenTask.Value <- Task.Run(fun () -> listen () :> Task)
                Task.CompletedTask

            member _.StopAsync cancellationToken =
                task {
                    // Stop claiming; whatever is running is not cancelled but allowed to finish and be recorded
                    stop.Cancel()
                    wake ()
                    do! loopTask.Value
                    do! listenTask.Value
                    let pending = lock gate (fun () -> Array.ofSeq running)
                    let drained = Task.WhenAll pending

                    let! first = Task.WhenAny(drained, Task.Delay(Timeout.Infinite, cancellationToken))

                    if not (obj.ReferenceEquals(first, drained)) then
                        log.LogWarning("{Queue}: shutdown gave up waiting for {Count} running job(s); their leases will expire", spec.Name, pending.Length)
                }
                :> Task }

    { Service = service
      Wake = wake
      InFlight = fun () -> lock gate (fun () -> inFlight.Count) }
