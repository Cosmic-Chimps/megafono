/// A followed Group's announces (feature 006, FR-004 to FR-009, research R-65 to R-67): an `Announce` from a remote
/// `Group` that live local users follow becomes `post.received` (a new post), `post.updated` (an `Announce` of an
/// `Update`) or, for `Undo` of the `Announce`, `post.deleted`, each with the Group in `announcedBy` and the post's
/// author in `actor`.
///
/// The announcer is not the author, so nothing in the `Announce` body is trusted beyond the target's id and whether it
/// is new or an edit: the object is read from its origin and accepted only when its `id` is the target, it is a Note or
/// Article attributed to an actor on the target's own host, that actor resolves and is not blocked by the instance,
/// and the object is public or unlisted (read from the object's own `to`/`cc`, never from the `Announce`'s). The cheap
/// checks that need no request (the activity is the signer's own, the signer is a `Group` some live user follows, the
/// id is new) come first, so an `Announce` is not a way to make this server fetch things.
///
/// Each activity is handled in one transaction: the users' rows are locked as in 005 (`FeedPosts.lockUsers`, R-57),
/// then follows and blocks are read, and the `feed_posts` rows, the processed marker and the app events commit together.
module Megafono.Federation.InboxAnnounces

open System
open System.Text.Json.Nodes
open System.Threading.Tasks
open Npgsql
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon
open Megafono.Federation.InboxNotes

/// What an `Announce` is about: a post new to the follower, or an edit of one already passed on
type private Kind =
    | NewPost
    | Edit

type private Target = { Iri: string; Kind: Kind }

/// Activity types an `Announce` may wrap that are never a post (a `Delete`, a `Like`...): ignored, not fetched
let private activityTypes =
    set [ "Delete"; "Undo"; "Announce"; "Like"; "Dislike"; "Follow"; "Accept"; "Reject"; "Block"; "Add"; "Remove"; "Flag"; "Move"; "Offer"; "Invite" ]

/// An embedded object whose type is known and is not a Note or Article is not worth a fetch
let private plausiblePost (node: JsonNode) : bool =
    match node with
    | :? JsonObject as o ->
        match str o "type" with
        | Some("Note" | "Article") | None -> true
        | _ -> false
    | _ -> true

/// The target of an announced object: an IRI, an object with an id that is not an activity, or the inner object of a
/// `Create` (Lemmy's shape, R-65) is a new post; the inner object of an `Update` is an edit; anything else is ignored
let private targetOf (node: JsonNode) : Target option =
    let target (kind: Kind) (inner: JsonNode) =
        if plausiblePost inner then idOf inner |> Option.map (fun iri -> { Iri = iri; Kind = kind }) else None

    match node with
    | :? JsonValue -> target NewPost node
    | :? JsonObject as o ->
        match str o "type" with
        | Some "Create" -> target NewPost o["object"]
        | Some "Update" -> target Edit o["object"]
        | Some kind when activityTypes.Contains kind -> None
        | _ -> if plausiblePost o then target NewPost o else None
    | _ -> None

/// True when the IRI is on this instance's own domain: those objects are the app's (R-65)
let private isOwn (env: Env) (iri: string) : bool =
    match Uri.TryCreate(iri, UriKind.Absolute) with
    | true, uri -> authority uri = env.Domain.ToLowerInvariant()
    | _ -> false

let private scalarExists (tx: NpgsqlTransaction) (sql: string) (parameters: (string * obj) list) : Task<bool> =
    task {
        use c = new NpgsqlCommand(sql, tx.Connection, tx)

        for name, value in parameters do
            c.Parameters.AddWithValue(name, value) |> ignore

        let! found = c.ExecuteScalarAsync()
        return not (isNull found)
    }

/// What is known without a request: whether an Announce of this kind is worth a fetch (R-65). No lock, nothing written.
let private worthFetching (env: Env) (signer: RemoteActor) (announceId: string) (target: Target) : Task<bool> =
    task {
        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()

        let! processed = scalarExists tx "SELECT 1 FROM processed_activities WHERE activity_id = @id" [ "id", box announceId ]
        let! followers = FeedPosts.followerCandidates tx signer.Id

        let! hasRows =
            match target.Kind with
            | NewPost -> Task.FromResult true
            | Edit ->
                task {
                    let! rows = FeedPosts.announcedRows tx target.Iri signer.Id
                    return not rows.IsEmpty
                }

        do! tx.RollbackAsync()
        return not processed && not followers.IsEmpty && hasRows
    }

/// The announced note read from its origin, and its author, once the trust checks of R-65 pass
type private Announced = { Note: JsonObject; Author: RemoteActor; Audience: Audience.Received }

let private read (env: Env) (target: string) : Task<Result<Announced, string>> =
    task {
        match! RemoteActors.fetchObject env.Remote target with
        | Error e -> return Error $"the object could not be read from its origin: {e}"
        | Ok note when str note "id" <> Some target -> return Error "the origin serves an object with another id"
        | Ok note when (match str note "type" with Some("Note" | "Article") -> false | _ -> true) -> return Error "the object is not a Note or Article"
        | Ok note ->
            match idOf note["attributedTo"] with
            | None -> return Error "the object has no single author"
            | Some authorId ->
                match Uri.TryCreate(authorId, UriKind.Absolute), Uri.TryCreate(target, UriKind.Absolute) with
                | (true, authorUri), (true, targetUri) when authority authorUri = authority targetUri ->
                    match! RemoteActors.getActor env.Remote authorId with
                    | Error e -> return Error $"the author could not be resolved: {e}"
                    | Ok author when author.Id <> authorId || author.Host <> authority targetUri -> return Error "the author is not an actor of the object's host"
                    | Ok author ->
                        let! blocked = Blocks.isInstanceBlocked env.DataSource author.Id author.Host
                        // The object's own addressing: never the Announce's, which is the Group's
                        let audience = Audience.fromAddressing author.FollowersUrl (iris note["to"]) (iris note["cc"])

                        if blocked then
                            return Error "the author is blocked by the instance"
                        elif not (Audience.isListable audience) then
                            return Error "the object is not public or unlisted"
                        else
                            return Ok { Note = note; Author = author; Audience = audience }
                | _ -> return Error "the object and its author are not on the same host"
    }

/// `post.received` for the users who follow the Group (R-66), except the root author of a thread of the app's the post
/// replies into (R-53)
let private passOn (env: Env) (signer: RemoteActor) (announceId: string) (announced: Announced) : Task<Outcome> =
    task {
        let note = announced.Note
        let author = announced.Author
        let postId = (str note "id").Value

        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()
        let! followers = FeedPosts.followerCandidates tx signer.Id

        let! rootAuthor =
            task {
                match idOf note["inReplyTo"] with
                | Some parentIri ->
                    match! Threads.resolveParent tx env.Domain parentIri with
                    | Threads.Resolved parent -> return Some parent.RootAuthor
                    | _ -> return None
                | None -> return None
            }

        let candidates = followers |> List.filter (fun user -> Some user <> rootAuthor)

        if candidates.IsEmpty then
            do! tx.RollbackAsync()
            return Ignored "no local user follows the Group (or only the thread's author does)"
        else
            // Serialized with each user's block call and deletion (R-57); then follows and blocks are read afresh
            let! locked = FeedPosts.lockUsers tx candidates
            let! users = FeedPosts.qualifyingAnnounced tx locked signer.Id signer.Host author.Id author.Host true false
            let! fresh = markProcessed tx env.Time announceId

            if not fresh then
                do! tx.RollbackAsync()
                return Ignored "already processed"
            else
                let now = env.Time.GetUtcNow()
                let published = time note "published" |> Option.defaultValue now
                let! inserted = FeedPosts.insertRows tx postId author.Id (Audience.receivedName announced.Audience) published (Some signer.Id) users
                // The marker commits even when nobody is left to tell: a repeat of this Announce costs nothing
                if not inserted.IsEmpty then
                    // The Announce's own `@context` is the Group's: only the note's, from its origin, names the app's terms
                    let! extensions = InboxPosts.extensionsOf tx null note
                    let post = InboxPosts.postObject env note (Audience.receivedName announced.Audience) published None

                    for user in inserted do
                        // A JSON node has one parent: each event gets its own copy of the extension data
                        let own = extensions |> Option.map (fun e -> e.DeepClone())
                        let! _ = Outbox.enqueueEvent tx env.Time user (Events.postBody Events.PostReceived (actorSummary author) post (Some(actorSummary signer)) own)
                        ()

                do! tx.CommitAsync()
                return if inserted.IsEmpty then Ignored "post already passed on, or every follower blocks the Group or the author" else Handled
    }

/// `post.updated` for the users who received the post through this Group, still follow it, block neither it nor the
/// author, and do not follow the author (they hear the author's own edit, R-67). The audience and `published` are the
/// ones the post arrived with.
let private passOnEdit (env: Env) (signer: RemoteActor) (announceId: string) (announced: Announced) : Task<Outcome> =
    task {
        let note = announced.Note
        let author = announced.Author
        let postId = (str note "id").Value

        use! connection = env.DataSource.OpenConnectionAsync()
        use! tx = connection.BeginTransactionAsync()

        // Lock the users the post went to, then read the rows again: a deletion that committed first is seen (R-57)
        let! first = FeedPosts.announcedRows tx postId signer.Id
        let! locked = FeedPosts.lockUsers tx (first |> List.map (fun r -> r.LocalUser))
        let! live = FeedPosts.announcedRows tx postId signer.Id
        let rows = live |> List.filter (fun r -> List.contains r.LocalUser locked)

        if rows.IsEmpty then
            do! tx.RollbackAsync()
            return Ignored "the post was retracted meanwhile"
        else
            let! fresh = markProcessed tx env.Time announceId

            if not fresh then
                do! tx.RollbackAsync()
                return Ignored "already processed"
            else
                let! users = FeedPosts.qualifyingAnnounced tx (rows |> List.map (fun r -> r.LocalUser)) signer.Id signer.Host author.Id author.Host true true
                let updated = time note "updated" |> Option.defaultValue (env.Time.GetUtcNow())
                do! FeedPosts.markUpdated tx env.Time postId author.Id
                let! extensions = InboxPosts.extensionsOf tx null note

                for row in rows |> List.filter (fun r -> List.contains r.LocalUser users) do
                    let post = InboxPosts.postObject env note row.Audience row.PublishedAt (Some updated)
                    let own = extensions |> Option.map (fun e -> e.DeepClone())
                    let! _ = Outbox.enqueueEvent tx env.Time row.LocalUser (Events.postBody Events.PostUpdated (actorSummary author) post (Some(actorSummary signer)) own)
                    ()

                do! tx.CommitAsync()
                return if users.IsEmpty then Ignored "no user is left to hear of the edit" else Handled
    }

/// `Announce` by a remote Group: the activity must be the signer's own and the signer a `Group`; then the target is
/// read from its origin only if the cheap checks say some user could hear of it (R-65)
let handleAnnounce (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        match str activity "id", idOf activity["actor"], targetOf activity["object"] with
        | Some announceId, Some actorId, Some target when actorId = signer.Id && isOnSignerHost signer announceId ->
            if signer.ActorType <> "Group" then
                return Ignored "the Announce is not from a Group"
            elif isOwn env target.Iri then
                return Ignored "the announced object is one of this instance's own"
            else
                match! worthFetching env signer announceId target with
                | false -> return Ignored "nobody to tell, or an Announce seen before"
                | true ->
                    match! read env target.Iri with
                    | Error reason -> return Ignored reason
                    | Ok announced ->
                        match target.Kind with
                        | NewPost -> return! passOn env signer announceId announced
                        | Edit -> return! passOnEdit env signer announceId announced
        | Some _, Some actorId, _ when actorId = signer.Id -> return Ignored "the Announce names no post"
        | _ -> return Ignored "the Announce is not the signer's own"
    }

// ---- Undo of an Announce ---------------------------------------------------------------------------------

/// The live rows this Group announced for the post, as (user, author) pairs sorted by user
let private announcedAuthors (tx: NpgsqlTransaction) (postId: string) (group: string) : Task<(string * string) list> =
    task {
        use c =
            new NpgsqlCommand(
                """SELECT local_actor, remote_actor FROM feed_posts
                   WHERE id = @id AND announced_by = @group AND state = 'live' ORDER BY local_actor""",
                tx.Connection,
                tx
            )

        c.Parameters.AddWithValue("id", postId) |> ignore
        c.Parameters.AddWithValue("group", group) |> ignore
        use! reader = c.ExecuteReaderAsync()
        let pairs = ResizeArray<string * string>()

        while reader.Read() do
            pairs.Add((reader.GetString 0, reader.GetString 1))

        return List.ofSeq pairs
    }

/// `Undo` of the Group's own `Announce` (embedded, with its `object`): the live rows it announced for the post turn
/// `deleted` and `post.deleted` goes to their users who block neither the Group nor the author. Nothing is fetched:
/// the Group is authoritative about its own announce (R-63, R-67).
let handleUndo (env: Env) (signer: RemoteActor) (activity: JsonNode) : Task<Outcome> =
    task {
        let announce = activity["object"]

        match str activity "id", idOf activity["actor"], idOf announce["actor"], targetOf announce["object"] with
        | Some undoId, Some actorId, Some announcer, Some target when
            actorId = signer.Id && announcer = signer.Id && isOnSignerHost signer undoId && target.Kind = NewPost
            ->
            use! connection = env.DataSource.OpenConnectionAsync()
            use! tx = connection.BeginTransactionAsync()
            let! first = announcedAuthors tx target.Iri signer.Id

            if first.IsEmpty then
                do! tx.RollbackAsync()
                return Ignored "not a live post this Group announced"
            else
                let! locked = FeedPosts.lockUsers tx (first |> List.map fst)
                let! rows = announcedAuthors tx target.Iri signer.Id
                let! fresh = markProcessed tx env.Time undoId

                if rows.IsEmpty || not fresh then
                    do! tx.RollbackAsync()
                    return Ignored "the post was retracted meanwhile, or this Undo was seen before"
                else
                    // Who is blocking is read before the rows change; the rows of a blocking or deleted user turn `deleted` too
                    let heard = ResizeArray<string * RemoteActor>()

                    for authorId, pairs in rows |> List.filter (fun (user, _) -> List.contains user locked) |> List.groupBy snd do
                        match! loadRemote tx authorId with
                        | Some author ->
                            let! hearing =
                                FeedPosts.qualifyingAnnounced tx (pairs |> List.map fst) signer.Id signer.Host author.Id author.Host false false

                            for user in hearing do
                                heard.Add((user, author))
                        | None -> ()

                    let! deleted = FeedPosts.markDeletedAnnounced tx env.Time target.Iri signer.Id

                    for user, author in heard |> Seq.filter (fun (user, _) -> List.contains user deleted) do
                        let! _ = Outbox.enqueueEvent tx env.Time user (Events.postDeletedBody (actorSummary author) target.Iri (Some(actorSummary signer)))
                        ()

                    do! tx.CommitAsync()
                    return Handled
        | _ -> return Ignored "the Undo is not the signer's own Announce undone"
    }