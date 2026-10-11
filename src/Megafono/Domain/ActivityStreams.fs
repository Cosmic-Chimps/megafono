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
      Published: DateTimeOffset }

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
    doc["discoverable"] <- flag true
    doc["indexable"] <- flag false
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

let private noteContext (extensions: Extensions option) : JsonNode =
    match extensions with
    | None -> str "https://www.w3.org/ns/activitystreams"
    | Some e ->
        let terms = JsonObject()
        terms[e.Prefix] <- str e.Namespace
        JsonArray(str "https://www.w3.org/ns/activitystreams", terms)

let private addressingNodes (domain: string) (appUserId: string) (audience: Audience.Audience) =
    let addressing = Audience.addressing (Ids.actorFollowers domain appUserId) audience
    let toArray (items: string list) = JsonArray(items |> List.map str |> Array.ofList)
    toArray addressing.To, toArray addressing.Cc

/// The Note or Article as served at `/ap/objects/{id}`. `updated` is set from the first edit on.
let noteObject
    (domain: string)
    (appUserId: string)
    (objectGuid: Guid)
    (audience: Audience.Audience)
    (published: DateTimeOffset)
    (updated: DateTimeOffset option)
    (content: ObjectContent)
    (extensions: Extensions option)
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
    let toNode, ccNode = addressingNodes domain appUserId audience
    doc["to"] <- toNode
    doc["cc"] <- ccNode

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
    let toNode, ccNode = addressingNodes domain appUserId audience
    doc["to"] <- toNode
    doc["cc"] <- ccNode
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

/// `Delete{Tombstone}`, addressed to the same audience as the object: never to Public for a followers-only one
let deleteObject (domain: string) (appUserId: string) (activityId: string) (now: DateTimeOffset) (audience: Audience.Audience) (objectGuid: Guid) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str activityId
    doc["type"] <- str "Delete"
    doc["actor"] <- str (Ids.actorId domain appUserId)
    doc["published"] <- str (iso now)
    let toNode, ccNode = addressingNodes domain appUserId audience
    doc["to"] <- toNode
    doc["cc"] <- ccNode
    let tombstone = JsonObject()
    tombstone["id"] <- str (Ids.objectId domain objectGuid)
    tombstone["type"] <- str "Tombstone"
    doc["object"] <- tombstone
    doc

/// What a deleted object's URL answers (410)
let tombstone (domain: string) (objectGuid: Guid) (formerType: string) (deleted: DateTimeOffset) : JsonNode =
    let doc = JsonObject()
    doc["@context"] <- str "https://www.w3.org/ns/activitystreams"
    doc["id"] <- str (Ids.objectId domain objectGuid)
    doc["type"] <- str "Tombstone"
    doc["formerType"] <- str formerType
    doc["deleted"] <- str (iso deleted)
    doc
