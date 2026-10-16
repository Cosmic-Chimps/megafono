module Megafono.Tests.Unit.BlockBuildersTests

open System
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private local = Ids.actorId domain "u-1"
let private blocked = "https://spam.example/users/bob"
let private blockId = Ids.activityId domain (Guid.Parse "6f1c0000-0000-4000-8000-0000000000b1")
let private undoId = Ids.activityId domain (Guid.Parse "6f1c0000-0000-4000-8000-0000000000b2")

let private keys (node: JsonNode) = (node :?> JsonObject) |> Seq.map (fun kv -> kv.Key) |> Set.ofSeq
let private strings (node: JsonNode) = node.AsArray() |> Seq.map jstr |> List.ofSeq

[<Fact>]
let ``004 R-40 Block has the local actor as actor, the blocked actor as object and addressee`` () =
    let node = ActivityStreams.block blockId local blocked
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at node "@context"))
    Assert.Equal(blockId, jstr (at node "id"))
    Assert.Equal("Block", jstr (at node "type"))
    Assert.Equal(local, jstr (at node "actor"))
    Assert.Equal(blocked, jstr (at node "object"))
    Assert.Equal<string list>([ blocked ], strings (at node "to"))
    Assert.Equal<Set<string>>(set [ "@context"; "id"; "type"; "actor"; "object"; "to" ], keys node)

[<Fact>]
let ``004 R-40 Undo of a Block embeds the Block under the id it was sent with`` () =
    let sent: ActivityStreams.BlockRef = { Id = blockId; Actor = local; Object = blocked }
    let node = ActivityStreams.undoBlock undoId local sent
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at node "@context"))
    Assert.Equal(undoId, jstr (at node "id"))
    Assert.Equal("Undo", jstr (at node "type"))
    Assert.Equal(local, jstr (at node "actor"))
    Assert.Equal<string list>([ blocked ], strings (at node "to"))
    Assert.Equal(blockId, jstr (at node "object.id"))
    Assert.Equal("Block", jstr (at node "object.type"))
    Assert.Equal(local, jstr (at node "object.actor"))
    Assert.Equal(blocked, jstr (at node "object.object"))
    Assert.Equal<Set<string>>(set [ "id"; "type"; "actor"; "object" ], keys (at node "object"))
    Assert.Equal<Set<string>>(set [ "@context"; "id"; "type"; "actor"; "object"; "to" ], keys node)
