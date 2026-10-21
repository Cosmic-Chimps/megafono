/// Replies from the fediverse (feature 002, FR-001 to FR-006, research R-22): `Create{Note|Article}` whose
/// `inReplyTo` is one of the app's objects or a reply already recorded in one of its threads, and the `Update` and
/// `Delete` of such a reply by its author. Each activity is handled in one transaction: the processed marker, the
/// reply record and the app event commit together or not at all. The event goes to the author of the thread's root.
///
/// Trust follows 001 (FR-015): the dispatch in `Inbox.fs` checks that the activity is the signer's own and reads the
/// note once (`InboxNotes.noteOf`: embedded only when its id is on the signer's host, else fetched from its origin,
/// signed, and attributed to the signer); the functions here take the note already read.
/// What is not for us is answered 202 and dropped, so a sender learns nothing about what we know.
module Megafono.Federation.InboxReplies

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon
open Megafono.Federation.InboxNotes

/// The app's own extension data on a reply (FR-046): the namespaces the app declared on the thread's root
let private extensionsOf (tx: NpgsqlTransaction) (root: Guid) (context: JsonNode) (note: JsonObject) : Task<JsonNode option> =
    task {
        use command = new NpgsqlCommand("SELECT document->'@context' FROM objects WHERE id = @id", tx.Connection, tx)
        command.Parameters.AddWithValue("id", root) |> ignore
        let! rootContext = command.ExecuteScalarAsync()

        let appNamespaces =
            match rootContext with
            | :? string as json when not (isNull json) -> ActivityStreams.extensionTerms (JsonNode.Parse json) |> List.map snd |> Set.ofList
            | _ -> Set.empty

        return InboxNotes.extensionData appNamespaces context note
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
    let fields = fieldsOf env.Remote.AllowPrivateAddresses note

    { Id = fields.NoteId
      Url = fields.NoteUrl
      Type = fields.NoteType
      Content = fields.Content
      Summary = fields.Summary
      Sensitive = fields.Sensitive
      Language = fields.Language
      Published = published
      Updated = updated
      Audience = audience
      InReplyTo = (idOf note["inReplyTo"]).Value
      Parent = parent
      Thread = threadRef env.Domain root
      Attachments = fields.Attachments }

/// True when the instance or the author of the thread's root blocks the signer, or the local author of the reply's
/// parent does (FR-006): a block of either keeps the signer out of what they said
let private isBlocked (tx: NpgsqlTransaction) (signer: RemoteActor) (rootAuthor: string) (parentAuthor: string option) : Task<bool> =
    task {
        let! byRoot = Blocks.isBlockedBy tx rootAuthor signer.Id signer.Host

        match parentAuthor with
        | Some author when not byRoot && author <> rootAuthor -> return! Blocks.isBlockedBy tx author signer.Id signer.Host
        | _ -> return byRoot
    }

// ---- Create ----------------------------------------------------------------------------------------------

/// `Create` of a note, read by the dispatch: a reply to one of the app's objects or to a reply of its threads
let createReply (env: Env) (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) : Task<Outcome> =
    task {
        match str activity "id", str note "id", idOf note["inReplyTo"] with
        | Some createId, Some replyId, Some parentIri ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            match! Threads.resolveParent tx env.Domain parentIri with
            | Threads.Unknown
            | Threads.Gone ->
                do! tx.RollbackAsync()
                return Ignored "not a reply to one of our objects or to a live reply in one of our threads"
            | Threads.Resolved parent ->
                // Serialized with the thread author's block call and deletion (R-46), then the blocks are read
                let! live = Outbox.lockLiveActor tx parent.RootAuthor
                let! blocked = if live then isBlocked tx signer parent.RootAuthor parent.LocalUser else Task.FromResult false
                let! fresh = if live && not blocked then markProcessed tx env.Time createId else Task.FromResult true

                if not live || blocked then
                    do! tx.RollbackAsync()
                    return Ignored(if blocked then "blocked" else "the thread's author was deleted")
                elif not fresh then
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
    }

// ---- Update and Delete -----------------------------------------------------------------------------------

/// Only a live reply recorded from this signer may be changed by it
let private ownedBy (signer: RemoteActor) (row: Threads.ReplyRow option) : Threads.ReplyRow option =
    row |> Option.filter (fun r -> r.State = "live" && r.RemoteActor = Some signer.Id)

let private parentRef (row: Threads.ReplyRow) : Events.ReplyParent =
    { Local = row.ParentUser.IsSome
      ObjectId = row.ParentObject
      UserId = row.ParentUser }

/// The live reply the signer wrote and we recorded, looked up before anything is fetched or locked
let lookupReply (env: Env) (signer: RemoteActor) (replyId: string) : Task<Threads.ReplyRow option> =
    task {
        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! row = Threads.findReply tx replyId
        do! tx.RollbackAsync()
        return ownedBy signer row
    }

/// `Update` of a note, read by the dispatch, for a reply `lookupReply` found (`earlier`)
let updateReply (env: Env) (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) (earlier: Threads.ReplyRow) : Task<Outcome> =
    task {
        match str activity "id", str note "id" with
        | Some updateId, Some replyId ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            // The thread author's row first, then the reply's (the order every writer takes them in, R-46)
            let! live = Outbox.lockLiveActor tx earlier.RootAuthor
            let! blocked = if live then isBlocked tx signer earlier.RootAuthor earlier.ParentUser else Task.FromResult false

            if not live || blocked then
                do! tx.RollbackAsync()
                return Ignored(if blocked then "blocked" else "the thread's author was deleted")
            else
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
        | _ -> return Ignored "the Update carries no ids"
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
