/// Replies from the fediverse (feature 002, FR-001 to FR-006, research R-22): `Create{Note|Article}` whose
/// `inReplyTo` is one of the app's objects or a reply already recorded in one of its threads, and the `Update` and
/// `Delete` of such a reply by its author. Each activity is handled in one transaction: the processed marker, the
/// reply record and the app event commit together or not at all. The event goes to the author of the thread's root.
///
/// Trust follows 001 (FR-015): the reply object embedded in the activity is used only when its id is on the signer's
/// host; any other id is fetched from its origin, signed. Either way the object must be attributed to the signer.
/// What is not for us is answered 202 and dropped, so a sender learns nothing about what we know.
module Megafono.Federation.InboxReplies

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let private isReplyType (node: JsonNode) =
    match str node "type" with
    | Some("Note" | "Article") -> true
    | _ -> false

/// True for an `Update` whose object is a Note or Article (a reply edit), as opposed to a profile update
let isNoteUpdate (activity: JsonNode) : bool =
    match activity["object"] with
    | :? JsonObject as o -> isReplyType o
    | _ -> false

/// The reply object of an activity: the embedded one when it is on the signer's host, else fetched from its own
/// origin. It must be a Note or Article attributed to the signer.
let private fetchNote (env: Env) (id: string) : Task<JsonObject option> =
    task {
        match! RemoteActors.fetchObject env.Remote id with
        | Ok fetched when str fetched "id" = Some id -> return Some fetched
        | _ -> return None
    }

let private noteOf (env: Env) (signer: RemoteActor) (node: JsonNode) : Task<JsonObject option> =
    task {
        let! candidate =
            match node with
            | :? JsonObject as o ->
                match str o "id" with
                | Some id when isOnSignerHost signer id -> Task.FromResult(Some o)
                | Some id -> fetchNote env id
                | None -> Task.FromResult None
            | :? JsonValue ->
                match idOf node with
                | Some id -> fetchNote env id
                | None -> Task.FromResult None
            | _ -> Task.FromResult None

        return candidate |> Option.filter (fun note -> isReplyType note && idOf note["attributedTo"] = Some signer.Id)
    }

// ---- reading the note ------------------------------------------------------------------------------------

let private iris (node: JsonNode) : string list =
    match node with
    | :? JsonArray as items -> [ for item in items do match idOf item with Some i -> yield i | None -> () ]
    | null -> []
    | single -> idOf single |> Option.toList

let private time (node: JsonNode) (name: string) : DateTimeOffset option =
    match str node name with
    | Some text ->
        match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, t -> Some t
        | _ -> None
    | None -> None

let private flag (node: JsonNode) (name: string) : bool =
    match node[name] with
    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.True -> true
    | _ -> false

let private isHttp (allowHttp: bool) (text: string) =
    match Uri.TryCreate(text, UriKind.Absolute) with
    | true, u -> u.Scheme = Uri.UriSchemeHttps || (allowHttp && u.Scheme = Uri.UriSchemeHttp)
    | _ -> false

/// A URL given as a string, a Link (`href`) or a list of them: the first usable one
let private urlOf (allowHttp: bool) (node: JsonNode) : string option =
    let candidates =
        match node with
        | :? JsonArray as items -> [ for item in items -> item ]
        | null -> []
        | single -> [ single ]

    candidates
    |> List.tryPick (fun candidate ->
        match candidate with
        | :? JsonValue -> idOf candidate
        | :? JsonObject as o -> str o "href" |> Option.orElse (str o "url")
        | _ -> None
        |> Option.filter (isHttp allowHttp))

let private attachments (allowHttp: bool) (note: JsonObject) : Events.ReplyAttachment list =
    match note["attachment"] with
    | :? JsonArray as items ->
        [ for item in items do
              match item with
              | :? JsonObject as o ->
                  match urlOf allowHttp o["url"] with
                  | Some url ->
                      yield
                          { Type = defaultArg (str o "type") "Document"
                            MediaType = str o "mediaType"
                            Url = url
                            Name = str o "name" |> Option.filter (fun n -> n <> "") }
                  | None -> ()
              | _ -> () ]
    | _ -> []

/// The language of the content: the first `contentMap` key
let private language (note: JsonObject) : string option =
    match note["contentMap"] with
    | :? JsonObject as map -> map |> Seq.tryHead |> Option.map (fun entry -> entry.Key)
    | _ -> None

/// The app's own extension data on a reply (FR-046): the namespaces the app declared on the thread's root, matched
/// by the full namespace against the terms the reply declares, whatever prefix it chose
let private extensionsOf (tx: NpgsqlTransaction) (root: Guid) (context: JsonNode) (note: JsonObject) : Task<JsonNode option> =
    task {
        use command = new NpgsqlCommand("SELECT document->'@context' FROM objects WHERE id = @id", tx.Connection, tx)
        command.Parameters.AddWithValue("id", root) |> ignore
        let! rootContext = command.ExecuteScalarAsync()

        let appNamespaces =
            match rootContext with
            | :? string as json when not (isNull json) -> ActivityStreams.extensionTerms (JsonNode.Parse json) |> List.map snd |> Set.ofList
            | _ -> Set.empty

        let replyTerms = ActivityStreams.extensionTerms context @ ActivityStreams.extensionTerms note["@context"]

        let found =
            replyTerms
            |> List.tryPick (fun (prefix, ns) ->
                if appNamespaces.Contains ns then
                    let data = JsonObject()

                    for entry in note do
                        if entry.Key.StartsWith(prefix + ":", StringComparison.Ordinal) then
                            data[entry.Key.Substring(prefix.Length + 1)] <- if isNull entry.Value then null else entry.Value.DeepClone()

                    if data.Count = 0 then
                        None
                    else
                        let node = JsonObject()
                        node["namespace"] <- JsonValue.Create ns
                        node["prefix"] <- JsonValue.Create prefix
                        node["data"] <- data
                        Some(node :> JsonNode)
                else
                    None)

        return found
    }

let private threadRef (domain: string) (root: Guid) : Events.ThreadRef = { ObjectId = root; Id = Ids.objectId domain root }

/// The reply as the app sees it. `audience` is how it was addressed when it first arrived.
let private replyObject
    (env: Env)
    (note: JsonObject)
    (audience: string)
    (parent: Events.ReplyParent)
    (root: Guid)
    (published: DateTimeOffset)
    (updated: DateTimeOffset option)
    : Events.ReplyObject =
    let allowHttp = env.Remote.AllowPrivateAddresses

    { Id = (str note "id").Value
      Url = urlOf allowHttp note["url"]
      Type = (str note "type").Value
      Content = Sanitizer.sanitizeHtml (defaultArg (str note "content") "")
      Summary = str note "summary" |> Option.map Sanitizer.toText |> Option.filter (fun s -> s <> "")
      Sensitive = flag note "sensitive"
      Language = language note
      Published = published
      Updated = updated
      Audience = audience
      InReplyTo = (idOf note["inReplyTo"]).Value
      Parent = parent
      Thread = threadRef env.Domain root
      Attachments = attachments allowHttp note }

let private audienceOf (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) : Audience.Received =
    let addressed (name: string) = if isNull note[name] then iris activity[name] else iris note[name]
    Audience.fromAddressing signer.FollowersUrl (addressed "to") (addressed "cc")

// ---- Create ----------------------------------------------------------------------------------------------

let handleCreate (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        match str activity "id", idOf activity["actor"] with
        | Some createId, Some actorId when actorId = signer.Id && isOnSignerHost signer createId ->
            match! noteOf env signer activity["object"] with
            | None -> return Ignored "no reply object attributed to the signer"
            | Some note ->
                match str note "id", idOf note["inReplyTo"] with
                | Some replyId, Some parentIri ->
                    use! connection = env.DataSource.OpenConnectionAsync()
                    use! tx = connection.BeginTransactionAsync()

                    match! Threads.resolveParent tx env.Domain parentIri with
                    | Threads.Unknown
                    | Threads.Gone ->
                        do! tx.RollbackAsync()
                        return Ignored "not a reply to one of our objects or to a live reply in one of our threads"
                    | Threads.Resolved parent ->
                        let! fresh = markProcessed tx env.Time createId

                        if not fresh then
                            do! tx.RollbackAsync()
                            return Ignored "already processed"
                        else
                            let now = env.Time.GetUtcNow()
                            let published = time note "published" |> Option.defaultValue now
                            let audience = audienceOf signer activity note
                            let! recorded = Threads.recordRemoteReply tx env.Time replyId signer.Id parent audience published

                            if recorded then
                                let! extensions = extensionsOf tx parent.Root activity["@context"] note

                                let parentRef: Events.ReplyParent =
                                    { Local = parent.LocalUser.IsSome
                                      ObjectId = parent.Object
                                      UserId = parent.LocalUser }

                                let reply = replyObject env note (Audience.receivedName audience) parentRef parent.Root published None
                                let! _ = Outbox.enqueueEvent tx env.Time parent.RootAuthor (Events.replyBody Events.ReplyReceived (actorSummary signer) reply extensions)
                                ()

                            // A reply seen again under another activity id is still only one reply: keep the marker
                            do! tx.CommitAsync()
                            return if recorded then Handled else Ignored "reply already recorded"
                | _ -> return Ignored "not a reply"
        | _ -> return Ignored "the Create is not the signer's own"
    }

// ---- Update and Delete -----------------------------------------------------------------------------------

/// Only a live reply recorded from this signer may be changed by it
let private ownedBy (signer: RemoteActor) (row: Threads.ReplyRow option) : Threads.ReplyRow option =
    row |> Option.filter (fun r -> r.State = "live" && r.RemoteActor = Some signer.Id)

let private parentRef (row: Threads.ReplyRow) : Events.ReplyParent =
    { Local = row.ParentUser.IsSome
      ObjectId = row.ParentObject
      UserId = row.ParentUser }

let private lookup (env: Env) (replyId: string) : Task<Threads.ReplyRow option> =
    task {
        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! row = Threads.findReply tx replyId
        do! tx.RollbackAsync()
        return row
    }

let handleUpdate (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        match str activity "id", idOf activity["actor"], idOf activity["object"] with
        | Some updateId, Some actorId, Some replyId when actorId = signer.Id && isOnSignerHost signer updateId ->
            // Before anything is fetched or locked: is this a reply of the signer's that we recorded?
            let! known = lookup env replyId

            match ownedBy signer known with
            | None -> return Ignored "not a live reply recorded from the signer"
            | Some _ ->
                match! noteOf env signer activity["object"] with
                | Some note when str note "id" = Some replyId ->
                    use! connection = env.DataSource.OpenConnectionAsync()
                    use! tx = connection.BeginTransactionAsync()

                    let! found = Threads.findReply tx replyId

                    match ownedBy signer found with
                    | None ->
                        do! tx.RollbackAsync()
                        return Ignored "the reply was deleted meanwhile"
                    | Some row ->
                        // GoToSocial gives every Update of a status the same activity id (`…/activity#Update`), Mastodon one per
                        // edit: the edit's own `updated` time tells two edits apart, so a second edit is not taken for a repeat
                        let dedupId = match str note "updated" with Some updated -> $"{updateId}|{updated}" | None -> updateId
                        let! fresh = markProcessed tx env.Time dedupId

                        if not fresh then
                            do! tx.RollbackAsync()
                            return Ignored "already processed"
                        else
                            let now = env.Time.GetUtcNow()
                            let updated = time note "updated" |> Option.defaultValue now
                            let reply = replyObject env note row.Audience (parentRef row) row.ThreadRoot row.PublishedAt (Some updated)
                            let! extensions = extensionsOf tx row.ThreadRoot activity["@context"] note
                            do! Threads.markUpdated tx env.Time replyId
                            let! _ = Outbox.enqueueEvent tx env.Time row.RootAuthor (Events.replyBody Events.ReplyUpdated (actorSummary signer) reply extensions)
                            do! tx.CommitAsync()
                            return Handled
                | _ -> return Ignored "the edited reply could not be read from the signer"
        | _ -> return Ignored "the Update is not the signer's own"
    }

let handleDelete (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        match str activity "id", idOf activity["actor"], idOf activity["object"] with
        | Some deleteId, Some actorId, Some replyId when actorId = signer.Id && isOnSignerHost signer deleteId ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            let! found = Threads.findReply tx replyId

            match ownedBy signer found with
            | None ->
                do! tx.RollbackAsync()
                return Ignored "not a live reply recorded from the signer"
            | Some row ->
                let! fresh = markProcessed tx env.Time deleteId

                if not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "already processed"
                else
                    let! _ = Threads.markDeleted tx env.Time replyId
                    let! _ = Outbox.enqueueEvent tx env.Time row.RootAuthor (Events.deletedBody (actorSummary signer) replyId (threadRef env.Domain row.ThreadRoot))
                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Delete is not the signer's own"
    }
