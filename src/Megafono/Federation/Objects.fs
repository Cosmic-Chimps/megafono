/// `GET /ap/objects/{id}` and `GET /ap/activities/{id}` with the fetch rules of contracts/federation.md
/// (FR-045, constitution Principle II): public and unlisted objects go to anyone; a followers-only one only to a
/// validly signed request from an accepted follower of the author, right now; everything else is 404, never 403.
module Megafono.Federation.Objects

open System
open System.Security.Cryptography
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.AspNetCore.Http.Features
open Npgsql
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Federation.Http

/// The hook for the inbound rate limiter. `Allow ctx host` is asked twice per signed fetch: with `None` before any
/// work is done (per source address) and with the verified key owner's host afterwards. False answers 429. The
/// default allows everything; the rate limiter registers its own instance.
type FetchRateLimit =
    { Allow: HttpContext -> string option -> Task<bool> }

let allowAll: FetchRateLimit = { Allow = fun _ _ -> Task.FromResult true }

let private tooManyRequests (ctx: HttpContext) : Task =
    ctx.SetStatusCode 429
    ctx.Response.Headers["Retry-After"] <- "60"
    Task.CompletedTask

// ---- who may see a followers-only object -----------------------------------------------------------------

/// The request as the signature covers it: raw target (path and query as received), method and headers
let private signedRequest (ctx: HttpContext) : SignedRequest =
    { Method = ctx.Request.Method
      PathAndQuery = ctx.Features.Get<IHttpRequestFeature>().RawTarget
      Headers = [ for h in ctx.Request.Headers -> h.Key, h.Value.ToString() ]
      Body = [||] }

let private verifyWith (time: TimeProvider) (pem: string) (request: SignedRequest) : Result<unit, VerifyError> =
    use key = RSA.Create()

    try
        key.ImportFromPem pem
        draftCavage.Verify time key request
    with :? CryptographicException ->
        Error BadSignature

/// The remote actor whose key signed this GET, or None when there is no valid signature. A bad signature gets
/// one key refetch (rotation, FR-012) before it is refused.
let private signer (ctx: HttpContext) : Task<RemoteActors.RemoteActor option> =
    task {
        let request = signedRequest ctx
        let env = ctx.GetService<RemoteActors.Env>()
        let time = ctx.GetService<TimeProvider>()

        match draftCavage.ExtractKeyId request with
        | Error _ -> return None
        | Ok keyId ->
            match! RemoteActors.findKey env keyId with
            | Error _ -> return None
            | Ok actor ->
                match verifyWith time actor.PublicKeyPem request with
                | Ok() -> return Some actor
                | Error BadSignature ->
                    match! RemoteActors.refetchKey env keyId with
                    | Ok refreshed when verifyWith time refreshed.PublicKeyPem request = Ok() -> return Some refreshed
                    | _ -> return None
                | Error _ -> return None
    }

let private isAcceptedFollower (dataSource: NpgsqlDataSource) (author: string) (remoteActor: string) : Task<bool> =
    task {
        use command =
            dataSource.CreateCommand
                "SELECT 1 FROM follows WHERE direction = 'inbound' AND local_actor = @author AND remote_actor = @remote AND status = 'accepted'"

        command.Parameters.AddWithValue("author", author) |> ignore
        command.Parameters.AddWithValue("remote", remoteActor) |> ignore
        let! found = command.ExecuteScalarAsync()
        return not (isNull found)
    }

type private Verdict =
    | Allowed
    | NotAllowed
    | RateLimited

/// A followers-only fetch is allowed only for a signed request from an accepted follower of `author`
let private authorize (ctx: HttpContext) (author: string) : Task<Verdict> =
    task {
        let limit = ctx.GetService<FetchRateLimit>()

        if not (ctx.Request.Headers.ContainsKey "Signature") then
            return NotAllowed
        else
            match! limit.Allow ctx None with
            | false -> return RateLimited
            | true ->
                match! signer ctx with
                | None -> return NotAllowed
                | Some actor ->
                    match! limit.Allow ctx (Some actor.Host) with
                    | false -> return RateLimited
                    | true ->
                        let! follower = isAcceptedFollower (ctx.GetService<NpgsqlDataSource>()) author actor.Id
                        return if follower then Allowed else NotAllowed
    }

// ---- storage ---------------------------------------------------------------------------------------------

type private Row =
    { Author: string
      Audience: Audience.Audience
      ObjectType: string
      Deleted: DateTimeOffset option
      /// The object document, or the stored activity bytes
      Payload: byte[]
      ObjectId: Guid }

let private objectRow (dataSource: NpgsqlDataSource) (id: Guid) : Task<Row option> =
    task {
        use command =
            dataSource.CreateCommand
                "SELECT local_actor, audience, object_type, deleted_at, document::text, id FROM objects WHERE id = @id"

        command.Parameters.AddWithValue("id", id) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { Author = reader.GetString 0
                      Audience = (Audience.parse (reader.GetString 1)).Value
                      ObjectType = reader.GetString 2
                      Deleted = if reader.IsDBNull 3 then None else Some(reader.GetFieldValue<DateTimeOffset> 3)
                      Payload = Text.Encoding.UTF8.GetBytes(reader.GetString 4)
                      ObjectId = reader.GetGuid 5 }
        else
            return None
    }

/// Only Create and Update activities are fetchable; they take their audience and state from their object
let private activityRow (dataSource: NpgsqlDataSource) (id: Guid) : Task<Row option> =
    task {
        use command =
            dataSource.CreateCommand
                """SELECT a.local_actor, o.audience, o.object_type, o.deleted_at, a.body, o.id
                   FROM activities a JOIN objects o ON o.id = a.object
                   WHERE a.id = @id AND a.activity_type IN ('Create', 'Update')"""

        command.Parameters.AddWithValue("id", id) |> ignore
        use! reader = command.ExecuteReaderAsync()

        if reader.Read() then
            return
                Some
                    { Author = reader.GetString 0
                      Audience = (Audience.parse (reader.GetString 1)).Value
                      ObjectType = reader.GetString 2
                      Deleted = if reader.IsDBNull 3 then None else Some(reader.GetFieldValue<DateTimeOffset> 3)
                      Payload = reader.GetFieldValue<byte[]> 4
                      ObjectId = reader.GetGuid 5 }
        else
            return None
    }

let private serve (kind: string) (load: NpgsqlDataSource -> Guid -> Task<Row option>) (idText: string) : EndpointHandler =
    fun ctx ->
        task {
            match Guid.TryParseExact(idText, "D") with
            | true, id when isCanonical ctx $"/ap/{kind}/{id:D}" ->
                let options = ctx.GetService<Config.MegafonoOptions>()

                match! load (ctx.GetService<NpgsqlDataSource>()) id with
                | None -> return! notFound ctx
                | Some row when row.Deleted.IsSome ->
                    let tombstone = ActivityStreams.tombstone options.Domain row.ObjectId row.ObjectType row.Deleted.Value
                    return! respond ctx 410 activityJson tombstone
                | Some row ->
                    let! verdict =
                        if Audience.isPrivate row.Audience then authorize ctx row.Author else Task.FromResult Allowed

                    match verdict with
                    | RateLimited -> return! tooManyRequests ctx
                    | NotAllowed -> return! notFound ctx
                    | Allowed ->
                        ctx.SetStatusCode 200
                        ctx.SetContentType activityJson
                        // A private answer must not sit in a shared cache where an unauthorized request could read it
                        if Audience.isPrivate row.Audience then ctx.Response.Headers.CacheControl <- "private, no-store"
                        return! ctx.WriteBytes row.Payload
            | _ -> return! notFound ctx
        }

let endpoints: Endpoint list =
    [ GET
          [ routef "/ap/objects/{%s}" (serve "objects" objectRow)
            routef "/ap/activities/{%s}" (serve "activities" activityRow) ] ]
