module Megafono.Tests.Unit.AnnounceBuildersTests

open System
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private group = "g-1"
let private groupId = Ids.actorId domain group
let private groupFollowers = Ids.actorFollowers domain group
let private pub = "https://www.w3.org/ns/activitystreams#Public"
let private now = DateTimeOffset(2026, 11, 4, 18, 0, 0, TimeSpan.Zero)
let private activityId = Ids.activityId domain (Guid.Parse "6b1e0000-0000-4000-8000-000000000001")
let private replyIri = "https://mastodon.social/users/bob/statuses/1131"
let private author = "https://mastodon.social/users/bob"

let private strings (node: System.Text.Json.Nodes.JsonNode) : string list =
    node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq

// ---- announceAddressing (R-62) --------------------------------------------------------------------------

[<Fact>]
let ``R-62 public addressing is to Public, cc the Group's followers and the author`` () =
    let addressing = ActivityStreams.announceAddressing groupFollowers (Some author) Audience.ReceivedPublic
    Assert.Equal<string list>([ pub ], addressing.To)
    Assert.Equal<string list>([ groupFollowers; author ], addressing.Cc)

[<Fact>]
let ``R-62 unlisted addressing is to the followers, cc Public and the author`` () =
    let addressing = ActivityStreams.announceAddressing groupFollowers (Some author) Audience.ReceivedUnlisted
    Assert.Equal<string list>([ groupFollowers ], addressing.To)
    Assert.Equal<string list>([ pub; author ], addressing.Cc)

[<Fact>]
let ``R-62 the author is omitted when there is none to name (a local reply)`` () =
    let publicAddressing = ActivityStreams.announceAddressing groupFollowers None Audience.ReceivedPublic
    Assert.Equal<string list>([ groupFollowers ], publicAddressing.Cc)
    let unlistedAddressing = ActivityStreams.announceAddressing groupFollowers None Audience.ReceivedUnlisted
    Assert.Equal<string list>([ pub ], unlistedAddressing.Cc)

// ---- announce: a plain boost of the reply's id (R-62) ----------------------------------------------------

[<Fact>]
let ``R-62 announce is a plain boost: object is the reply's id as a string, never a copy`` () =
    let addressing = ActivityStreams.announceAddressing groupFollowers (Some author) Audience.ReceivedPublic
    let doc = ActivityStreams.announce domain group activityId now addressing replyIri
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at doc "@context"))
    Assert.Equal(activityId, jstr (at doc "id"))
    Assert.Equal("Announce", jstr (at doc "type"))
    Assert.Equal(groupId, jstr (at doc "actor"))
    Assert.Equal("2026-11-04T18:00:00Z", jstr (at doc "published"))
    Assert.Equal<string list>([ pub ], strings (at doc "to"))
    Assert.Equal<string list>([ groupFollowers; author ], strings (at doc "cc"))
    // the object is the reply's id as a plain string, not an embedded document
    Assert.Equal(replyIri, jstr (at doc "object"))

// ---- announceUpdate: Announce{Update} (R-63) ---------------------------------------------------------------

[<Fact>]
let ``R-63 announceUpdate embeds a minimal Update pointing at the reply, never trusted content`` () =
    let addressing = ActivityStreams.announceAddressing groupFollowers (Some author) Audience.ReceivedPublic
    let updateId = $"{replyIri}#updates/1762280400"
    let doc = ActivityStreams.announceUpdate domain group activityId now addressing updateId author replyIri
    Assert.Equal("Announce", jstr (at doc "type"))
    Assert.Equal(groupId, jstr (at doc "actor"))
    Assert.Equal<string list>([ groupFollowers; author ], strings (at doc "cc"))
    Assert.Equal(updateId, jstr (at doc "object.id"))
    Assert.Equal("Update", jstr (at doc "object.type"))
    Assert.Equal(author, jstr (at doc "object.actor"))
    Assert.Equal(replyIri, jstr (at doc "object.object"))

// ---- undoAnnounce: Undo{Announce}, the retraction (R-63) ---------------------------------------------------

[<Fact>]
let ``R-63 undoAnnounce embeds the Group's own original Announce by its id`` () =
    let addressing = ActivityStreams.announceAddressing groupFollowers (Some author) Audience.ReceivedUnlisted
    let doc = ActivityStreams.undoAnnounce domain group activityId now addressing activityId replyIri
    Assert.Equal("Undo", jstr (at doc "type"))
    Assert.Equal(groupId, jstr (at doc "actor"))
    Assert.Equal<string list>([ groupFollowers ], strings (at doc "to"))
    Assert.Equal<string list>([ pub; author ], strings (at doc "cc"))
    Assert.Equal(activityId, jstr (at doc "object.id"))
    Assert.Equal("Announce", jstr (at doc "object.type"))
    Assert.Equal(groupId, jstr (at doc "object.actor"))
    Assert.Equal(replyIri, jstr (at doc "object.object"))
