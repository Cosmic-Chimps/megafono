/// `GET /health` (FR-062): 200 when the database answers, 503 when it does not
module Megafono.Infrastructure.Health

open System
open System.Threading
open Npgsql
open Oxpecker

let private databaseReachable (dataSource: NpgsqlDataSource) : System.Threading.Tasks.Task<bool> =
    task {
        use cts = new CancellationTokenSource(TimeSpan.FromSeconds 3.0)

        try
            use! connection = dataSource.OpenConnectionAsync cts.Token
            use command = new NpgsqlCommand("SELECT 1", connection)
            let! _ = command.ExecuteScalarAsync cts.Token
            return true
        with _ ->
            return false
    }

let handler (dataSource: NpgsqlDataSource) : EndpointHandler =
    fun ctx ->
        task {
            let! reachable = databaseReachable dataSource

            if reachable then
                return! ctx.WriteJson {| status = "ok"; database = "ok" |}
            else
                ctx.Response.StatusCode <- 503
                return! ctx.WriteJson {| status = "unavailable"; database = "unreachable" |}
        }
