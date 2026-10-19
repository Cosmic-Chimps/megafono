# Research: Posts from followed accounts (the app's feed)

Numbering continues after 004 (R-40 to R-50). Decisions taken in `/speckit-clarify` (spec, Clarifications, session
2026-10-18) are marked "(clarified)". No NEEDS CLARIFICATION remains in the Technical Context.

## R-51 One event per following user (clarified)

- **Decision**: a post produces one `post.received` per local user it goes to, in that user's event stream (`user`,
  per-user `seq`), with the same `object` and `extensions` in each. `post.updated` and `post.deleted` likewise.
- **Rationale**: every event of contract version 1 belongs to one user: the envelope's `user` is required and `seq`
  orders one user's events (001 FR-057). One event listing users would need a user-less envelope, a breaking change
  under Principle I, and one user's failing handler would retry the whole list. Per-user events tie a followers-only
  post to exactly the users who follow its author. `Outbox.enqueueEvent` already queues nothing for a deleted user (003).
- **Cost**: a post followed by N local users queues N events in one transaction (two statements each). At the scale
  reference (a few hundred local followers of one account at most) that is well under a second; not optimized further
  (Principle VII).
- **Alternatives considered**: one event with a `users` list (declined in clarify).

## R-52 Which posts are feed posts, and for whom

- **Decision**: a `Create` whose activity is the signer's own (actor = signer, activity id on the signer's host) and
  whose object is a `Note` or `Article` attributed to the signer, embedded when on the signer's host, else fetched
  signed from its origin (001 FR-015, exactly as 002's `noteOf`). Its audience is read with 002's
  `Audience.fromAddressing` (the signer's followers URL, `to`/`cc` of the object, else of the activity). Recipients are
  the live local users with an `accepted` outbound follow of the signer who do not block it, and:
  - `public`, `unlisted`, `followers`: all of them;
  - `direct`: only those whose actor id (`https://{domain}/ap/actors/{id}`) appears in the post's `to` or `cc` (or the
    activity's, when the object has none).
  Replies by followed accounts to anyone's posts are feed posts too, with `inReplyTo` passed on (clarified).
- **Rationale**: the inbox a post arrived on says nothing about who may read it (a shared inbox serves every local
  user), so recipients come only from the follow relationships and, for `direct`, from the addressing (FR-002,
  Principle II: fail closed). A direct post addressed to a user who does not follow the author is a mention, left to the
  mentions task. Pending, rejected and removed follows do not count.
- **Alternatives considered**: the personal inbox's owner as recipient (a shared-inbox delivery has no owner; a
  personal one would fan out wrongly when servers deliver once per server); passing direct posts to every follower
  (leaks a private message).

## R-53 A reply into one of the app's threads (FR-003)

- **Decision**: when the post's `inReplyTo` resolves (`Threads.resolveParent`) to a live object or reply of one of the
  app's threads, the thread's root author is left out of the feed recipients; 002's reply path handles it as today
  (`reply.received`, `reply.updated`, `object.deleted` to the root author). Every other recipient gets the feed events.
  Because the root author never gets a `feed_posts` row for that post, later edits and deletions route the same way
  without another check.
- **Rationale**: no user receives two events of different types about one post (FR-003), and 002's events keep their
  exact meaning (SC-005). The root author is the only user 002 tells, whatever the depth of the reply. When the parent
  does not resolve (unknown, or `Gone`), the post is an ordinary feed post for everyone who follows its author.
- **Alternatives considered**: both events for the root author (rejected in specify: "two conflicting events");
  feed events instead of reply events for followers of the replier (changes 002).

## R-54 One read of the object, two independent paths

- **Decision**: the inbox dispatch of `Create`, `Update{Note|Article}` and `Delete` checks the activity's own
  preconditions once, reads the object once and hands it to both paths: 002's reply handling and the new feed
  handling. Each path keeps its own transaction and its own idempotency, so either can act when the other ignores
  the activity. The combined outcome is `Handled` when either path handled it, else `Ignored` with both reasons; the
  HTTP answer stays 202 in every case. For `Update`, nothing is fetched unless the object is a recorded reply or has
  live feed rows by the signer.
- **Rationale**: today `markProcessed` on the activity id belongs to the reply path; a shared marker would make the
  second path see the first one's marker as a repeat. Separate transactions keep 002's lock order and failure modes
  untouched. Reading the object once avoids a second signed fetch of the same post.
- **Alternatives considered**: one combined transaction (couples two features' locks); calling the two handlers
  unchanged one after the other (double fetch, marker clash).

## R-55 Idempotency by object id (FR-004, FR-005, FR-006)

- **Decision**:
  - `Create`: one `feed_posts` row per (post id, local user), inserted `ON CONFLICT DO NOTHING RETURNING
    local_actor`; only the users returned get `post.received`. No processed marker.
  - `Delete`: only rows still `live` turn `deleted`, and only those users can get `post.deleted`. No marker.
  - `Update`: a processed marker `post|{updateId}|{updated}` (`post|{updateId}` when the note has no `updated`); the
    `post|` prefix keeps it apart from 002's `{updateId}|{updated}` (R-54), and the `updated` part tells GoToSocial's
    reused `…#Update` activity ids apart (002 R-25 behaviour). A repeat of the same edit inserts nothing and sends
    nothing.
- **Rationale**: the rows themselves say who already got a post, which also covers the same post arriving under two
  activity ids (shared and personal inbox). Edits have no such state, so they need the marker, which expires after 7
  days like every other (001 FR-014).

## R-56 Edits and deletions after the follow changed (clarified)

- **Decision**: `post.updated` goes to the users with a live row of the post who still have an `accepted` outbound
  follow of the author and do not block it. `post.deleted` goes to every user with a live row who does not block the
  author, whatever the follow is now; all live rows turn `deleted`, also those of users who block the author (they get
  no event). Deleted local users get nothing (`enqueueEvent`).
- **Rationale**: an edit is new content from an account the user no longer follows; a deletion only removes content
  the app holds (clarified Q4). A blocked author's deletion is not passed on, keeping 004's "the app hears nothing from
  a blocked actor".

## R-57 Locks and blocks

- **Decision**: in each feed transaction, lock the candidate users' `local_actors` rows `ORDER BY app_user_id FOR NO
  KEY UPDATE` (live users only), then, in a separate statement, read which of them still qualify (follow state as R-52
  or R-56, `feed_posts` rows, not covered by `Blocks.coveredSql` with the user, the signer id and its host). Events are
  queued in the same sorted order.
- **Rationale**: the lock serializes with each user's block call and deletion, as `lockLiveActor` does for one user
  (004 R-46, 003 R-34): a block committed before the lock is seen by the second statement (a fresh snapshot in READ
  COMMITTED); a block starting later waits. Every other writer locks one local actor, or several in `app_user_id`
  order (the instance block in `Internal/Blocks.fs`: `… FROM local_actors … ORDER BY app_user_id FOR UPDATE`), so
  sorted locking cannot deadlock. `enqueueEvent` bumps rows already held. `FOR NO KEY UPDATE` is the mode
  `enqueueEvent`'s `UPDATE` takes, avoiding the shared-lock upgrade deadlock 004 found.
- **Alternatives considered**: `lockLiveActor` per user in arbitrary order (deadlocks with a concurrent post to an
  overlapping set of users); no lock (an event could follow a block the app was already told about).

## R-58 The app's extension namespaces (FR-008)

- **Decision**: a table `extension_namespaces (namespace PRIMARY KEY, first_used_at)`, written in the publish and update
  transactions of `Internal/Objects.fs` whenever the request carries `extensions`, and filled once from the stored
  objects when the table is first created (the `@context` object terms with a string value, Megáfono's own terms left
  out, as `ActivityStreams.extensionTerms` does). A feed post's terms (`extensionTerms` of the activity's and the
  object's `@context`) are matched against it by the full namespace, and the data under the post's own prefix is passed
  on in the same `{ namespace, prefix, data }` shape as 002's replies.
- **Rationale**: a reply's namespaces come from its thread's root, but a feed post has no root of ours. The namespaces
  the app has published with are its own by definition; a small keyed table makes the check one indexed lookup instead
  of a scan of every stored object's JSON per inbound post. No operator setting (Principle VII, SC-001 of the vision).
  The backfill is guarded by the table's absence so later starts do not rescan `objects`.
- **Limitation**: an app that never published with `extensions` gets `extensions: null` on every feed post; stated in
  the contract.
- **Alternatives considered**: an operator setting listing namespaces (an extra step); scanning `objects` per post (the
  largest table, on the hot inbound path).

## R-59 Boosts (clarified)

- **Decision**: `Announce` stays an unhandled type: 202, dropped, logged as "activity type not handled". The limitation
  is stated in `docs/operating.md` and the app contract.
- **Rationale**: boosts of remote posts are a v1 non-goal (vision, constitution scope list).

## R-60 What real servers send (confirmed by the interop task)

- **Expected**: GoToSocial 0.22.2 and Mastodon 4.7.3 deliver a followed account's posts to the follower server's shared
  inbox (Megáfono's actors publish `endpoints.sharedInbox`), Note embedded and attributed to the signer. Followers-only
  posts are addressed `to` the author's followers collection. Direct posts are addressed `to` the mentioned actors and
  delivered to their personal inboxes or the shared inbox. `Update{Note}`: Mastodon a new activity id per edit with
  `updated` set, GoToSocial a reused `…#Update`-style id with `updated` set (as 002 observed for replies). `Delete`:
  Mastodon a `Tombstone` object, GoToSocial the IRI.
- **Observed**: _filled in by the interop task, with captured payloads added to `tests/Megafono.Tests/Fixtures/payloads`._
