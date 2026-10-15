module Megafono.Tests.Unit.RepliesBuildersTests

open System
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private user = "u-1"
let private pub = "https://www.w3.org/ns/activitystreams#Public"
let private now = DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)
let private root = Guid.Parse "6f1c0000-0000-4000-8000-000000000001"
let private rootIri = Ids.objectId domain root

let private content: ActivityStreams.ObjectContent =
    { ObjectType = "Note"
      Content = "<p>hi</p>"
      Url = None
      InReplyTo = None
      Summary = None
      Sensitive = false
      Attachments = [] }

let private strings (node: JsonNode) : string list = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq
let private ids (n: int) = [ for i in 1..n -> $"https://r.example/users/bob/statuses/{i}" ]

// ---- the replies collection (R-20) ----------------------------------------------------------------------

[<Fact>]
let ``002 R-20 replies is a Collection with an embedded first page, items as ids and next as a URL`` () =
    let node = ActivityStreams.replies (Ids.objectId domain root) 120L (ids 50) true
    Assert.Equal(Ids.repliesId rootIri, jstr (at node "id"))
    Assert.Equal("Collection", jstr (at node "type"))
    Assert.Equal(120L, (at node "totalItems").GetValue<int64>())
    Assert.Equal($"{rootIri}/replies?page=1", jstr (at node "first.id"))
    Assert.Equal("CollectionPage", jstr (at node "first.type"))
    Assert.Equal(Ids.repliesId rootIri, jstr (at node "first.partOf"))
    Assert.Equal<string list>(ids 50, strings (at node "first.items"))
    Assert.Equal($"{rootIri}/replies?page=2", jstr (at node "first.next"))
    // `items`, never `orderedItems`, on a Collection
    Assert.Null(at node "first.orderedItems")
    Assert.Null(at node "orderedItems")
    Assert.Null(at node "@context")

[<Fact>]
let ``002 R-20 the first page has no next when nothing follows, and an empty collection still has its first page`` () =
    let last = ActivityStreams.replies rootIri 3L (ids 3) false
    Assert.Null(at last "first.next")
    let empty = ActivityStreams.replies rootIri 0L [] false
    Assert.Equal(0L, (at empty "totalItems").GetValue<int64>())
    Assert.Equal(0, (at empty "first.items").AsArray().Count)

[<Fact>]
let ``002 R-20 a later page is a CollectionPage of its own with a context, partOf and its number`` () =
    let page = ActivityStreams.collectionPage (Ids.repliesId rootIri) 3 (ids 20) false
    Assert.Equal("CollectionPage", jstr (at page "type"))
    Assert.Equal($"{rootIri}/replies?page=3", jstr (at page "id"))
    Assert.Equal(Ids.repliesId rootIri, jstr (at page "partOf"))
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at page "@context"))
    Assert.Null(at page "next")
    let more = ActivityStreams.collectionPage (Ids.repliesId rootIri) 3 (ids 50) true
    Assert.Equal($"{rootIri}/replies?page=4", jstr (at more "next"))

[<Fact>]
let ``002 R-24 the context collection is served on its own with the same shape`` () =
    let id = Ids.contextId domain root
    Assert.Equal("https://megafono.test/ap/contexts/6f1c0000-0000-4000-8000-000000000001", id)
    let node = ActivityStreams.standaloneCollection (ActivityStreams.idCollection id 4L (rootIri :: ids 3) false)
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at node "@context"))
    Assert.Equal(rootIri, jstr (at node "first.items.0"))
    Assert.Equal($"{id}?page=1", jstr (at node "first.id"))

// ---- every object is part of a thread (FR-023, R-24) ----------------------------------------------------

[<Fact>]
let ``002 FR-023 a root post is its own context and declares that anyone may reply`` () =
    let doc = ActivityStreams.noteObject domain user root Audience.Public now None content None
    Assert.Equal(Ids.contextId domain root, jstr (at doc "context"))
    Assert.Equal(Ids.contextId domain root, jstr (at doc "conversation"))
    Assert.Equal<string list>([ pub ], strings (at doc "interactionPolicy.canReply.automaticApproval"))
    Assert.Null(at doc "tag")
    Assert.Null(at doc "inReplyTo")
    // the replies collection is computed when the object is served, not stored
    Assert.Null(at doc "replies")

[<Fact>]
let ``002 R-23 a reply carries inReplyTo, the root's context, the parent's author in cc and a Mention`` () =
    let author = "https://r.example/users/bob"
    let thread: ActivityStreams.ThreadInfo = { Root = root; ExtraCc = [ author ]; Mentions = [ { Href = author; Name = "@bob@r.example" } ] }
    let id = Guid.NewGuid()
    let reply = { content with InReplyTo = Some "https://r.example/users/bob/statuses/1" }
    let doc = ActivityStreams.noteObjectIn domain user id Audience.Public now None reply None thread
    Assert.Equal("https://r.example/users/bob/statuses/1", jstr (at doc "inReplyTo"))
    Assert.Equal(Ids.contextId domain root, jstr (at doc "context"))
    Assert.Equal(Ids.contextId domain root, jstr (at doc "conversation"))
    Assert.Equal<string list>([ pub ], strings (at doc "to"))
    Assert.Equal<string list>([ Ids.actorFollowers domain user; author ], strings (at doc "cc"))
    Assert.Equal("Mention", jstr (at doc "tag.0.type"))
    Assert.Equal(author, jstr (at doc "tag.0.href"))
    Assert.Equal("@bob@r.example", jstr (at doc "tag.0.name"))

[<Fact>]
let ``002 R-23 an unlisted reply keeps Public in cc and the followers in to`` () =
    let author = "https://r.example/users/bob"
    let thread: ActivityStreams.ThreadInfo = { Root = root; ExtraCc = [ author; author ]; Mentions = [] }
    let doc = ActivityStreams.noteObjectIn domain user (Guid.NewGuid()) Audience.Unlisted now None content None thread
    Assert.Equal<string list>([ Ids.actorFollowers domain user ], strings (at doc "to"))
    // listed once even when two sources name the same actor
    Assert.Equal<string list>([ pub; author ], strings (at doc "cc"))

[<Fact>]
let ``002 R-23 the Create, Update and Delete of a reply mirror the object's addressing`` () =
    let author = "https://r.example/users/bob"
    let thread: ActivityStreams.ThreadInfo = { Root = root; ExtraCc = [ author ]; Mentions = [] }
    let id = Guid.NewGuid()
    let doc = ActivityStreams.noteObjectIn domain user id Audience.Public now None content None thread
    let create = ActivityStreams.create domain user (Ids.activityId domain (Guid.NewGuid())) now Audience.Public None doc
    Assert.Equal<string list>(strings (at doc "to"), strings (at create "to"))
    Assert.Equal<string list>(strings (at doc "cc"), strings (at create "cc"))
    Assert.Contains(author, strings (at create "object.cc"))
    let update = ActivityStreams.update domain user (Ids.activityId domain (Guid.NewGuid())) now Audience.Public None doc
    Assert.Equal<string list>(strings (at doc "cc"), strings (at update "cc"))

    let delete =
        ActivityStreams.deleteObjectAddressed domain user (Ids.activityId domain (Guid.NewGuid())) now { To = strings (at doc "to"); Cc = strings (at doc "cc") } id

    Assert.Equal("Delete", jstr (at delete "type"))
    Assert.Equal(Ids.objectId domain id, jstr (at delete "object.id"))
    Assert.Equal<string list>(strings (at doc "cc"), strings (at delete "cc"))

[<Fact>]
let ``002 R-24 the context defines the terms Mastodon and GoToSocial read, beside an extension prefix`` () =
    let data = JsonObject()
    data["watch"] <- JsonNode.Parse """{"malId":1}"""
    let extensions: ActivityStreams.Extensions = { Namespace = "https://app.example.com/ns#"; Prefix = "app"; Data = data }
    let doc = ActivityStreams.noteObject domain user root Audience.Public now None content (Some extensions)
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr (at doc "@context.0"))
    Assert.Equal("https://app.example.com/ns#", jstr (at doc "@context.1.app"))
    Assert.Equal("ostatus:conversation", jstr (at doc "@context.1.conversation"))
    Assert.Equal("gts:interactionPolicy", jstr (at doc "@context.1.interactionPolicy.@id"))

[<Fact>]
let ``002 FR-046 extension terms are the string-valued terms that are not Megafono's own`` () =
    let data = JsonObject()
    let extensions: ActivityStreams.Extensions = { Namespace = "https://app.example.com/ns#"; Prefix = "app"; Data = data }
    let doc = ActivityStreams.noteObject domain user root Audience.Public now None content (Some extensions)
    Assert.Equal<(string * string) list>([ "app", "https://app.example.com/ns#" ], ActivityStreams.extensionTerms (doc["@context"]))
    // without an extension nothing is reported, and a plain context or a null is harmless
    let plain = ActivityStreams.noteObject domain user root Audience.Public now None content None
    Assert.Empty(ActivityStreams.extensionTerms (plain["@context"]))
    Assert.Empty(ActivityStreams.extensionTerms (JsonValue.Create "https://www.w3.org/ns/activitystreams"))
    Assert.Empty(ActivityStreams.extensionTerms null)
