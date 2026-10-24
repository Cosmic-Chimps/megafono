module Megafono.Tests.Unit.ActorBuildersTests

open System
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures.Publishing

let private profile: ActivityStreams.ActorProfile =
    { AppUserId = "u-1"
      Handle = "alex"
      ActorType = "Person"
      Locked = false
      Name = "Alex"
      Summary = None
      IconUrl = None
      ProfileUrl = "https://app.example/u/alex"
      PublicKeyPem = "pem"
      Published = DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero)
      Discoverable = true
      Indexable = false }

[<Fact>]
let ``the actor document carries the profile's discoverable and indexable`` () =
    for discoverable, indexable in [ true, false; false, false; true, true; false, true ] do
        let node = ActivityStreams.actor "megafono.test" { profile with Discoverable = discoverable; Indexable = indexable }
        Assert.Equal(discoverable, (at node "discoverable").GetValue<bool>())
        Assert.Equal(indexable, (at node "indexable").GetValue<bool>())
