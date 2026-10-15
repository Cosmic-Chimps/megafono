/// Helpers shared by the public endpoints. Documents are written as bytes with their own content type,
/// never through `WriteJson` (research R-09).
module Megafono.Federation.Http

open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Oxpecker
open Megafono.Domain

let activityJson = "application/activity+json"
let jrdJson = "application/jrd+json"

let respond (ctx: HttpContext) (status: int) (contentType: string) (document: JsonNode) : Task =
    ctx.SetStatusCode status
    ctx.SetContentType contentType
    ctx.WriteBytes(ActivityStreams.toBytes document)

/// 404 with an empty body: nothing about why, so a private object looks the same as a missing one
let notFound (ctx: HttpContext) : Task =
    ctx.SetStatusCode 404
    Task.CompletedTask

/// Routing is case-insensitive and ignores a trailing slash, but ids inside documents must equal the URL
/// that was requested, byte for byte (research R-02). Anything but the canonical path is a 404.
let isCanonical (ctx: HttpContext) (canonicalPath: string) : bool =
    ctx.Request.Path.Value = canonicalPath
