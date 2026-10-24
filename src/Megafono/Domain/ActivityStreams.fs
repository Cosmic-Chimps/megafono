/// ActivityStreams documents Megáfono publishes (research R-02): actors, the instance actor and collection
/// stubs. Pure: they take plain values and return JSON text, so the wire shapes are testable without HTTP.
module Megafono.Domain.ActivityStreams

open System
open System.Text.Encodings.Web
open System.Text.Json
open System.Text.Json.Nodes

/// Shared by every document: no indentation, and `&`, `<` and non-ASCII written as themselves (the content type
/// is JSON, never HTML, so the extra escaping buys nothing and makes the documents harder to read)
let jsonOptions =
    JsonSerializerOptions(WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

let toBytes (node: JsonNode) : byte[] =
    Text.Encoding.UTF8.GetBytes(node.ToJsonString jsonOptions)

let publicAudience = "https://www.w3.org/ns/activitystreams#Public"

let private str (value: string) : JsonNode = JsonValue.Create value
let private flag (value: bool) : JsonNode = JsonValue.Create value
let private iso (time: DateTimeOffset) = time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'")

/// `@context` of every actor: ActivityStreams, the security vocabulary (public keys) and the Mastodon terms
/// that Mastodon and GoToSocial read (`manuallyApprovesFollowers`, `discoverable`, `indexable`), plus FEP-2c59's `webfinger`
let private actorContext () : JsonNode =
    let terms = JsonObject()
    terms["manuallyApprovesFollowers"] <- str "as:manuallyApprovesFollowers"
    terms["toot"] <- str "http://joinmastodon.org/ns#"
    terms["schema"] <- str "http://schema.org#"
    terms["discoverable"] <- str "toot:discoverable"
    terms["indexable"] <- str "toot:indexable"
    terms["webfinger"] <- str "https://purl.archive.org/socialweb/webfinger#webfinger"

    JsonArray(str "https://www.w3.org/ns/activitystreams", str "https://w3id.org/security/v1", terms)

type ActorProfile =
    { AppUserId: string
      Handle: string
      /// `Person`, `Service` or `Group`
      ActorType: string
      Locked: bool
      Name: string
      /// HTML, owned by the app
      Summary: string option
      IconUrl: string option
      ProfileUrl: string
      PublicKeyPem: string
      Published: DateTimeOffset
      /// Requests to other servers, set by the app (directories and suggestions; full-text search of public posts)
      Discoverable: bool
      Indexable: bool }

let private publicKey (keyId: string) (owner: string) (pem: string) : JsonNode =
    let key = JsonObject()
    key["id"] <- str keyId
    key["owner"] <- str owner
    key["publicKeyPem"] <- str pem
    key

/// The actor document at `https://{domain}/ap/actors/{id}`. Its `id` is the request URL.
let actor (domain: string) (profile: ActorProfile) : JsonNode =
    let id = Ids.actorId domain profile.AppUserId
    let doc = JsonObject()
    doc["@context"] <- actorContext ()
    doc["id"] <- str id
    doc["type"] <- str profile.ActorType
    doc["preferredUsername"] <- str profile.Handle
    doc["name"] <- str profile.Name

    profile.Summary |> Option.iter (fun s -> doc["summary"] <- str s)

    doc["url"] <- str profile.ProfileUrl
    doc["inbox"] <- str (Ids.actorInbox domain profile.AppUserId)
    doc["outbox"] <- str (Ids.actorOutbox domain profile.AppUserId)
    doc["followers"] <- str (Ids.actorFollowers domain profile.AppUserId)
    doc["following"] <- str (Ids.actorFollowing domain profile.AppUserId)

    let endpoints = JsonObject()
    endpoints["sharedInbox"] <- str (Ids.sharedInbox domain)
    doc["endpoints"] <- endpoints

    doc["manuallyApprovesFollowers"] <- flag profile.Locked
    doc["discoverable"] <- flag profile.Discoverable
    doc["indexable"] <- flag profile.Indexable
    doc["published"] <- str (iso profile.Published)

    profile.IconUrl
    |> Option.iter (fun url ->
        let icon = JsonObject()
        icon["type"] <- str "Image"
        icon["url"] <- str url
        doc["icon"] <- icon)

    doc["webfinger"] <- str $"acct:{profile.Handle}@{domain}"
    doc["publicKey"] <- publicKey (Ids.actorKeyId domain profile.AppUserId) id profile.PublicKeyPem
    doc

/// The instance actor (`Application`) at `https://{domain}/ap/actor`: signs outbound GETs that no user made
let instanceActor (domain: string) (publicKeyPem: string) : JsonNode =
    let id = Ids.instanceActorId domain
    let doc = JsonObject()
    doc["@context"] <- actorContext ()
    doc["id"] <- str id
    doc["type"] <- str "Application"
    doc["preferredUsername"] <- str domain
    doc["name"] <- str domain
    doc["url"] <- str $"https://{domain}/"
    doc["inbox"] <- str (Ids.instanceActorInbox domain)
    doc["outbox"] <- str $"{id}/outbox"
    doc["manuallyApprovesFollowers"] <- flag true
    doc["discoverable"] <- flag false
    doc["webfinger"] <- str $"acct:{domain}@{domain}"
    doc["publicKey"] <- publicKey (Ids.instanceActorKeyId domain) id publicKeyPem
    doc

/// An `OrderedCollection` that states its size and lists nothing (outbox, followers, following)
let collectionStub (collectionId: string) (totalItems: int64) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str collectionId
    doc["type"] <- str "OrderedCollection"
    doc["totalItems"] <- JsonValue.Create totalItems
    doc

/// `Update` of an actor, addressed to the public like Mastodon's own profile updates; delivered to followers'
/// inboxes. `actorDocument` is the new actor document as served.
let updateActor (domain: string) (appUserId: string) (activityId: string) (now: DateTimeOffset) (actorDocument: JsonNode) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- actorContext ()
    doc["id"] <- str activityId
    doc["type"] <- str "Update"
    doc["actor"] <- str (Ids.actorId domain appUserId)
    doc["published"] <- str (iso now)
    doc["to"] <- JsonArray(str publicAudience)
    doc["object"] <- actorDocument
    doc

/// WebFinger JRD for a local actor (research R-03)
let webFinger (domain: string) (profile: ActorProfile) : JsonNode =
    let actorId = Ids.actorId domain profile.AppUserId
    let self = JsonObject()
    self["rel"] <- str "self"
    self["type"] <- str "application/activity+json"
    self["href"] <- str actorId
    let page = JsonObject()
    page["rel"] <- str "http://webfinger.net/rel/profile-page"
    page["type"] <- str "text/html"
    page["href"] <- str profile.ProfileUrl
    let jrd = JsonObject()
    jrd["subject"] <- str $"acct:{profile.Handle}@{domain}"
    jrd["aliases"] <- JsonArray(str profile.ProfileUrl, str actorId)
    jrd["links"] <- JsonArray(self, page)
    jrd

/// WebFinger JRD for the instance actor, `acct:{domain}@{domain}` (what Mastodon does too). GoToSocial and Mastodon
/// webfinger every actor they dereference to check its `preferredUsername@host`; without this answer they refuse
/// the instance actor's signed GETs and so every key and actor fetch Megáfono makes.
let instanceWebFinger (domain: string) : JsonNode =
    let actorId = Ids.instanceActorId domain
    let self = JsonObject()
    self["rel"] <- str "self"
    self["type"] <- str "application/activity+json"
    self["href"] <- str actorId
    let jrd = JsonObject()
    jrd["subject"] <- str $"acct:{domain}@{domain}"
    jrd["aliases"] <- JsonArray(str actorId)
    jrd["links"] <- JsonArray(self)
    jrd

let private nodeInfoSchema (version: string) =
    $"http://nodeinfo.diaspora.software/ns/schema/{version}"

/// `/.well-known/nodeinfo`: links to both schema versions
let nodeInfoLinks (domain: string) : JsonNode =
    let link (version: string) =
        let l = JsonObject()
        l["rel"] <- str (nodeInfoSchema version)
        l["href"] <- str $"https://{domain}/nodeinfo/{version}"
        l

    let doc = JsonObject()
    doc["links"] <- JsonArray(link "2.0", link "2.1")
    doc

/// NodeInfo 2.0 or 2.1 (research R-04)
let nodeInfo (version: string) (softwareName: string) (softwareVersion: string) (totalUsers: int64) : JsonNode =
    let software = JsonObject()
    software["name"] <- str softwareName
    software["version"] <- str softwareVersion

    let services = JsonObject()
    services["inbound"] <- JsonArray()
    services["outbound"] <- JsonArray()

    let users = JsonObject()
    users["total"] <- JsonValue.Create totalUsers
    let usage = JsonObject()
    usage["users"] <- users

    let doc = JsonObject()
    doc["version"] <- str version
    doc["software"] <- software
    doc["protocols"] <- JsonArray(str "activitypub")
    doc["services"] <- services
    doc["openRegistrations"] <- flag false
    doc["usage"] <- usage
    doc["metadata"] <- JsonObject()
    doc

// ---- objects and their activities (US3; research R-06, R-07) ---------------------------------------------

type Attachment =
    { Url: string
      MediaType: string option
      Name: string option }

/// What the app sends in `object` of a publish or update call, already validated
type ObjectContent =
    { ObjectType: string
      Content: string
      Url: string option
      InReplyTo: string option
      Summary: string option
      Sensitive: bool
      Attachments: Attachment list }

/// App data carried under its namespace: `Data`'s top-level keys become `prefix:key` properties (FR-046)
type Extensions =
    { Namespace: string
      Prefix: string
      Data: JsonObject }

/// Terms Megáfono defines itself in the `@context` of an object (R-24); an app's extension prefix is never one of these
let private ownTerms = set [ "ostatus"; "conversation"; "gts"; "interactionPolicy"; "canReply"; "automaticApproval" ]

let private noteContext (extensions: Extensions option) : JsonNode =
    let terms = JsonObject()
    terms["ostatus"] <- str "http://ostatus.org#"
    terms["conversation"] <- str "ostatus:conversation"
    terms["gts"] <- str "https://gotosocial.org/ns#"
    let policy = JsonObject()
    policy["@id"] <- str "gts:interactionPolicy"
    policy["@type"] <- str "@id"
    terms["interactionPolicy"] <- policy
    let canReply = JsonObject()
    canReply["@id"] <- str "gts:canReply"
    canReply["@type"] <- str "@id"
    terms["canReply"] <- canReply
    let approval = JsonObject()
    approval["@id"] <- str "gts:automaticApproval"
    approval["@type"] <- str "@id"
    terms["automaticApproval"] <- approval
    extensions |> Option.iter (fun e -> terms[e.Prefix] <- str e.Namespace)
    JsonArray(str "https://www.w3.org/ns/activitystreams", terms)

/// The string-valued terms of a received `@context` that are not Megáfono's own: `(prefix, namespace)` pairs.
/// An app's extension arrives as one of these (FR-046: matched by the full namespace, never by the prefix).
let extensionTerms (context: JsonNode) : (string * string) list =
    let ofObject (o: JsonObject) =
        [ for entry in o do
              match entry.Value with
              | :? JsonValue as v when v.GetValueKind() = JsonValueKind.String && not (ownTerms.Contains entry.Key) ->
                  yield entry.Key, v.GetValue<string>()
              | _ -> () ]

    match context with
    | :? JsonObject as o -> ofObject o
    | :? JsonArray as a -> [ for item in a do match item with :? JsonObject as o -> yield! ofObject o | _ -> () ]
    | _ -> []

/// An actor a reply mentions: `name` is `@user@host`
type Mention = { Href: string; Name: string }

/// What makes an object part of a thread (research R-23, R-24): the root it belongs to, the actors addressed in
/// `cc` besides the audience's own addressing, and the Mentions that go with them
type ThreadInfo =
    { Root: Guid
      ExtraCc: string list
      Mentions: Mention list }

/// The `canReply` declaration every object carries: anyone may reply, no approval round trip (R-24)
let private interactionPolicy () : JsonNode =
    let canReply = JsonObject()
    canReply["automaticApproval"] <- JsonArray(str publicAudience)
    let policy = JsonObject()
    policy["canReply"] <- canReply
    policy

/// The Note or Article as served at `/ap/objects/{id}`, a member of the thread `thread`. `updated` is set from the
/// first edit on. `replies` is not part of it: it is computed when the object is served.
let noteObjectIn
    (domain: string)
    (appUserId: string)
    (objectGuid: Guid)
    (audience: Audience.Audience)
    (published: DateTimeOffset)
    (updated: DateTimeOffset option)
    (content: ObjectContent)
    (extensions: Extensions option)
    (thread: ThreadInfo)
    : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- noteContext extensions
    doc["id"] <- str (Ids.objectId domain objectGuid)
    doc["type"] <- str content.ObjectType
    doc["attributedTo"] <- str (Ids.actorId domain appUserId)
    doc["published"] <- str (iso published)
    updated |> Option.iter (fun u -> doc["updated"] <- str (iso u))
    doc["content"] <- str content.Content
    content.Url |> Option.iter (fun u -> doc["url"] <- str u)
    content.Summary |> Option.iter (fun s -> doc["summary"] <- str s)
    content.InReplyTo |> Option.iter (fun r -> doc["inReplyTo"] <- str r)
    doc["sensitive"] <- flag content.Sensitive
    let addressing = Audience.addressing (Ids.actorFollowers domain appUserId) audience
    let nodes (items: string list) = JsonArray(items |> List.map str |> Array.ofList)
    doc["to"] <- nodes addressing.To
    doc["cc"] <- nodes (addressing.Cc @ thread.ExtraCc |> List.distinct)

    let context = Ids.contextId domain thread.Root
    doc["context"] <- str context
    doc["conversation"] <- str context
    doc["interactionPolicy"] <- interactionPolicy ()

    if not thread.Mentions.IsEmpty then
        let tags = JsonArray()

        for m in thread.Mentions do
            let tag = JsonObject()
            tag["type"] <- str "Mention"
            tag["href"] <- str m.Href
            tag["name"] <- str m.Name
            tags.Add tag

        doc["tag"] <- tags

    if not content.Attachments.IsEmpty then
        let attachments = JsonArray()

        for a in content.Attachments do
            let node = JsonObject()
            node["type"] <- str "Document"
            a.MediaType |> Option.iter (fun m -> node["mediaType"] <- str m)
            node["url"] <- str a.Url
            a.Name |> Option.iter (fun n -> node["name"] <- str n)
            attachments.Add node

        doc["attachment"] <- attachments

    match extensions with
    | Some e ->
        for entry in e.Data do
            doc[$"{e.Prefix}:{entry.Key}"] <- if isNull entry.Value then null else entry.Value.DeepClone()
    | None -> ()

    doc

/// A root post: its own thread, nobody mentioned
let noteObject domain appUserId (objectGuid: Guid) audience published updated content extensions =
    noteObjectIn domain appUserId objectGuid audience published updated content extensions { Root = objectGuid; ExtraCc = []; Mentions = [] }

/// The `Create` or `Update` for an object: same addressing as the object, the object embedded without its own context
let private objectActivity
    (activityType: string)
    (domain: string)
    (appUserId: string)
    (activityId: string)
    (now: DateTimeOffset)
    (audience: Audience.Audience)
    (extensions: Extensions option)
    (objectDocument: JsonNode)
    : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- noteContext extensions
    doc["id"] <- str activityId
    doc["type"] <- str activityType
    doc["actor"] <- str (Ids.actorId domain appUserId)
    doc["published"] <- str (iso now)
    // The activity mirrors its object's addressing (a reply's `cc` holds more than the audience's own)
    doc["to"] <- objectDocument["to"].DeepClone()
    doc["cc"] <- objectDocument["cc"].DeepClone()
    let embedded = objectDocument.DeepClone().AsObject()
    embedded.Remove "@context" |> ignore
    doc["object"] <- embedded
    doc

let create domain appUserId activityId now audience extensions objectDocument =
    objectActivity "Create" domain appUserId activityId now audience extensions objectDocument

/// `Update` of an object that carries `updated` (Mastodon applies an edit only then); `updated` is repeated on the activity
let update domain appUserId activityId (now: DateTimeOffset) audience extensions objectDocument =
    let doc = objectActivity "Update" domain appUserId activityId now audience extensions objectDocument
    doc["updated"] <- str (iso now)
    doc

/// `Delete{Tombstone}` to exactly these `to` and `cc`: those of the object it removes
let deleteObjectAddressed (domain: string) (appUserId: string) (activityId: string) (now: DateTimeOffset) (addressing: Audience.Addressing) (objectGuid: Guid) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str activityId
    doc["type"] <- str "Delete"
    doc["actor"] <- str (Ids.actorId domain appUserId)
    doc["published"] <- str (iso now)
    let nodes (items: string list) = JsonArray(items |> List.map str |> Array.ofList)
    doc["to"] <- nodes addressing.To
    doc["cc"] <- nodes addressing.Cc
    let tombstone = JsonObject()
    tombstone["id"] <- str (Ids.objectId domain objectGuid)
    tombstone["type"] <- str "Tombstone"
    doc["object"] <- tombstone
    doc

/// `Delete{Tombstone}`, addressed to the same audience as the object: never to Public for a followers-only one
let deleteObject (domain: string) (appUserId: string) (activityId: string) (now: DateTimeOffset) (audience: Audience.Audience) (objectGuid: Guid) : JsonNode =
    deleteObjectAddressed domain appUserId activityId now (Audience.addressing (Ids.actorFollowers domain appUserId) audience) objectGuid

/// What a deleted object's URL answers (410)
let tombstone (domain: string) (objectGuid: Guid) (formerType: string) (deleted: DateTimeOffset) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str (Ids.objectId domain objectGuid)
    doc["type"] <- str "Tombstone"
    doc["formerType"] <- str formerType
    doc["deleted"] <- str (iso deleted)
    doc

// ---- actor deletion (feature 003; research R-30) ------------------------------------------------------

/// `Delete{actor}`: actor and object are both the actor id, addressed to Public like Mastodon's own account deletion
let deleteActor (domain: string) (appUserId: string) (activityId: string) (now: DateTimeOffset) : JsonNode =
    let actorId = Ids.actorId domain appUserId
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str activityId
    doc["type"] <- str "Delete"
    doc["actor"] <- str actorId
    doc["object"] <- str actorId
    doc["published"] <- str (iso now)
    doc["to"] <- JsonArray(str publicAudience)
    doc

/// What a deleted actor's URL answers (410): its id, former type and deletion time, nothing else
let actorTombstone (domain: string) (appUserId: string) (formerType: string) (deleted: DateTimeOffset) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str (Ids.actorId domain appUserId)
    doc["type"] <- str "Tombstone"
    doc["formerType"] <- str formerType
    doc["deleted"] <- str (iso deleted)
    doc

// ---- replies and threads (feature 002; research R-20, R-24) ------------------------------------------

/// Replies and context pages hold at most this many ids
let pageSize = 50

let private page (collectionId: string) (number: int) (items: string list) (hasNext: bool) : JsonObject =
    let doc = JsonObject()
    doc["id"] <- str $"{collectionId}?page={number}"
    doc["type"] <- str "CollectionPage"
    doc["partOf"] <- str collectionId
    doc["items"] <- JsonArray(items |> List.map str |> Array.ofList)

    if hasNext then
        doc["next"] <- str $"{collectionId}?page={number + 1}"

    doc

/// A `Collection` of ids: `totalItems` and an embedded first page, the following ones linked by URL. `items`
/// (never `orderedItems`) because the type is `Collection`; Mastodon ignores a collection's own `items` once
/// `first` exists, so the ids live in the page.
let idCollection (collectionId: string) (totalItems: int64) (firstItems: string list) (hasNext: bool) : JsonObject =
    let doc = JsonObject()
    doc["id"] <- str collectionId
    doc["type"] <- str "Collection"
    doc["totalItems"] <- JsonValue.Create totalItems
    doc["first"] <- page collectionId 1 firstItems hasNext
    doc

/// The `replies` property of an object, as embedded in its document
let replies (objectIri: string) (totalItems: int64) (firstItems: string list) (hasNext: bool) : JsonNode =
    idCollection (Ids.repliesId objectIri) totalItems firstItems hasNext

/// Page `number` (from 2) of a collection, served on its own
let collectionPage (collectionId: string) (number: int) (items: string list) (hasNext: bool) : JsonNode =
    let doc = page collectionId number items hasNext
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc

/// A collection served on its own (`/replies`, `/ap/contexts/{root}`): the embedded form with a context
let standaloneCollection (collection: JsonObject) : JsonNode =
    collection["@context"] <- str "https://www.w3.org/ns/activitystreams"
    collection

// ---- serve-time thread fields (feature 002, US3; research R-20, R-24) ----------------------------------

/// A stored object document as served: `replies` (computed now), `context`, `conversation` and `interactionPolicy`
/// are set whatever the stored copy holds, so objects stored before 002 carry them too, and the terms the last
/// two need are added to `@context` when it lacks them. The document's `id` is left as stored.
let withThreadFields (domain: string) (root: Guid) (repliesProperty: JsonNode) (doc: JsonObject) : JsonNode =
    let context = Ids.contextId domain root
    doc["context"] <- str context
    doc["conversation"] <- str context
    doc["interactionPolicy"] <- interactionPolicy ()
    doc["replies"] <- repliesProperty

    let terms =
        match noteContext None with
        | :? JsonArray as a -> a[1] :?> JsonObject
        | _ -> JsonObject()

    let existing: JsonNode =
        match doc["@context"] with
        | null -> null
        | n -> n.DeepClone()

    let items = JsonArray()

    match existing with
    | :? JsonArray as a -> for item in a do items.Add(item.DeepClone())
    | null -> items.Add(str "https://www.w3.org/ns/activitystreams")
    | single -> items.Add single

    let target =
        match items |> Seq.tryPick (fun n -> match n with :? JsonObject as o -> Some o | _ -> None) with
        | Some o -> o
        | None ->
            let o = JsonObject()
            items.Add o
            o

    for entry in terms do
        if not (target.ContainsKey entry.Key) then
            target[entry.Key] <- entry.Value.DeepClone()

    doc["@context"] <- items
    doc :> JsonNode

// ======================================================================================================
// Follow traffic (User Story 2, research R-05): Follow, Accept, Reject, Undo
// ======================================================================================================

/// The three ids that identify a Follow
type FollowRef =
    { Id: string
      /// Who follows
      Actor: string
      /// Who is followed
      Object: string }

let private followNode (follow: FollowRef) : JsonNode =
    let node = JsonObject()
    node["id"] <- str follow.Id
    node["type"] <- str "Follow"
    node["actor"] <- str follow.Actor
    node["object"] <- str follow.Object
    node

let private followActivity (kind: string) (activityId: string) (actorId: string) (obj: JsonNode) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str activityId
    doc["type"] <- str kind
    doc["actor"] <- str actorId
    doc["object"] <- obj
    doc

/// A Follow of `targetId` by the local actor `actorId`
let follow (activityId: string) (actorId: string) (targetId: string) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str activityId
    doc["type"] <- str "Follow"
    doc["actor"] <- str actorId
    doc["object"] <- str targetId
    doc

/// Accept by the local actor, with its own id and the FULL Follow embedded: GoToSocial refuses an Accept
/// without an id and only understands the embedded form, Mastodon matches on it (R-05)
let accept (activityId: string) (localActorId: string) (followed: FollowRef) : JsonNode =
    followActivity "Accept" activityId localActorId (followNode followed)

let reject (activityId: string) (localActorId: string) (followed: FollowRef) : JsonNode =
    followActivity "Reject" activityId localActorId (followNode followed)

/// Undo of a Follow by the local actor
let undoFollow (activityId: string) (localActorId: string) (followed: FollowRef) : JsonNode =
    followActivity "Undo" activityId localActorId (followNode followed)

// ======================================================================================================
// Block traffic (specs/004-blocks R-40): Block and Undo{Block}
// ======================================================================================================

/// The ids that identify a Block
type BlockRef =
    { Id: string
      /// Who blocks (the local actor)
      Actor: string
      /// Who is blocked
      Object: string }

/// Block of the remote actor `targetId` by the local actor `localActorId`, addressed to the blocked actor
let block (activityId: string) (localActorId: string) (targetId: string) : JsonNode =
    let doc = followActivity "Block" activityId localActorId (str targetId)
    doc["to"] <- JsonArray(str targetId)
    doc

/// Undo of a Block by the local actor, with the full Block embedded under the id it was sent with
let undoBlock (activityId: string) (localActorId: string) (blocked: BlockRef) : JsonNode =
    let node = JsonObject()
    node["id"] <- str blocked.Id
    node["type"] <- str "Block"
    node["actor"] <- str blocked.Actor
    node["object"] <- str blocked.Object
    let doc = followActivity "Undo" activityId localActorId node
    doc["to"] <- JsonArray(str blocked.Object)
    doc
