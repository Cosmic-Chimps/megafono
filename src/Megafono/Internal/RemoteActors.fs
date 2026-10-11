/// `GET /internal/remote-actors?handle=|url=`: resolve a remote handle or actor URL (FR-033, FR-021)
module Megafono.Internal.RemoteActors

open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors

let error (ctx: HttpContext) (status: int) (code: string) (message: string) : Task =
    ctx.SetStatusCode status
    ctx.WriteJson {| error = code; message = message |}

/// The sanitized view the app gets of a remote actor: `name` is plain text (FR-056), `handle` has its leading `@`
let actorView (actor: RemoteActor) =
    {| id = actor.Id
       handle = actor.Handle |> Option.map (fun h -> "@" + h) |> Option.defaultValue actor.Id
       name = actor.Name |> Option.defaultValue (actor.Handle |> Option.defaultValue actor.Id)
       iconUrl = actor.IconUrl |> Option.toObj
       profileUrl = actor.ProfileUrl |> Option.toObj |}

/// The answer for a failed resolution: 400 for an address we refuse to dial, 404 when it cannot be resolved,
/// 502 when the remote server did not answer
let resolutionError (ctx: HttpContext) (failure: RemoteError) : Task =
    match failure with
    | UnsafeTarget reason -> error ctx 400 "invalid_request" $"That address is refused: {reason}"
    | NotFound
    | Invalid _ -> error ctx 404 "not_found" "The remote actor could not be found"
    | Unreachable _ -> error ctx 502 "remote_unreachable" "The remote server did not answer"

let private lookup: EndpointHandler =
    fun ctx ->
        task {
            let env = ctx.GetService<RemoteActors.Env>()

            let target =
                match ctx.TryGetQueryValue "handle", ctx.TryGetQueryValue "url" with
                | Some handle, None -> Some(RemoteActors.resolveHandle env handle)
                | None, Some url -> Some(RemoteActors.resolveTarget env url)
                | _ -> None

            match target with
            | None -> return! error ctx 400 "invalid_request" "Give exactly one of handle or url"
            | Some resolution ->
                match! resolution with
                | Ok actor ->
                    return!
                        ctx.WriteJson
                            {| id = actor.Id
                               handle = (actorView actor).handle
                               name = (actorView actor).name
                               iconUrl = (actorView actor).iconUrl
                               profileUrl = (actorView actor).profileUrl
                               ``type`` = actor.ActorType |}
                | Error failure -> return! resolutionError ctx failure
        }

let endpoints: Endpoint list = [ GET [ route "/internal/remote-actors" lookup ] ]
