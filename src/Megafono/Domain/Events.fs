/// App events (contracts/app-contract.md): the envelope, its ids and the webhook signature. Pure.
module Megafono.Domain.Events

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes

type EventType =
    | FollowRequested
    | FollowAccepted
    | FollowRejected
    | FollowRemoved
    | ActorUpdated
    | DeliveryFailed
    | ReplyReceived
    | ReplyUpdated
    | ObjectDeleted

let typeName (eventType: EventType) : string =
    match eventType with
    | FollowRequested -> "follow.requested"
    | FollowAccepted -> "follow.accepted"
    | FollowRejected -> "follow.rejected"
    | FollowRemoved -> "follow.removed"
    | ActorUpdated -> "actor.updated"
    | DeliveryFailed -> "delivery.failed"
    | ReplyReceived -> "reply.received"
    | ReplyUpdated -> "reply.updated"
    | ObjectDeleted -> "object.deleted"

/// The remote actor an event is about; `Name` is plain text
type ActorSummary =
    { Id: string
      /// `@user@host`
      Handle: string
      Name: string
      IconUrl: string option
      ProfileUrl: string option }

type FollowInfo =
    { Id: Guid
      /// `inbound` (they follow the user) or `outbound` (the user follows them)
      Direction: string
      Status: string }

type EventBody =
    { Type: EventType
      Actor: ActorSummary option
      Follow: FollowInfo option
      /// Further top-level fields (`object`, `delivery` on delivery.failed)
      Extra: (string * JsonNode) list }

let contractVersion = 1

let private crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

/// 26 characters: 48 bits of milliseconds, then 80 random bits. Sorts by creation time.
let newUlid (now: DateTimeOffset) : string =
    let random = RandomNumberGenerator.GetBytes 10
    let mutable randomBits = UInt128.Zero

    for b in random do
        randomBits <- (randomBits <<< 8) ||| UInt128.op_Implicit (uint64 b)

    let full = (UInt128.op_Implicit (uint64 (now.ToUnixTimeMilliseconds())) <<< 80) ||| randomBits
    let chars = Array.zeroCreate<char> 26
    let mutable rest = full

    for i in 25 .. -1 .. 0 do
        chars[i] <- crockford[int (rest &&& UInt128.op_Implicit 31UL)]
        rest <- rest >>> 5

    String chars

let newEventId (now: DateTimeOffset) : string = "evt_" + newUlid now

let private optionalString (value: string option) : JsonNode =
    match value with
    | Some v -> JsonValue.Create v
    | None -> null

/// The JSON sent to the app. `seq` and `id` come from the queue; follow `status` is refreshed when it is sent.
let envelope (eventId: string) (occurredAt: DateTimeOffset) (seq: int64) (userId: string) (userHandle: string) (body: EventBody) : string =
    let json = JsonObject()
    json["id"] <- JsonValue.Create eventId
    json["version"] <- JsonValue.Create contractVersion
    json["type"] <- JsonValue.Create(typeName body.Type)
    json["occurredAt"] <- JsonValue.Create(occurredAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))
    json["seq"] <- JsonValue.Create seq

    let user = JsonObject()
    user["id"] <- JsonValue.Create userId
    user["handle"] <- JsonValue.Create userHandle
    json["user"] <- user

    match body.Actor with
    | Some actor ->
        let node = JsonObject()
        node["id"] <- JsonValue.Create actor.Id
        node["handle"] <- JsonValue.Create actor.Handle
        node["name"] <- JsonValue.Create actor.Name
        node["iconUrl"] <- optionalString actor.IconUrl
        node["profileUrl"] <- optionalString actor.ProfileUrl
        json["actor"] <- node
    | None -> ()

    match body.Follow with
    | Some follow ->
        let node = JsonObject()
        node["id"] <- JsonValue.Create(follow.Id.ToString "D")
        node["direction"] <- JsonValue.Create follow.Direction
        node["status"] <- JsonValue.Create follow.Status
        json["follow"] <- node
    | None -> ()

    for name, value in body.Extra do
        json[name] <- value

    // A body may bring its own `extensions` (a reply carrying the app's namespace); otherwise the field is null
    if not (json.ContainsKey "extensions") then
        json["extensions"] <- null

    json.ToJsonString()

/// `Megafono-Signature` value: `t=<unix>,v1=<lowercase hex HMAC-SHA256 of "t.body">` with the webhook secret
let signature (secret: string) (timestamp: int64) (body: byte[]) : string =
    let prefix = Encoding.UTF8.GetBytes $"{timestamp}."
    let message = Array.append prefix body
    let mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, message)
    $"t={timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}"

// ---- replies (feature 002, contracts/app-contract.md) --------------------------------------------------

type ReplyAttachment =
    { Type: string
      MediaType: string option
      Url: string
      /// Alt text
      Name: string option }

/// The parent of a reply: one of the app's own objects (`Local`, with Megáfono's object id and the user who
/// published it), or a remote reply already recorded in the thread
type ReplyParent =
    { Local: bool
      ObjectId: Guid option
      UserId: string option }

/// The root the app published
type ThreadRef = { ObjectId: Guid; Id: string }

/// The reply as the app sees it. `Content` is already sanitized HTML, `Summary` plain text.
type ReplyObject =
    { Id: string
      Url: string option
      /// `Note` or `Article`
      Type: string
      Content: string
      Summary: string option
      Sensitive: bool
      Language: string option
      Published: DateTimeOffset
      /// Set on `reply.updated`
      Updated: DateTimeOffset option
      /// `public`, `unlisted`, `followers` or `direct`
      Audience: string
      InReplyTo: string
      Parent: ReplyParent
      Thread: ThreadRef
      Attachments: ReplyAttachment list }

let private iso (time: DateTimeOffset) : JsonNode =
    JsonValue.Create(time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))

let private threadNode (thread: ThreadRef) : JsonNode =
    let node = JsonObject()
    node["objectId"] <- JsonValue.Create(thread.ObjectId.ToString "D")
    node["id"] <- JsonValue.Create thread.Id
    node

/// `reply.received` and `reply.updated`; `extensions` (the app's namespace, data under its prefix) is null when absent
let replyBody (eventType: EventType) (actor: ActorSummary) (reply: ReplyObject) (extensions: JsonNode option) : EventBody =
    let o = JsonObject()
    o["id"] <- JsonValue.Create reply.Id
    o["url"] <- optionalString reply.Url
    o["type"] <- JsonValue.Create reply.Type
    o["content"] <- JsonValue.Create reply.Content
    o["summary"] <- optionalString reply.Summary
    o["sensitive"] <- JsonValue.Create reply.Sensitive
    o["language"] <- optionalString reply.Language
    o["published"] <- iso reply.Published
    o["updated"] <- (match reply.Updated with Some u -> iso u | None -> null)
    o["audience"] <- JsonValue.Create reply.Audience
    o["inReplyTo"] <- JsonValue.Create reply.InReplyTo

    let parent = JsonObject()
    parent["local"] <- JsonValue.Create reply.Parent.Local
    parent["objectId"] <- (match reply.Parent.ObjectId with Some g -> JsonValue.Create(g.ToString "D") | None -> null)
    parent["userId"] <- optionalString reply.Parent.UserId
    o["parent"] <- parent
    o["thread"] <- threadNode reply.Thread

    let attachments = JsonArray()

    for a in reply.Attachments do
        let node = JsonObject()
        node["type"] <- JsonValue.Create a.Type
        node["mediaType"] <- optionalString a.MediaType
        node["url"] <- JsonValue.Create a.Url
        node["name"] <- optionalString a.Name
        attachments.Add node

    o["attachment"] <- attachments

    let extra = [ "object", (o :> JsonNode) ]

    { Type = eventType
      Actor = Some actor
      Follow = None
      Extra = match extensions with Some e -> extra @ [ "extensions", e ] | None -> extra }

/// `object.deleted` for a reply: only its id and thread
let deletedBody (actor: ActorSummary) (replyId: string) (thread: ThreadRef) : EventBody =
    let o = JsonObject()
    o["id"] <- JsonValue.Create replyId
    o["thread"] <- threadNode thread
    { Type = ObjectDeleted; Actor = Some actor; Follow = None; Extra = [ "object", (o :> JsonNode) ] }
