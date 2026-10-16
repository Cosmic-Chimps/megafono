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

/// What an actor id stands for
type ActorState =
    | Live of LocalActor
    /// Deleted: the former type and the deletion time, for the 410 Tombstone
    | Gone of actorType: string * deletedAt: System.DateTimeOffset
    | Missing

/// The local actor behind an id: fresh from the app, or the stored profile while the app is down (under 24 h);
/// a deleted actor is answered from its stored tombstone, never by asking the app (FR-006 of 003)
let find (ctx: HttpContext) (appUserId: string) : Task<ActorState> =
    task {
        if not (Ids.isValidAppUserId appUserId) then
            return Missing
        else
            match! ctx.GetService<AppClient>().ById appUserId with
            | Found actor
            | UnavailableWithFallback actor -> return Live actor
            | AppClient.Deleted ->
                use command =
                    ctx.GetService<NpgsqlDataSource>().CreateCommand "SELECT actor_type, deleted_at FROM local_actors WHERE app_user_id = @id AND deleted_at IS NOT NULL"

                command.Parameters.AddWithValue("id", appUserId) |> ignore
                use! reader = command.ExecuteReaderAsync()

                if reader.Read() then
                    return Gone(reader.GetString 0, reader.GetFieldValue<System.DateTimeOffset> 1)
                else
                    return Missing
            | NotFound
            | UnavailableNoFallback -> return Missing
    }

/// 410 with the actor Tombstone
let private gone (ctx: HttpContext) (appUserId: string) (actorType: string) (deletedAt: System.DateTimeOffset) : Task =
    let options = ctx.GetService<Config.MegafonoOptions>()
    respond ctx 410 activityJson (ActivityStreams.actorTombstone options.Domain appUserId actorType deletedAt)

/// True when the request is not for the canonical URL, or claims a key the instance or this user blocks (R-44): both
/// answer 404, before anything else is done
let private hidden (ctx: HttpContext) (appUserId: string) (canonicalPath: string) : Task<bool> =
    task {
        if not (isCanonical ctx canonicalPath) then
            return true
        else
            return! claimsBlockedKey ctx (Some appUserId)
    }

let private actor (appUserId: string) : EndpointHandler =
    fun ctx ->
        task {
            let! hide = hidden ctx appUserId $"/ap/actors/{appUserId}"

            if hide then
                return! notFound ctx
            else
                match! find ctx appUserId with
                | Missing -> return! notFound ctx
                | Gone(kind, deletedAt) -> return! gone ctx appUserId kind deletedAt
                | Live local ->
                    let options = ctx.GetService<Config.MegafonoOptions>()
                    return! respond ctx 200 activityJson (ActivityStreams.actor options.Domain (profileOf local))
        }

let private instanceActor: EndpointHandler =
    fun ctx ->
        task {
            // A key the instance blocks gets the 404 of a missing document (R-44)
            let! refused = claimsBlockedKey ctx None

            if refused || not (isCanonical ctx "/ap/actor") then
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
            let! hide = hidden ctx appUserId $"/ap/actors/{appUserId}/{name}"

            if hide then
                return! notFound ctx
            else
                match! find ctx appUserId with
                | Missing -> return! notFound ctx
                | Gone(kind, deletedAt) -> return! gone ctx appUserId kind deletedAt
                | Live local ->
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
