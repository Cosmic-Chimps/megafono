module Megafono.Tests.Unit.AudienceTests

open System
open System.Text.Json.Nodes
open Xunit
open Megafono.Domain
open Megafono.Domain.Audience
open Megafono.Tests.Fixtures.Publishing

let private domain = "megafono.test"
let private user = "u-1"
let private followers = Ids.actorFollowers domain user
let private pub = "https://www.w3.org/ns/activitystreams#Public"
let private now = DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)

let private content: ActivityStreams.ObjectContent =
    { ObjectType = "Note"
      Content = "<p>hi</p>"
      Url = Some "https://app.example/u/alex/1"
      InReplyTo = None
      Summary = None
      Sensitive = false
      Attachments = [] }

let private strings (node: JsonNode) : string list = node.AsArray() |> Seq.map (fun n -> n.GetValue<string>()) |> List.ofSeq

let private objectDoc audience = ActivityStreams.noteObject domain user (Guid.NewGuid()) audience now None content None

[<Theory>]
[<InlineData("public")>]
[<InlineData("unlisted")>]
[<InlineData("followers")>]
let ``FR-040 the audiences parse and print the same`` (text: string) =
    Assert.Equal(text, name (parse text).Value)

[<Theory>]
[<InlineData("mutuals")>]
[<InlineData("direct")>]
[<InlineData("Public")>]
[<InlineData("")>]
let ``FR-040 other audiences are refused for now`` (text: string) = Assert.True((parse text).IsNone)

[<Fact>]
let ``FR-041 R-06 to and cc follow the addressing table`` () =
    Assert.Equal({ To = [ pub ]; Cc = [ followers ] }, addressing followers Public)
    Assert.Equal({ To = [ followers ]; Cc = [ pub ] }, addressing followers Unlisted)
    Assert.Equal({ To = [ followers ]; Cc = [] }, addressing followers Followers)

[<Theory>]
[<InlineData("public")>]
[<InlineData("unlisted")>]
[<InlineData("followers")>]
let ``FR-041 the activity and the object carry identical addressing, with the actor's own followers URL`` (text: string) =
    let audience = (parse text).Value
    let document = objectDoc audience
    let activity = ActivityStreams.create domain user (Ids.activityId domain (Guid.NewGuid())) now audience None document
    let expected = addressing followers audience
    Assert.Equal<string list>(expected.To, strings (at document "to"))
    Assert.Equal<string list>(expected.Cc, strings (at document "cc"))
    Assert.Equal<string list>(expected.To, strings (at activity "to"))
    Assert.Equal<string list>(expected.Cc, strings (at activity "cc"))
    Assert.Equal<string list>(expected.To, strings (at activity "object.to"))
    Assert.Equal<string list>(expected.Cc, strings (at activity "object.cc"))
    Assert.Equal(followers, "https://megafono.test/ap/actors/u-1/followers")

[<Fact>]
let ``FR-045 a followers-only object is never addressed to Public`` () =
    let document = objectDoc Followers
    let all = strings (at document "to") @ strings (at document "cc")
    Assert.DoesNotContain(pub, all)

[<Fact>]
let ``FR-044 an Update carries updated on the object and the activity, with the same addressing`` () =
    let document = ActivityStreams.noteObject domain user (Guid.NewGuid()) Followers now (Some(now.AddHours 1.0)) content None
    let activity = ActivityStreams.update domain user (Ids.activityId domain (Guid.NewGuid())) (now.AddHours 1.0) Followers None document
    Assert.Equal("Update", jstr ((at activity "type")))
    Assert.Equal("2026-10-10T13:00:00Z", jstr ((at activity "object.updated")))
    Assert.Equal("2026-10-10T13:00:00Z", jstr ((at activity "updated")))
    Assert.Equal<string list>([ followers ], strings (at activity "to"))
    Assert.Equal<string list>([], strings (at activity "cc"))

[<Fact>]
let ``FR-044 a Delete carries a Tombstone to the same audience, never to Public for followers-only`` () =
    let objectGuid = Guid.NewGuid()
    let activity = ActivityStreams.deleteObject domain user (Ids.activityId domain (Guid.NewGuid())) now Followers objectGuid
    Assert.Equal("Delete", jstr ((at activity "type")))
    Assert.Equal("Tombstone", jstr ((at activity "object.type")))
    Assert.Equal(Ids.objectId domain objectGuid, jstr ((at activity "object.id")))
    Assert.Equal<string list>([ followers ], strings (at activity "to"))
    Assert.DoesNotContain(pub, strings (at activity "to") @ strings (at activity "cc"))

    let publicDelete = ActivityStreams.deleteObject domain user (Ids.activityId domain (Guid.NewGuid())) now Public objectGuid
    Assert.Equal<string list>([ pub ], strings (at publicDelete "to"))

[<Fact>]
let ``FR-046 R-07 extension data sits under its prefix with the namespace in the context`` () =
    let data = JsonObject()
    data["watch"] <- JsonNode.Parse """{"malId":52991,"episodes":28}"""

    let extensions: ActivityStreams.Extensions =
        { Namespace = "https://app.example.com/ns#"
          Prefix = "app"
          Data = data }

    let document = ActivityStreams.noteObject domain user (Guid.NewGuid()) Public now None content (Some extensions)
    Assert.Equal("https://www.w3.org/ns/activitystreams", jstr ((at document "@context.0")))
    Assert.Equal("https://app.example.com/ns#", jstr ((at document "@context.1.app")))
    Assert.Equal(52991, intOf ((at document "app:watch.malId")))
    Assert.Null (at document "watch")

    // The Create keeps the context on the activity and the data on the embedded object
    let activity = ActivityStreams.create domain user (Ids.activityId domain (Guid.NewGuid())) now Public (Some extensions) document
    Assert.Equal("https://app.example.com/ns#", jstr ((at activity "@context.1.app")))
    Assert.Equal(28, intOf ((at activity "object.app:watch.episodes")))
    Assert.Null (at activity "object.@context")

[<Fact>]
let ``R-07 a note has the fields remote servers read`` () =
    let document = objectDoc Public
    Assert.Equal("Note", jstr ((at document "type")))
    Assert.Equal(Ids.actorId domain user, jstr ((at document "attributedTo")))
    Assert.Equal("2026-10-10T12:00:00Z", jstr ((at document "published")))
    Assert.Equal("<p>hi</p>", jstr ((at document "content")))
    Assert.Equal("https://app.example/u/alex/1", jstr ((at document "url")))
    Assert.False(boolOf ((at document "sensitive")))
    Assert.Null (at document "updated")
