module Megafono.Tests.Unit.IdsTests

open System
open Xunit
open Megafono.Domain

[<Theory>]
[<InlineData("3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10")>]
[<InlineData("Alex_01")>]
[<InlineData("a")>]
let ``FR-004 accepts ids made of letters, digits, underscore and dash`` (id: string) =
    Assert.True(Ids.isValidAppUserId id)

[<Fact>]
let ``FR-004 accepts the 64 character limit and refuses 65`` () =
    Assert.True(Ids.isValidAppUserId (String('a', 64)))
    Assert.False(Ids.isValidAppUserId (String('a', 65)))

[<Theory>]
[<InlineData("")>]
[<InlineData("has space")>]
[<InlineData("slash/id")>]
[<InlineData("dot.id")>]
[<InlineData("at@id")>]
[<InlineData("id\n")>]
[<InlineData("idé")>]
let ``FR-004 refuses ids outside the pattern, even with a trailing newline`` (id: string) =
    Assert.False(Ids.isValidAppUserId id)

[<Fact>]
let ``FR-004 refuses null`` () =
    Assert.False(Ids.isValidAppUserId null)
    Assert.False(Ids.isValidHandle null)
    Assert.Equal(None, Ids.normalizeHandle null)

[<Theory>]
[<InlineData("alex", "alex")>]
[<InlineData("Alex_9", "alex_9")>]
[<InlineData("ALEX", "alex")>]
[<InlineData("Anime-Fan", "anime-fan")>]
[<InlineData("a--b", "a--b")>]
[<InlineData("_x-y_", "_x-y_")>]
let ``handles are lowercased then validated`` (input: string, expected: string) =
    Assert.Equal(Some expected, Ids.normalizeHandle input)

[<Theory>]
[<InlineData("")>]
[<InlineData("al ex")>]
[<InlineData("-alex")>]
[<InlineData("alex-")>]
[<InlineData("-")>]
[<InlineData("alex.b")>]
[<InlineData("alex\n")>]
let ``handles outside the pattern are refused`` (input: string) =
    Assert.Equal(None, Ids.normalizeHandle input)

[<Fact>]
let ``handle length is capped at 30`` () =
    Assert.True(Ids.isValidHandle (String('a', 30)))
    Assert.False(Ids.isValidHandle (String('a', 31)))
    Assert.True(Ids.isValidHandle ("a" + String('-', 28) + "b"))
    Assert.False(Ids.isValidHandle ("a" + String('-', 29) + "b"))

[<Fact>]
let ``isValidHandle wants lowercase, normalizeHandle accepts mixed case`` () =
    Assert.False(Ids.isValidHandle "Alex")
    Assert.True(Ids.isValidHandle "alex")

[<Fact>]
let ``FR-005 actor IRIs hang off /ap/actors/{id}`` () =
    let d = "megafono.test"
    Assert.Equal("https://megafono.test/ap/actors/u1", Ids.actorId d "u1")
    Assert.Equal("https://megafono.test/ap/actors/u1#main-key", Ids.actorKeyId d "u1")
    Assert.Equal("https://megafono.test/ap/actors/u1/inbox", Ids.actorInbox d "u1")
    Assert.Equal("https://megafono.test/ap/actors/u1/outbox", Ids.actorOutbox d "u1")
    Assert.Equal("https://megafono.test/ap/actors/u1/followers", Ids.actorFollowers d "u1")
    Assert.Equal("https://megafono.test/ap/actors/u1/following", Ids.actorFollowing d "u1")

[<Fact>]
let ``FR-022 instance actor and shared inbox IRIs`` () =
    let d = "megafono.test"
    Assert.Equal("https://megafono.test/ap/actor", Ids.instanceActorId d)
    Assert.Equal("https://megafono.test/ap/actor#main-key", Ids.instanceActorKeyId d)
    Assert.Equal("https://megafono.test/ap/actor/inbox", Ids.instanceActorInbox d)
    Assert.Equal("https://megafono.test/inbox", Ids.sharedInbox d)

[<Fact>]
let ``object and activity IRIs carry the lowercase hyphenated UUID`` () =
    let id = Guid.Parse "0A1B2C3D-0000-4000-8000-000000000001"
    Assert.Equal("https://megafono.test/ap/objects/0a1b2c3d-0000-4000-8000-000000000001", Ids.objectId "megafono.test" id)
    Assert.Equal("https://megafono.test/ap/activities/0a1b2c3d-0000-4000-8000-000000000001", Ids.activityId "megafono.test" id)
