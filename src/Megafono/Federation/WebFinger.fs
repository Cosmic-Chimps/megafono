/// `GET /.well-known/webfinger?resource=acct:{handle}@{domain}` (research R-03, FR-001)
module Megafono.Federation.WebFinger

open System
open Microsoft.AspNetCore.Http
open Oxpecker
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.AppClient
open Megafono.Federation.Http

/// `acct:user@domain` (the scheme in any case) as (user, domain)
let private parseAcct (resource: string) : (string * string) option =
    let prefix = "acct:"

    if not (resource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) then
        None
    else
        let rest = resource.Substring prefix.Length
        let at = rest.IndexOf '@'

        if at <= 0 || at = rest.Length - 1 || rest.IndexOf('@', at + 1) >= 0 then
            None
        else
            Some(rest.Substring(0, at), rest.Substring(at + 1))

let private handler: EndpointHandler =
    fun ctx ->
        task {
            match ctx.TryGetQueryValue "resource" with
            | None ->
                // RFC 7033: `resource` is required
                ctx.SetStatusCode 400
                return! System.Threading.Tasks.Task.CompletedTask
            | Some resource ->
                let options = ctx.GetService<Config.MegafonoOptions>()
                // Domains are case-insensitive; handles are lowercase once normalized
                let ours =
                    parseAcct resource
                    |> Option.filter (fun (_, domain) -> String.Equals(domain, options.Domain, StringComparison.OrdinalIgnoreCase))

                let isInstanceActor =
                    match ours with
                    | Some(user, _) -> String.Equals(user, options.Domain, StringComparison.OrdinalIgnoreCase)
                    | None -> false

                // Handles never contain a dot, so a domain cannot be a user's handle
                let local = ours |> Option.bind (fun (user, _) -> Ids.normalizeHandle user)

                match local with
                | None when isInstanceActor ->
                    ctx.SetHttpHeader("Access-Control-Allow-Origin", "*")
                    return! respond ctx 200 jrdJson (ActivityStreams.instanceWebFinger options.Domain)
                | None -> return! notFound ctx
                | Some handle ->
                    match! ctx.GetService<AppClient>().ByHandle handle with
                    | Found actor
                    | UnavailableWithFallback actor ->
                        ctx.SetHttpHeader("Access-Control-Allow-Origin", "*")
                        let profile = Actors.profileOf actor
                        return! respond ctx 200 jrdJson (ActivityStreams.webFinger options.Domain profile)
                    | NotFound
                    | Deleted
                    | UnavailableNoFallback -> return! notFound ctx
        }

let endpoints: Endpoint list = [ GET [ route "/.well-known/webfinger" handler ] ]
