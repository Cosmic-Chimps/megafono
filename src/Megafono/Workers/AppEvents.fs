/// Delivers app events to `POST /megafono/webhooks` (FR-055, FR-057): signed, retried forever, never dropped
module Megafono.Workers.AppEvents

open System
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Workers.Dispatcher

type Env =
    { DataSource: NpgsqlDataSource
      /// Plain client with a 10 s timeout: the app is on a private address, which SafeHttp refuses
      Http: HttpClient
      BaseUrl: string
      WebhookSecret: string
      Time: TimeProvider }

/// Events are small and the app is one host: a modest fixed concurrency, no per-group limit
let concurrency = 8

/// `follow.status` is the status at send time (contracts/app-contract.md), so it is read again on every attempt
let private refreshFollowStatus (env: Env) (payload: JsonNode) : Task =
    task {
        match payload["follow"] with
        | :? JsonObject as follow when not (isNull follow["id"]) ->
            use command = env.DataSource.CreateCommand "SELECT status FROM follows WHERE id = @id::uuid"
            command.Parameters.AddWithValue("id", follow["id"].GetValue<string>()) |> ignore

            match! command.ExecuteScalarAsync() with
            | :? string as status -> follow["status"] <- JsonValue.Create status
            | _ -> ()
        | _ -> ()
    }

let private process' (env: Env) (job: Job) : Task<Outcome> =
    task {
        use command = env.DataSource.CreateCommand "SELECT payload::text FROM app_events WHERE id = @id"
        command.Parameters.AddWithValue("id", job.Id) |> ignore

        match! command.ExecuteScalarAsync() with
        | :? string as stored ->
            let payload = JsonNode.Parse stored
            do! refreshFollowStatus env payload
            let body = Encoding.UTF8.GetBytes(payload.ToJsonString())
            let timestamp = env.Time.GetUtcNow().ToUnixTimeSeconds()

            use request = new HttpRequestMessage(HttpMethod.Post, env.BaseUrl.TrimEnd('/') + "/megafono/webhooks")
            request.Headers.TryAddWithoutValidation("Megafono-Signature", Events.signature env.WebhookSecret timestamp body) |> ignore
            let content = new ByteArrayContent(body)
            content.Headers.TryAddWithoutValidation("Content-Type", "application/json") |> ignore
            request.Content <- content

            try
                use! response = env.Http.SendAsync request

                if response.IsSuccessStatusCode then
                    return Succeeded
                else
                    return Failed $"HTTP {int response.StatusCode}"
            with
            | :? HttpRequestException as e -> return Failed e.Message
            | :? TaskCanceledException -> return Failed "no answer within 10 s"
        | _ -> return Failed "the event row disappeared" // never GaveUp: events are not dropped
    }

let spec (env: Env) : QueueSpec =
    { Name = "app_events"
      Table = "app_events"
      IdType = "text"
      Channel = Outbox.eventsChannel
      GroupColumn = None
      TerminalStatus = None
      GlobalLimit = concurrency
      GroupLimit = 0
      Process = process' env }

let createHttpClient () : HttpClient =
    new HttpClient(Timeout = TimeSpan.FromSeconds 10.0)
