module Megafono.Tests.Unit.EventsTests

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain

/// A string at a dotted path of a JSON document
let private get (node: JsonNode) (path: string) : string =
    let target = path.Split('.') |> Array.fold (fun (n: JsonNode) key -> n[key]) node
    target.GetValue<string>()

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
