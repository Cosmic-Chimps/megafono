/// `/.well-known/nodeinfo`, `/nodeinfo/2.0` and `/nodeinfo/2.1` (research R-04, FR-002)
module Megafono.Federation.NodeInfo

open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Federation.Http

let private discovery: EndpointHandler =
    fun ctx ->
        let options = ctx.GetService<Config.MegafonoOptions>()
        respond ctx 200 "application/json" (ActivityStreams.nodeInfoLinks options.Domain)

let private document (version: string) : EndpointHandler =
    fun ctx ->
        task {
            let options = ctx.GetService<Config.MegafonoOptions>()
            use command = ctx.GetService<NpgsqlDataSource>().CreateCommand "SELECT count(*) FROM local_actors WHERE deleted_at IS NULL"
            let! total = command.ExecuteScalarAsync()

            let body = ActivityStreams.nodeInfo version options.Software.Name options.Software.Version (total :?> int64)
            return! respond ctx 200 $"application/json; profile=\"http://nodeinfo.diaspora.software/ns/schema/{version}#\"" body
        }

let endpoints: Endpoint list =
    [ GET
          [ route "/.well-known/nodeinfo" discovery
            route "/nodeinfo/2.0" (document "2.0")
            route "/nodeinfo/2.1" (document "2.1") ] ]
