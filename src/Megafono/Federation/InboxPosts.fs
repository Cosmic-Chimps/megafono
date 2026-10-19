/// Posts from followed accounts (feature 005, FR-001 to FR-012, research R-51 to R-58): a `Create{Note|Article}` by a
/// remote account local users follow becomes one `post.received` per qualifying user. Recipients come from the
/// accepted follows of the signer and, for a `direct` post, from the addressing; never from the inbox the request hit,
/// since a shared inbox serves every local user. The Create is handled in one transaction: the users' rows are locked,
/// the follows and blocks are read, a `feed_posts` row per user and the app events commit together or not at all.
///
/// The dispatch in `Inbox.fs` checks that the activity is the signer's own and reads the note once; this module takes
/// the note already read, and runs next to the reply path (`InboxReplies`), each in a transaction of its own.
module Megafono.Federation.InboxPosts

open System.Text.Json.Nodes
open System.Threading.Tasks
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon
open Megafono.Federation.InboxNotes

/// The post as the app sees it. `audience` is how it was addressed when it arrived.
let private postObject
    (env: Env)
    (note: JsonObject)
    (audience: string)
    (published: System.DateTimeOffset)
    (updated: System.DateTimeOffset option)
    : Events.PostObject =
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
      InReplyTo = idOf note["inReplyTo"]
      Attachments = fields.Attachments }

/// The app's own extension data on a post (FR-008): the namespaces the post declares that the app has published with
let private extensionsOf (tx: Npgsql.NpgsqlTransaction) (context: JsonNode) (note: JsonObject) : Task<JsonNode option> =
    task {
        let declared = ActivityStreams.extensionTerms context @ ActivityStreams.extensionTerms note["@context"] |> List.map snd |> List.distinct
        let! known = FeedPosts.knownNamespaces tx declared
        return InboxNotes.extensionData known context note
    }

/// `Create` of a note, read by the dispatch: tells each local user who follows the signer (accepted) and is not
/// blocking it, once per post id whatever the activity or inbox that brought it (R-55)
let createPost (env: Env) (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) : Task<Outcome> =
    task {
        match str note "id" with
        | None -> return Ignored "the note has no id"
        | Some postId ->
            let audience = audienceOf signer activity note
            let addressed = addressedTo activity note |> Set.ofList

            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            // Who may hear of it: the signer's followers; for a direct post only those it is addressed to (R-52)
            let! followers = FeedPosts.followerCandidates tx signer.Id

            // A reply into one of the app's threads reaches the thread's author through 002's reply events, never as a
            // feed post: that user is left out (R-53). A parent that is unknown or deleted leaves everyone in.
            let! rootAuthor =
                task {
                    match idOf note["inReplyTo"] with
                    | Some parentIri ->
                        match! Threads.resolveParent tx env.Domain parentIri with
                        | Threads.Resolved parent -> return Some parent.RootAuthor
                        | _ -> return None
                    | None -> return None
                }

            let candidates =
                followers
                |> List.filter (fun user -> Some user <> rootAuthor)
                |> List.filter (fun user -> audience <> Audience.ReceivedDirect || addressed.Contains(Ids.actorId env.Domain user))

            if candidates.IsEmpty then
                do! tx.RollbackAsync()
                return Ignored "no local user follows the author (or the direct post is not for a follower)"
            else
                // Serialized with each user's block call and deletion (R-57); then follows and blocks are read afresh
                let! locked = FeedPosts.lockUsers tx candidates
                let! users = FeedPosts.qualifying tx locked signer.Id signer.Host true

                let now = env.Time.GetUtcNow()
                let published = time note "published" |> Option.defaultValue now
                let! inserted = FeedPosts.insertRows tx postId signer.Id (Audience.receivedName audience) published users

                if inserted.IsEmpty then
                    do! tx.RollbackAsync()
                    return Ignored "post already passed on, or every follower blocks the author"
                else
                    let! extensions = extensionsOf tx activity["@context"] note
                    let post = postObject env note (Audience.receivedName audience) published None

                    for user in inserted do
                        // A JSON node has one parent: each event gets its own copy of the extension data
                        let own = extensions |> Option.map (fun e -> e.DeepClone())
                        let! _ = Outbox.enqueueEvent tx env.Time user (Events.postBody Events.PostReceived (actorSummary signer) post own)
                        ()

                    do! tx.CommitAsync()
                    return Handled
    }

// ---- Update and Delete -----------------------------------------------------------------------------------

/// True when the signer has a live post of this id that was passed on to some user. No lock, nothing fetched: the
/// dispatch asks this before it reads the edited note (R-54).
let lookupPost (env: Env) (signer: RemoteActor) (postId: string) : Task<bool> =
    task {
        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! rows = FeedPosts.liveRows tx postId signer.Id
        do! tx.RollbackAsync()
        return not rows.IsEmpty
    }

/// `Update` of a note, read by the dispatch, for a post `lookupPost` found: `post.updated` to the users who got the post,
/// still follow the signer and do not block it (R-56). The audience and `published` are those the post arrived with.
let updatePost (env: Env) (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) : Task<Outcome> =
    task {
        match str activity "id", str note "id" with
        | Some updateId, Some postId ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            // Lock the users the post went to, then read the rows again: a deletion that committed first is seen (R-57)
            let! first = FeedPosts.liveRows tx postId signer.Id
            let! locked = FeedPosts.lockUsers tx (first |> List.map (fun r -> r.LocalUser))
            let! live = FeedPosts.liveRows tx postId signer.Id
            let rows = live |> List.filter (fun r -> List.contains r.LocalUser locked)

            if rows.IsEmpty then
                do! tx.RollbackAsync()
                return Ignored "the post was deleted meanwhile"
            else
                // GoToSocial reuses one activity id for every Update of a status: the edit's own `updated` tells two edits
                // apart. The `post|` prefix keeps this marker apart from the reply path's (R-55).
                let dedupId =
                    match str note "updated" with
                    | Some updated -> $"post|{updateId}|{updated}"
                    | None -> $"post|{updateId}"

                let! fresh = markProcessed tx env.Time dedupId

                if not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "already processed"
                else
                    do! FeedPosts.markUpdated tx env.Time postId signer.Id
                    let! users = FeedPosts.qualifying tx (rows |> List.map (fun r -> r.LocalUser)) signer.Id signer.Host true
                    let updated = time note "updated" |> Option.defaultValue (env.Time.GetUtcNow())
                    let! extensions = extensionsOf tx activity["@context"] note

                    for row in rows |> List.filter (fun r -> List.contains r.LocalUser users) do
                        let post = postObject env note row.Audience row.PublishedAt (Some updated)
                        let own = extensions |> Option.map (fun e -> e.DeepClone())
                        let! _ = Outbox.enqueueEvent tx env.Time row.LocalUser (Events.postBody Events.PostUpdated (actorSummary signer) post own)
                        ()

                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Update carries no ids"
    }

/// `Delete` of a post by its author, by id or as a Tombstone: every live row turns `deleted`, and `post.deleted` goes to
/// the users who do not block the author, whether they still follow it or not (R-56). Nothing is fetched.
let deletePost (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        match idOf activity["object"] with
        | None -> return Ignored "the Delete names no object"
        | Some postId ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()

            let! first = FeedPosts.liveRows tx postId signer.Id

            if first.IsEmpty then
                do! tx.RollbackAsync()
                return Ignored "not a live post recorded from the signer"
            else
                let! locked = FeedPosts.lockUsers tx (first |> List.map (fun r -> r.LocalUser))
                // Who is blocking is read before the rows change; the rows of a blocking or deleted user turn `deleted` too
                let! hearing = FeedPosts.qualifying tx locked signer.Id signer.Host false
                let! deleted = FeedPosts.markDeleted tx env.Time postId signer.Id

                if deleted.IsEmpty then
                    do! tx.RollbackAsync()
                    return Ignored "the post was deleted meanwhile"
                else
                    for user in deleted |> List.filter (fun u -> List.contains u hearing) do
                        let! _ = Outbox.enqueueEvent tx env.Time user (Events.postDeletedBody (actorSummary signer) postId)
                        ()

                    do! tx.CommitAsync()
                    return Handled
    }
