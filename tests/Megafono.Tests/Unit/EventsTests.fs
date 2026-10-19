module Megafono.Tests.Unit.EventsTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain

/// A string at a dotted path of a JSON document
let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> match n with :? JsonArray as a -> a[int key] | o -> o[key]) node
    target.GetValue<string>()

let private nodeAt (node: JsonNode) (path: string) : JsonNode =
    path.Split('.') |> Array.fold (fun (n: JsonNode) key -> match n with :? JsonArray as a -> a[int key] | o -> o[key]) node
let private boolAt (node: JsonNode) (path: string) : bool = (nodeAt node path).GetValue<bool>()
let private intAt (node: JsonNode) (path: string) : int = (nodeAt node path).GetValue<int>()

let private crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

[<Fact>]
let ``FR-055 the signature is t=<unix>,v1=<hex HMAC-SHA256 of "t.body"> with the webhook secret`` () =
    let body = Encoding.UTF8.GetBytes """{"id":"evt_1"}"""
    let expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes "s3cret", Encoding.UTF8.GetBytes("1791662400." + """{"id":"evt_1"}"""))).ToLowerInvariant()
    Assert.Equal($"t=1791662400,v1={expected}", Events.signature "s3cret" 1791662400L body)

[<Fact>]
let ``FR-055 the signature depends on the secret, the timestamp and every byte of the body`` () =
    let body = "{}"B
    let baseline = Events.signature "a" 1L body
    Assert.NotEqual<string>(baseline, Events.signature "b" 1L body)
    Assert.NotEqual<string>(baseline, Events.signature "a" 2L body)
    Assert.NotEqual<string>(baseline, Events.signature "a" 1L "{ }"B)

[<Fact>]
let ``FR-055 event ids are evt_ plus a 26 character ULID that sorts by time`` () =
    let t = DateTimeOffset(2026, 10, 10, 18, 0, 0, TimeSpan.Zero)
    let first = Events.newEventId t
    let later = Events.newEventId (t.AddSeconds 1.0)
    Assert.Matches(@"^evt_[0-9A-HJKMNP-TV-Z]{26}\z", first)
    Assert.True(String.CompareOrdinal(first, later) < 0)

    // The first ten characters are the millisecond timestamp in Crockford base32
    let ms = first.Substring(4, 10) |> Seq.fold (fun acc c -> acc * 32L + int64 (crockford.IndexOf c)) 0L
    Assert.Equal(t.ToUnixTimeMilliseconds(), ms)

[<Fact>]
let ``FR-055 event ids do not repeat`` () =
    let t = DateTimeOffset.UtcNow
    let ids = [ for _ in 1..5000 -> Events.newEventId t ]
    Assert.Equal(5000, (List.distinct ids).Length)

let private body: Events.EventBody =
    { Type = Events.FollowAccepted
      Actor =
        Some
            { Id = "https://r.example/users/bob"
              Handle = "@bob@r.example"
              Name = "Bob"
              IconUrl = None
              ProfileUrl = Some "https://r.example/@bob" }
      Follow = Some { Id = Guid.Parse "6a1e0000-0000-4000-8000-000000000001"; Direction = "inbound"; Status = "accepted" }
      Extra = [] }

[<Fact>]
let ``app-contract the envelope has every documented field`` () =
    let json = JsonNode.Parse(Events.envelope "evt_X" (DateTimeOffset(2026, 10, 10, 18, 0, 5, TimeSpan.Zero)) 42L "u1" "alex" body)
    Assert.Equal("evt_X", json["id"].GetValue<string>())
    Assert.Equal(1, json["version"].GetValue<int>())
    Assert.Equal("follow.accepted", json["type"].GetValue<string>())
    Assert.Equal("2026-10-10T18:00:05Z", json["occurredAt"].GetValue<string>())
    Assert.Equal(42L, json["seq"].GetValue<int64>())
    Assert.Equal("u1", get json "user.id")
    Assert.Equal("alex", get json "user.handle")
    Assert.Equal("@bob@r.example", get json "actor.handle")
    Assert.Null(json["actor"]["iconUrl"])
    Assert.Equal("https://r.example/@bob", get json "actor.profileUrl")
    Assert.Equal("6a1e0000-0000-4000-8000-000000000001", get json "follow.id")
    Assert.Equal("inbound", get json "follow.direction")
    Assert.Equal("accepted", get json "follow.status")
    Assert.True(json.AsObject().ContainsKey "extensions")
    Assert.Null json["extensions"]

[<Fact>]
let ``app-contract type names match the contract and extra fields are added at the top level`` () =
    Assert.Equal("follow.requested", Events.typeName Events.FollowRequested)
    Assert.Equal("follow.accepted", Events.typeName Events.FollowAccepted)
    Assert.Equal("follow.rejected", Events.typeName Events.FollowRejected)
    Assert.Equal("follow.removed", Events.typeName Events.FollowRemoved)
    Assert.Equal("actor.updated", Events.typeName Events.ActorUpdated)
    Assert.Equal("delivery.failed", Events.typeName Events.DeliveryFailed)

    let failed: Events.EventBody =
        { Type = Events.DeliveryFailed
          Actor = None
          Follow = None
          Extra = [ "object", (let o = JsonObject() in o["id"] <- JsonValue.Create "obj-1"; o :> JsonNode) ] }

    let json = JsonNode.Parse(Events.envelope "evt_Y" DateTimeOffset.UtcNow 1L "u1" "alex" failed)
    Assert.Equal("obj-1", get json "object.id")
    Assert.False(json.AsObject().ContainsKey "actor")
    Assert.False(json.AsObject().ContainsKey "follow")

// ---- replies (feature 002, T004, contracts/app-contract.md) ----------------------------------------------

let private bob: Events.ActorSummary =
    { Id = "https://mastodon.social/users/bob"
      Handle = "@bob@mastodon.social"
      Name = "Bob"
      IconUrl = Some "https://files.mastodon.social/bob.png"
      ProfileUrl = Some "https://mastodon.social/@bob" }

let private rootGuid = Guid.Parse "6f1c0000-0000-4000-8000-000000000001"

let private reply: Events.ReplyObject =
    { Id = "https://mastodon.social/users/bob/statuses/1131"
      Url = Some "https://mastodon.social/@bob/1131"
      Type = "Note"
      Content = "<p>that ending!</p>"
      Summary = Some "Spoilers: S2 E5"
      Sensitive = true
      Language = Some "en"
      Published = DateTimeOffset(2026, 10, 12, 17, 59, 58, TimeSpan.Zero)
      Updated = None
      Audience = "public"
      InReplyTo = "https://frunchy.example/ap/objects/6f1c0000-0000-4000-8000-000000000001"
      Parent = { Local = true; ObjectId = Some rootGuid; UserId = Some "series-52991" }
      Thread = { ObjectId = rootGuid; Id = "https://frunchy.example/ap/objects/6f1c0000-0000-4000-8000-000000000001" }
      Attachments = [ { Type = "Image"; MediaType = Some "image/png"; Url = "https://files.mastodon.social/a.png"; Name = Some "alt text" } ] }

let private envelopeOf (body: Events.EventBody) =
    JsonNode.Parse(Events.envelope "evt_R" (DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero)) 812L "series-52991" "frieren" body)

[<Fact>]
let ``002 app-contract reply.received carries every documented field`` () =
    let json = envelopeOf (Events.replyBody Events.ReplyReceived bob reply None)
    Assert.Equal("reply.received", get json "type")
    Assert.Equal("series-52991", get json "user.id")
    Assert.Equal("https://mastodon.social/users/bob", get json "actor.id")
    Assert.Equal("@bob@mastodon.social", get json "actor.handle")
    Assert.Equal("https://mastodon.social/users/bob/statuses/1131", get json "object.id")
    Assert.Equal("https://mastodon.social/@bob/1131", get json "object.url")
    Assert.Equal("Note", get json "object.type")
    Assert.Equal("<p>that ending!</p>", get json "object.content")
    Assert.Equal("Spoilers: S2 E5", get json "object.summary")
    Assert.True(boolAt json "object.sensitive")
    Assert.Equal("en", get json "object.language")
    Assert.Equal("2026-10-12T17:59:58Z", get json "object.published")
    Assert.Null(nodeAt json "object.updated")
    Assert.True(json["object"].AsObject().ContainsKey "updated")
    Assert.Equal("public", get json "object.audience")
    Assert.Equal(reply.InReplyTo, get json "object.inReplyTo")
    Assert.True(boolAt json "object.parent.local")
    Assert.Equal("6f1c0000-0000-4000-8000-000000000001", get json "object.parent.objectId")
    Assert.Equal("series-52991", get json "object.parent.userId")
    Assert.Equal("6f1c0000-0000-4000-8000-000000000001", get json "object.thread.objectId")
    Assert.Equal(reply.Thread.Id, get json "object.thread.id")
    Assert.Equal("Image", get json "object.attachment.0.type")
    Assert.Equal("image/png", get json "object.attachment.0.mediaType")
    Assert.Equal("https://files.mastodon.social/a.png", get json "object.attachment.0.url")
    Assert.Equal("alt text", get json "object.attachment.0.name")
    Assert.True(json.AsObject().ContainsKey "extensions")
    Assert.Null json["extensions"]

[<Fact>]
let ``002 app-contract a reply to a remote reply has parent.local false and no ids, and missing fields are null`` () =
    let remoteParent = { reply with Parent = { Local = false; ObjectId = None; UserId = None }; Summary = None; Sensitive = false; Language = None; Url = None; Attachments = [] }
    let json = envelopeOf (Events.replyBody Events.ReplyReceived bob remoteParent None)
    Assert.False(boolAt json "object.parent.local")
    Assert.Null(nodeAt json "object.parent.objectId")
    Assert.Null(nodeAt json "object.parent.userId")
    Assert.Null(nodeAt json "object.summary")
    Assert.False(boolAt json "object.sensitive")
    Assert.Null(nodeAt json "object.language")
    Assert.Null(nodeAt json "object.url")
    Assert.Equal(0, (nodeAt json "object.attachment").AsArray().Count)

[<Fact>]
let ``002 app-contract reply.updated sets object.updated`` () =
    let updated = { reply with Updated = Some(DateTimeOffset(2026, 10, 12, 18, 5, 0, TimeSpan.Zero)) }
    let json = envelopeOf (Events.replyBody Events.ReplyUpdated bob updated None)
    Assert.Equal("reply.updated", get json "type")
    Assert.Equal("2026-10-12T18:05:00Z", get json "object.updated")

[<Fact>]
let ``002 app-contract extensions of the reply replace the null`` () =
    let data = JsonObject()
    data["watch"] <- JsonNode.Parse """{"episode":5}"""
    let extensions = JsonObject()
    extensions["namespace"] <- JsonValue.Create "https://app.example/ns#"
    extensions["prefix"] <- JsonValue.Create "app"
    extensions["data"] <- data
    let json = envelopeOf (Events.replyBody Events.ReplyReceived bob reply (Some(extensions :> JsonNode)))
    Assert.Equal("https://app.example/ns#", get json "extensions.namespace")
    Assert.Equal(5, intAt json "extensions.data.watch.episode")

[<Fact>]
let ``002 app-contract object.deleted has only the id and the thread`` () =
    let json = envelopeOf (Events.deletedBody bob reply.Id reply.Thread)
    Assert.Equal("object.deleted", get json "type")
    Assert.Equal(reply.Id, get json "object.id")
    Assert.Equal("6f1c0000-0000-4000-8000-000000000001", get json "object.thread.objectId")
    Assert.Equal(reply.Thread.Id, get json "object.thread.id")
    Assert.Equal(2, (nodeAt json "object").AsObject().Count)
    Assert.Equal("@bob@mastodon.social", get json "actor.handle")
    Assert.Null json["extensions"]

// ---- posts from followed accounts (feature 005, T005, contracts/app-contract.md) ----------------------------

let private post: Events.PostObject =
    { Id = "https://mastodon.social/users/bob/statuses/1200"
      Url = Some "https://mastodon.social/@bob/1200"
      Type = "Note"
      Content = "<p>Started Frieren S2</p>"
      Summary = Some "Spoilers: S2 E1"
      Sensitive = true
      Language = Some "en"
      Published = DateTimeOffset(2026, 10, 18, 17, 59, 58, TimeSpan.Zero)
      Updated = None
      Audience = "followers"
      InReplyTo = None
      Attachments = [ { Type = "Image"; MediaType = Some "image/png"; Url = "https://files.mastodon.social/b.png"; Name = Some "alt text" } ] }

[<Fact>]
let ``005 app-contract post.received carries every documented field, inReplyTo null, no parent or thread`` () =
    let json = envelopeOf (Events.postBody Events.PostReceived bob post None)
    Assert.Equal("post.received", get json "type")
    Assert.Equal("series-52991", get json "user.id")
    Assert.Equal("https://mastodon.social/users/bob", get json "actor.id")
    Assert.Equal("@bob@mastodon.social", get json "actor.handle")
    Assert.Equal("https://mastodon.social/users/bob/statuses/1200", get json "object.id")
    Assert.Equal("https://mastodon.social/@bob/1200", get json "object.url")
    Assert.Equal("Note", get json "object.type")
    Assert.Equal("<p>Started Frieren S2</p>", get json "object.content")
    Assert.Equal("Spoilers: S2 E1", get json "object.summary")
    Assert.True(boolAt json "object.sensitive")
    Assert.Equal("en", get json "object.language")
    Assert.Equal("2026-10-18T17:59:58Z", get json "object.published")
    Assert.Null(nodeAt json "object.updated")
    Assert.True(json["object"].AsObject().ContainsKey "updated")
    Assert.Equal("followers", get json "object.audience")
    Assert.Null(nodeAt json "object.inReplyTo")
    Assert.True(json["object"].AsObject().ContainsKey "inReplyTo")
    Assert.False(json["object"].AsObject().ContainsKey "parent")
    Assert.False(json["object"].AsObject().ContainsKey "thread")
    Assert.Equal("Image", get json "object.attachment.0.type")
    Assert.Equal("image/png", get json "object.attachment.0.mediaType")
    Assert.Equal("https://files.mastodon.social/b.png", get json "object.attachment.0.url")
    Assert.Equal("alt text", get json "object.attachment.0.name")
    Assert.True(json.AsObject().ContainsKey "extensions")
    Assert.Null json["extensions"]

[<Fact>]
let ``005 app-contract post.received passes inReplyTo on and null fields stay null`` () =
    let answer = { post with InReplyTo = Some "https://other.example/users/carol/statuses/9"; Summary = None; Sensitive = false; Language = None; Url = None; Attachments = [] }
    let json = envelopeOf (Events.postBody Events.PostReceived bob answer None)
    Assert.Equal("https://other.example/users/carol/statuses/9", get json "object.inReplyTo")
    Assert.Null(nodeAt json "object.summary")
    Assert.False(boolAt json "object.sensitive")
    Assert.Null(nodeAt json "object.language")
    Assert.Null(nodeAt json "object.url")
    Assert.Equal(0, (nodeAt json "object.attachment").AsArray().Count)

[<Fact>]
let ``005 app-contract post.updated sets object.updated`` () =
    let updated = { post with Updated = Some(DateTimeOffset(2026, 10, 18, 18, 5, 0, TimeSpan.Zero)) }
    let json = envelopeOf (Events.postBody Events.PostUpdated bob updated None)
    Assert.Equal("post.updated", get json "type")
    Assert.Equal("2026-10-18T18:05:00Z", get json "object.updated")
    Assert.Equal("followers", get json "object.audience")

[<Fact>]
let ``005 app-contract extensions of the post replace the null`` () =
    let data = JsonObject()
    data["watch"] <- JsonNode.Parse """{"episode":1}"""
    let extensions = JsonObject()
    extensions["namespace"] <- JsonValue.Create "https://app.example/ns#"
    extensions["prefix"] <- JsonValue.Create "app"
    extensions["data"] <- data
    let json = envelopeOf (Events.postBody Events.PostReceived bob post (Some(extensions :> JsonNode)))
    Assert.Equal("https://app.example/ns#", get json "extensions.namespace")
    Assert.Equal("app", get json "extensions.prefix")
    Assert.Equal(1, intAt json "extensions.data.watch.episode")

[<Fact>]
let ``005 app-contract post.deleted has only the id`` () =
    let json = envelopeOf (Events.postDeletedBody bob post.Id)
    Assert.Equal("post.deleted", get json "type")
    Assert.Equal(post.Id, get json "object.id")
    Assert.Equal(1, (nodeAt json "object").AsObject().Count)
    Assert.Equal("@bob@mastodon.social", get json "actor.handle")
    Assert.Null json["extensions"]

[<Fact>]
let ``005 SC-005 reply.received keeps its exact JSON`` () =
    // field order included (the app may compare the raw body); both sides go through the serializer, which escapes `<`
    let json = Events.envelope "evt_R" (DateTimeOffset(2026, 10, 12, 18, 0, 0, TimeSpan.Zero)) 812L "series-52991" "frieren" (Events.replyBody Events.ReplyReceived bob reply None)

    let expected =
        """{"id":"evt_R","version":1,"type":"reply.received","occurredAt":"2026-10-12T18:00:00Z","seq":812,"user":{"id":"series-52991","handle":"frieren"},"actor":{"id":"https://mastodon.social/users/bob","handle":"@bob@mastodon.social","name":"Bob","iconUrl":"https://files.mastodon.social/bob.png","profileUrl":"https://mastodon.social/@bob"},"object":{"id":"https://mastodon.social/users/bob/statuses/1131","url":"https://mastodon.social/@bob/1131","type":"Note","content":"<p>that ending!</p>","summary":"Spoilers: S2 E5","sensitive":true,"language":"en","published":"2026-10-12T17:59:58Z","updated":null,"audience":"public","inReplyTo":"https://frunchy.example/ap/objects/6f1c0000-0000-4000-8000-000000000001","parent":{"local":true,"objectId":"6f1c0000-0000-4000-8000-000000000001","userId":"series-52991"},"thread":{"objectId":"6f1c0000-0000-4000-8000-000000000001","id":"https://frunchy.example/ap/objects/6f1c0000-0000-4000-8000-000000000001"},"attachment":[{"type":"Image","mediaType":"image/png","url":"https://files.mastodon.social/a.png","name":"alt text"}]},"extensions":null}"""

    Assert.Equal(JsonNode.Parse(expected).ToJsonString(), JsonNode.Parse(json).ToJsonString())
