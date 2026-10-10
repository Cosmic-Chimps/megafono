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

let typeName (eventType: EventType) : string =
    match eventType with
    | FollowRequested -> "follow.requested"
    | FollowAccepted -> "follow.accepted"
    | FollowRejected -> "follow.rejected"
    | FollowRemoved -> "follow.removed"
    | ActorUpdated -> "actor.updated"
    | DeliveryFailed -> "delivery.failed"

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

    json["extensions"] <- null
    json.ToJsonString()

/// `Megafono-Signature` value: `t=<unix>,v1=<lowercase hex HMAC-SHA256 of "t.body">` with the webhook secret
let signature (secret: string) (timestamp: int64) (body: byte[]) : string =
    let prefix = Encoding.UTF8.GetBytes $"{timestamp}."
    let message = Array.append prefix body
    let mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes secret, message)
    $"t={timestamp},v1={Convert.ToHexString(mac).ToLowerInvariant()}"
