/// One Postgres container for the whole test assembly; every test gets its own database
module Megafono.Tests.Fixtures.PostgresFixture

open System
open System.Threading.Tasks
open Npgsql
open Testcontainers.PostgreSql
open Xunit

type PostgresFixture() =
    let container = PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=1000").Build()

    interface IAsyncLifetime with
        member _.InitializeAsync() = ValueTask(container.StartAsync())
        member _.DisposeAsync() = container.DisposeAsync()

    /// A new empty database; returns its connection string. Databases are not dropped: the container is.
    member _.CreateDatabase() : Task<string> =
        task {
            let name = $"mf_{Guid.NewGuid():N}"
            use admin = new NpgsqlConnection(container.GetConnectionString())
            do! admin.OpenAsync()
            use command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)
            let! _ = command.ExecuteNonQueryAsync()

            let builder = NpgsqlConnectionStringBuilder(container.GetConnectionString())
            builder.Database <- name
            // Tests do not always dispose their data sources: keep idle connections short-lived and few
            builder.MaxPoolSize <- 8
            builder.ConnectionIdleLifetime <- 2
            builder.ConnectionPruningInterval <- 1
            return builder.ConnectionString
        }

    /// Drops a database while connections are open (to make `/health` fail)
    member _.DropDatabase(connectionString: string) : Task =
        task {
            let name = NpgsqlConnectionStringBuilder(connectionString).Database
            use admin = new NpgsqlConnection(container.GetConnectionString())
            do! admin.OpenAsync()
            use command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin)
            let! _ = command.ExecuteNonQueryAsync()
            ()
        }

[<assembly: AssemblyFixture(typeof<PostgresFixture>)>]
do ()

/// Small SQL helpers for assertions
module Db =
    let scalar (dataSource: NpgsqlDataSource) (sql: string) : Task<obj> =
        task {
            use command = dataSource.CreateCommand sql
            return! command.ExecuteScalarAsync()
        }

    let execute (dataSource: NpgsqlDataSource) (sql: string) : Task =
        task {
            use command = dataSource.CreateCommand sql
            let! _ = command.ExecuteNonQueryAsync()
            ()
        }

    let count (dataSource: NpgsqlDataSource) (sql: string) : Task<int64> =
        task {
            let! value = scalar dataSource sql
            return value :?> int64
        }
