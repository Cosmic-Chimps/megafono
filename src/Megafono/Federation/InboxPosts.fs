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

            let candidates =
                followers
                |> List.filter (fun user -> audience <> Audience.ReceivedDirect || addressed.Contains(Ids.actorId env.Domain user))

            // (US3: the root author of a thread the post replies into is filtered out of `candidates` here, R-53)
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
