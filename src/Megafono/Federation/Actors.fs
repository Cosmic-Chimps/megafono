/// `GET /ap/actors/{id}`, the instance actor and the collection stubs (FR-003 to FR-006, FR-022)
module Megafono.Federation.Actors

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient
open Megafono.Federation.Http

let profileOf (actor: LocalActor) : ActivityStreams.ActorProfile =
    { AppUserId = actor.AppUserId
      Handle = actor.Handle
      ActorType = actor.ActorType
      Locked = actor.Locked
      Name = actor.Name
      Summary = actor.Summary
      IconUrl = actor.IconUrl
      ProfileUrl = actor.ProfileUrl
      PublicKeyPem = actor.PublicKeyPem
      Published = actor.CreatedAt }

/// The local actor behind an id: fresh from the app, or the stored profile while the app is down (under 24 h)
let find (ctx: HttpContext) (appUserId: string) : Task<LocalActor option> =
    task {
        if not (Ids.isValidAppUserId appUserId) then
            return None
        else
            match! ctx.GetService<AppClient>().ById appUserId with
            | Found actor
            | UnavailableWithFallback actor -> return Some actor
            | NotFound
            | UnavailableNoFallback -> return None
    }

let private actor (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            if not (isCanonical ctx $"/ap/actors/{appUserId}") then
                return! notFound ctx
            else
                match! find ctx appUserId with
                | None -> return! notFound ctx
                | Some local ->
                    let options = ctx.GetService<Config.MegafonoOptions>()
                    return! respond ctx 200 activityJson (ActivityStreams.actor options.Domain (profileOf local))
        }

let private instanceActor: EndpointHandler =
    fun ctx ->
        task {
            if not (isCanonical ctx "/ap/actor") then
                return! notFound ctx
            else
                let options = ctx.GetService<Config.MegafonoOptions>()
                let! key = ctx.GetService<InstanceKey.InstanceKeyProvider>().Get()
                return! respond ctx 200 activityJson (ActivityStreams.instanceActor options.Domain key.PublicKeyPem)
        }

let private count (dataSource: NpgsqlDataSource) (sql: string) (appUserId: string) : Task<int64> =
    task {
        use command = dataSource.CreateCommand sql
        command.Parameters.AddWithValue("id", appUserId) |> ignore
        let! value = command.ExecuteScalarAsync()
        return value :?> int64
    }

/// outbox, followers and following: a stub that states its size
let private collection (name: string) (sql: string) (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            if not (isCanonical ctx $"/ap/actors/{appUserId}/{name}") then
                return! notFound ctx
            else
                match! find ctx appUserId with
                | None -> return! notFound ctx
                | Some local ->
                    let options = ctx.GetService<Config.MegafonoOptions>()
                    let! total = count (ctx.GetService<NpgsqlDataSource>()) sql local.AppUserId
                    let id = $"{Ids.actorId options.Domain local.AppUserId}/{name}"
                    return! respond ctx 200 activityJson (ActivityStreams.collectionStub id total)
        }

let private followCount (direction: string) =
    $"SELECT count(*) FROM follows WHERE direction = '{direction}' AND local_actor = @id AND status = 'accepted'"

let endpoints: Endpoint list =
    [ GET
          [ route "/ap/actor" instanceActor
            routef "/ap/actors/{%s}" actor
            routef
                "/ap/actors/{%s}/outbox"
                (collection
                    "outbox"
                    "SELECT count(*) FROM objects WHERE local_actor = @id AND state = 'live' AND audience IN ('public', 'unlisted')")
            routef "/ap/actors/{%s}/followers" (collection "followers" (followCount "inbound"))
            routef "/ap/actors/{%s}/following" (collection "following" (followCount "outbound")) ] ]
