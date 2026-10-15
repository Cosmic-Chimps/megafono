module Megafono.Tests.Unit.ActorDeletionBuildersTests

open System
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private now = DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero)
let private activityId = Ids.activityId domain (Guid.Parse "6f1c0000-0000-4000-8000-0000000000aa")

[<Fact>]
let ``003 R-30 Delete of an actor has the actor as actor and object, addressed to Public`` () =
    let node = ActivityStreams.deleteActor domain "u-1" activityId now
    let actorId = Ids.actorId domain "u-1"
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at node "@context"))
    Assert.Equal(activityId, jstr (at node "id"))
    Assert.Equal("Delete", jstr (at node "type"))
    Assert.Equal(actorId, jstr (at node "actor"))
    // The object is the bare actor id, not a Tombstone: that is what makes servers delete the account
    Assert.Equal(actorId, jstr (at node "object"))
    Assert.Equal("2026-10-15T09:30:00Z", jstr (at node "published"))
    let recipients = (at node "to").AsArray() |> Seq.map jstr |> List.ofSeq
    Assert.Equal<string list>([ ActivityStreams.publicAudience ], recipients)
    Assert.Null(at node "cc")

[<Fact>]
let ``003 R-30 the actor Tombstone states the id, the former type and when it was deleted, nothing else`` () =
    let node = ActivityStreams.actorTombstone domain "u-1" "Group" now
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at node "@context"))
    Assert.Equal(Ids.actorId domain "u-1", jstr (at node "id"))
    Assert.Equal("Tombstone", jstr (at node "type"))
    Assert.Equal("Group", jstr (at node "formerType"))
    Assert.Equal("2026-10-15T09:30:00Z", jstr (at node "deleted"))
    let keys = (node :?> JsonObject) |> Seq.map (fun kv -> kv.Key) |> Set.ofSeq
    Assert.Equal<Set<string>>(set [ "@context"; "id"; "type"; "formerType"; "deleted" ], keys)
