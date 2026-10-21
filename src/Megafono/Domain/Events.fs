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
    | PostReceived
    | PostUpdated
    | PostDeleted

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
    | PostReceived -> "post.received"
    | PostUpdated -> "post.updated"
    | PostDeleted -> "post.deleted"

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

/// The note fields `reply.received` and `post.received` share, up to `inReplyTo`; the attachments and what is
/// specific to each event follow in the caller, so a reply's JSON keeps its field order
let private noteFields
    (id: string)
    (url: string option)
    (noteType: string)
    (content: string)
    (summary: string option)
    (sensitive: bool)
    (language: string option)
    (published: DateTimeOffset)
    (updated: DateTimeOffset option)
    (audience: string)
    (inReplyTo: JsonNode)
    : JsonObject =
    let o = JsonObject()
    o["id"] <- JsonValue.Create id
    o["url"] <- optionalString url
    o["type"] <- JsonValue.Create noteType
    o["content"] <- JsonValue.Create content
    o["summary"] <- optionalString summary
    o["sensitive"] <- JsonValue.Create sensitive
    o["language"] <- optionalString language
    o["published"] <- iso published
    o["updated"] <- (match updated with Some u -> iso u | None -> null)
    o["audience"] <- JsonValue.Create audience
    o["inReplyTo"] <- inReplyTo
    o

let private attachmentsNode (attachments: ReplyAttachment list) : JsonNode =
    let nodes = JsonArray()

    for a in attachments do
        let node = JsonObject()
        node["type"] <- JsonValue.Create a.Type
        node["mediaType"] <- optionalString a.MediaType
        node["url"] <- JsonValue.Create a.Url
        node["name"] <- optionalString a.Name
        nodes.Add node

    nodes

/// The `object` and `extensions` fields of an event; `extensions` stays out (and so null in the envelope) when absent
let private withExtensions (o: JsonObject) (extensions: JsonNode option) : (string * JsonNode) list =
    let extra = [ "object", (o :> JsonNode) ]
    match extensions with Some e -> extra @ [ "extensions", e ] | None -> extra

/// `reply.received` and `reply.updated`; `extensions` (the app's namespace, data under its prefix) is null when absent
let replyBody (eventType: EventType) (actor: ActorSummary) (reply: ReplyObject) (extensions: JsonNode option) : EventBody =
    let o =
        noteFields
            reply.Id
            reply.Url
            reply.Type
            reply.Content
            reply.Summary
            reply.Sensitive
            reply.Language
            reply.Published
            reply.Updated
            reply.Audience
            (JsonValue.Create reply.InReplyTo)

    let parent = JsonObject()
    parent["local"] <- JsonValue.Create reply.Parent.Local
    parent["objectId"] <- (match reply.Parent.ObjectId with Some g -> JsonValue.Create(g.ToString "D") | None -> null)
    parent["userId"] <- optionalString reply.Parent.UserId
    o["parent"] <- parent
    o["thread"] <- threadNode reply.Thread
    o["attachment"] <- attachmentsNode reply.Attachments

    { Type = eventType
      Actor = Some actor
      Follow = None
      Extra = withExtensions o extensions }

/// `object.deleted` for a reply: only its id and thread
let deletedBody (actor: ActorSummary) (replyId: string) (thread: ThreadRef) : EventBody =
    let o = JsonObject()
    o["id"] <- JsonValue.Create replyId
    o["thread"] <- threadNode thread
    { Type = ObjectDeleted; Actor = Some actor; Follow = None; Extra = [ "object", (o :> JsonNode) ] }

// ---- posts from followed accounts (feature 005, contracts/app-contract.md) ---------------------------------

/// A post of a followed account as the app sees it. `Content` is already sanitized HTML, `Summary` plain text.
/// Unlike a reply it has no `parent` or `thread`: those describe the app's own threads.
type PostObject =
    { Id: string
      Url: string option
      /// `Note` or `Article`
      Type: string
      Content: string
      Summary: string option
      Sensitive: bool
      Language: string option
      Published: DateTimeOffset
      /// Set on `post.updated`
      Updated: DateTimeOffset option
      /// `public`, `unlisted`, `followers` or `direct`
      Audience: string
      /// The post this one answers, on any server
      InReplyTo: string option
      Attachments: ReplyAttachment list }

/// `post.received` and `post.updated`; `extensions` is null when absent
let postBody (eventType: EventType) (actor: ActorSummary) (post: PostObject) (extensions: JsonNode option) : EventBody =
    let o =
        noteFields
            post.Id
            post.Url
            post.Type
            post.Content
            post.Summary
            post.Sensitive
            post.Language
            post.Published
            post.Updated
            post.Audience
            (optionalString post.InReplyTo)

    o["attachment"] <- attachmentsNode post.Attachments

    { Type = eventType
      Actor = Some actor
      Follow = None
      Extra = withExtensions o extensions }

/// `post.deleted`: only the post's id
let postDeletedBody (actor: ActorSummary) (postId: string) : EventBody =
    let o = JsonObject()
    o["id"] <- JsonValue.Create postId
    { Type = PostDeleted; Actor = Some actor; Follow = None; Extra = [ "object", (o :> JsonNode) ] }
