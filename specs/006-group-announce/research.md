# Research: Group actors announce the replies they receive

Numbering continues after 005 (R-51 to R-60). The decisions marked "(spec)" were taken in the spec's Clarifications
from the task's text. R-70 is filled in by the interop task.

## R-61 The opt-in: `announcesReplies` on the actor lookup, Group only (spec)

- **Decision**: a new optional boolean in the app's actor lookup answer, `announcesReplies` (absent or `null` is
  `false`), stored in `local_actors.announces_replies` and refreshed with the profile like `discoverable` and
  `indexable`. `true` is accepted only when `type` is `Group`; otherwise `parseProfile` answers the same kind of
  error as a bad `type` ("'announcesReplies' is only for Group actors"), so the app learns at once (the lookup fails
  and is logged) instead of a silent no-op. It is not part of the actor document, so a change does not send an actor
  `Update` (`profileDiffers` leaves it out).
- **Rationale**: the task asks for an opt-in per actor or per app; per actor through the lookup is the pattern the
  contract already has, needs no new call and no operator setting (Principle VII), and lets one app run plain Groups
  next to announcing ones. FEP-1b12 is a Group behaviour; a Person boosting every reply it gets is not a thing other
  servers expect.
- **Alternatives considered**: an operator setting (`Megafono__…`): all or nothing, and the operator is not the app;
  an internal API call: a new endpoint for one boolean the lookup can carry.

## R-62 What the Group announces, and the wire shape

- **Decision**: for every reply recorded in a thread whose root the announcing Group published, however deep, whose
  audience as received is `public` or `unlisted` (`Audience.isListable`): one `Announce` with its own id
  (`https://{domain}/ap/activities/{guid}`), `actor` the Group, `object` the reply's **id as a string**, `published`,
  and the addressing of a boost of that audience: public → `to: [Public]`, `cc: [groupFollowers, author]`; unlisted →
  `to: [groupFollowers]`, `cc: [Public, author]`. Stored in `activities` (`activity_type = 'Announce'`, `audience` the
  reply's, `object` NULL since the reply is not one of our objects), delivered through `Outbox.enqueueDeliveries` to
  the Group's accepted followers' inboxes (shared where there is one, one request per server) plus, for a remote reply,
  the author's own inbox (`remote_actors.inbox`). The Group's own replies (root author = reply author) are not
  announced; followers-only and direct replies are not announced.
- **Rationale**: `Announce{IRI}` is exactly Mastodon's and GoToSocial's boost, what they produce and consume; every
  receiver fetches the reply from its origin, which is also what Principle III asks of a Megáfono receiver. Sending it
  to the author's inbox is what Mastodon does for a boost: the author's server records who boosted, notifies the
  author, and from then on delivers the author's edits and deletions to the boosting server (Mastodon's
  `StatusReachFinder` includes the servers that reblogged; GoToSocial, it turned out, does not: R-70), which is how a
  Mastodon reply's edits keep reaching Megáfono even when nobody here follows the author. Followers-only and direct replies were addressed
  to specific people: boosting them would widen the audience the author chose (Principle II); Mastodon refuses to
  boost private posts anyway.
- **Alternatives considered**: FEP-1b12's `Announce{Create{Note}}` with the Create embedded: Lemmy's shape, but
  Mastodon resolves it by fetching the Create's id (served by Mastodon, not by GoToSocial) and GoToSocial's support is
  unverified; the plain boost is understood everywhere. Embedding the Note: a copy of remote content signed by us.

## R-63 Edits and deletions: `Announce{Update}` and `Undo{Announce}` (spec)

- **Decision**: when an announced reply is edited (002's `reply.updated` path, or the app's `PUT` of a local reply),
  the Group sends a new `Announce` whose `object` is a minimal `Update`: `{ id: <the edit's activity id>, type:
  "Update", actor: <the author>, object: <the reply's id> }`, addressed and delivered as R-62 (followers' servers and
  the remote author's inbox). When the reply is deleted (002's `object.deleted` path, or the app's `DELETE`), the Group
  sends `Undo` with the original `Announce` embedded (`id`, `type`, `actor`, `object`), addressed the same, delivered
  the same. Both are stored in `activities` (`'Announce'` / `'Undo'`). The reply's row remembers the Create-announce's
  activity id (`replies.announce_activity`); a reply without one is never followed up.
- **Rationale**: the task asks for the edit and the deletion to follow. For edits, FEP-1b12's `Announce{Update}` is the
  shape group-aware software reads, and a Megáfono receiver turns it into `post.updated` after re-reading the reply
  from its origin (so the embedded `Update` is only a pointer; nothing in it is trusted). Mastodon and GoToSocial
  receiving it resolve the `Update`'s id to the status they already hold and find the boost they already have: a
  no-op, while their copy is kept current by the author's own `Update` (R-62). For deletions, `Undo{Announce}` is the
  retraction every server understands: Mastodon and GoToSocial followers drop the boost from their timelines (the
  status itself is removed by the author's `Delete`, which reaches them when they follow the author, and otherwise
  turns 410 on refetch, as for any deleted status), and a Megáfono follower turns it into `post.deleted` with nothing
  to fetch: the Group is authoritative about its own announce. An `Announce{Delete}` would be ignored by Mastodon and
  GoToSocial (nothing to resolve), leaving the boost in place.
- **Alternatives considered**: `Announce{Delete{Tombstone}}` (FEP-1b12): leaves the boost standing on Mastodon and
  GoToSocial; both `Undo` and `Announce{Delete}`: two activities for one event. A plain repeated `Announce{IRI}` for an
  edit: no receiver can tell an edit from a repeat.

## R-64 Local replies are announced too (spec)

- **Decision**: a reply published through the app (`POST /internal/actors/{user}/objects` with `inReplyTo` into the
  Group's thread) by a local user other than the Group is announced by the Group in the publish transaction, right
  after `Threads.recordLocalReply`; its `PUT` and `DELETE` are followed by the `Announce{Update}` (the `Update`'s id is
  our own Update activity id) and the `Undo`. Local replies are always public or unlisted (002 FR-012), so the audience
  check always passes. No author inbox: the author is local.
- **Rationale**: the task's example is one Frunchy following another: the comments Frunchy B's own users write on B's
  episodes are the bulk of the conversation, and they go out as `Create`s from those users to *their* followers, never
  to the Group's. Without this the feature would not carry the comments the task is about.
- **Lock order**: the publish transaction already holds the replier's `local_actors` row (`lockLiveActor`, NO KEY
  UPDATE). The Group's row is then read `FOR KEY SHARE` (live, type Group, `announces_replies`): a deletion of the
  Group in flight (`FOR UPDATE`) makes the read wait and then see the row gone, so nothing is queued for a deleted
  Group (003 R-34); KEY SHARE does not conflict with NO KEY UPDATE, so two users replying into each other's Groups'
  threads at once cannot deadlock, and the instance block's sorted `FOR UPDATE` over every actor (004) waits for the
  replier's row first and takes the Group's after the publish commits. In 002's inbound path the Group's row (the root
  author) is already held NO KEY UPDATE by `lockLiveActor`, so the same read adds nothing there.

## R-65 Inbound: which Announces count, and how the object is trusted

- **Decision**: an `Announce` is handled when the activity is the signer's own (actor = signer, id on the signer's
  host, 001 FR-015), the signer's stored `actor_type` is `Group`, and at least one live local user has an accepted
  outbound follow of it (`FeedPosts.followerCandidates`), all checked before anything is fetched. Its target is: the
  `object` when it is an IRI or an object with an `id` whose `type` is not an activity; the inner `object` when the
  `object` is a `Create` (FEP-1b12 Lemmy shape) or an `Update` (R-63, the edit); anything else (a `Delete`, a `Like`,
  no id) is ignored. A target on this instance's own domain is ignored (the app published it). The target is fetched
  from its origin signed by the instance actor (`RemoteActors.fetchObject`, which refuses an instance-blocked host
  before connecting) and accepted only when the document's `id` equals the target, its `type` is Note or Article, its
  `attributedTo` is an actor id on the same host as the target (the origin is authoritative for both), that actor
  resolves (`RemoteActors.getActor`: cached under 24 h, else fetched; an instance-blocked author is `Blocked` and the
  announce dropped), and the audience read from the object's `to`/`cc` against the author's followers collection is
  `public` or `unlisted`. Nothing in the `Announce` body beyond the target id and the kind (new or edit) is used.
- **Rationale**: Principle III: the announcer is not the author; only the origin can vouch for the object. A Group
  nobody follows, or a non-Group, costs no fetch (an `Announce` is a cheap way to make a server fetch things). A
  followers-only or direct object announced by a Group would widen an audience the author chose: dropped (Principle
  II, fail closed). Our own objects are already the app's.
- **Alternatives considered**: trusting an embedded object on the announcer's host (the announcer is not its author);
  accepting Announces from any followed actor (005 decided boosts by people stay out, and a Person's boosts are not a
  community's contributions).

## R-66 Recipients and events: `announcedBy`, `feed_posts.announced_by`

- **Decision**: recipients are computed as 005 does for a post of the author, with the Group in the author's place
  for the follow: the live local users with an accepted outbound follow of the Group, minus the root author of a
  thread of the app's the object replies into (R-53), locked `ORDER BY app_user_id FOR NO KEY UPDATE` (R-57), then
  filtered to those still following the Group and not covered by a block of the Group (`qualifying … mustFollow=true`
  with the Group) nor of the author (`qualifying … mustFollow=false` with the author); rows are inserted into
  `feed_posts` with `remote_actor` = the author and the new column `announced_by` = the Group (`ON CONFLICT DO
  NOTHING RETURNING local_actor`), and `post.received` is queued for the users returned, with `actor` the author's
  summary and the new top-level `announcedBy` the Group's summary. `post.updated` and `post.deleted` carry
  `announcedBy` likewise; the three events carry `announcedBy: null` when the post came straight from a followed
  account.
- **Rationale**: one feed code path in the app (the task offers `post.received` with the announcer recorded or a new
  event; the former keeps `actor` = author for display and lets the app filter on `announcedBy`). The rows are the
  same rows 005 uses, so a user who follows both the author and the Group is told once, by whichever arrived first,
  and the author's own `Update` and `Delete` (005's paths match rows by `remote_actor` = the author) still reach the
  announced rows. A `null` `announcedBy` on existing feed events is additive (Principle I); the reply events are
  untouched.

## R-67 Idempotency, and one `post.updated` per edit

- **Decision**: `Announce` (new): the `feed_posts` primary key (post id, user) makes it once per user whatever the
  `Announce` id; the `Announce` id is also recorded in `processed_activities` in the same transaction, and checked
  (read only) before any fetch so an exact repeat costs nothing. `Announce{Update}`: the `Announce` id is the marker
  (ours are unique per edit; a repeat is dropped before the fetch); the users told are those with a live row
  `announced_by` this Group who still follow it, block neither it nor the author, **and have no accepted follow of
  the author** (those get the author's own `Update` through 005's path, which matches the same rows by
  `remote_actor`). `Undo{Announce}`: the live rows `announced_by` this Group for the object turn `deleted` (once);
  `post.deleted` goes to their users who block neither the Group nor the author; a repeat finds nothing live.
- **Rationale**: 005 R-55 and R-56 carried over; the exclusion of direct followers of the author is what keeps
  "no user receives two events about one change" (005 FR-003's spirit) when the author's edit arrives by two roads.

## R-68 Locks

- **Decision**: inbound announce transactions lock the candidate users as 005 R-57 (sorted, NO KEY UPDATE) and
  nothing else; the outbound announce is written inside 002's reply transaction (the Group's row already held) or the
  publish transaction (the Group's row `FOR KEY SHARE`, R-64). No new lock order.

## R-69 Scope: the constitution's "boosts of remote posts"

- **Decision**: constitution amendment 1.1.0 (MINOR, Scope and Technology Constraints): "boosts of remote posts" is
  narrowed to boosts *by the app's users* of arbitrary remote posts (a reblog feature) and public timelines; a
  `Group`'s announces of the replies in its own threads (FEP-1b12) and the passing on of a followed Group's announces
  are in scope since this feature. The vision's non-goal line says the same.
- **Rationale**: the governance section asks for a written amendment before an out-of-scope item is built; the task
  (the project's own) asks for exactly this, and the user-facing meaning of the non-goal (an app's "boost" button and
  a federated timeline) is untouched: nothing here lets a user boost anything, and nothing is a timeline.

## R-70 What real servers do with a Group's announces (filled in by the interop task)

- **Expected**: GoToSocial 0.22.2 and Mastodon 4.7.3 accept an `Announce` of a public reply from a followed `Group`
  (the reply's author's server, which is also the follower's server in the test) and list the Group under the reply's
  boosters (`GET /api/v1/statuses/{id}/reblogged_by`); a followers-only reply is not boosted (never sent). Both answer
  2xx to the `Announce` of the `Update` (no visible effect) and to the `Undo` of the `Announce` (the boost disappears).
  Both deliver the author's `Update` and `Delete` of the boosted reply to Megáfono (the booster's server) even when
  no local user follows the author.
- **Observed** (2026-11-04, `tests/Megafono.Interop.Tests/GroupAnnounceTests.fs`: `GroupAnnounceTests` on GoToSocial
  0.22.2 and `MastodonGroupAnnounceTests` on Mastodon 4.7.3; both runs green, GoToSocial about 50 s, Mastodon about 40 s
  with the images present). The scenario: carol follows the Group (an `announcesReplies` Group, unlocked, accepted at
  once), finds its post by URL and replies publicly mentioning the Group. Everything in **Expected** held except the
  last sentence, which holds on Mastodon only:
  - **`reblogged_by` (SC-001)**: the Group is listed among the boosters of carol's reply on both servers, as soon as
    the first poll after `reply.received` (GoToSocial 0.5 s, Mastodon 1.0 s after carol posted; the limit is 30 s). On
    Mastodon the account has `group: true`; GoToSocial reports `group: false` for it.
    The `Announce` delivery row was `delivered` at the first check, 1 attempt, no error.
  - **The `Announce` on the wire** (from the `activities` row): `to` = [Public], `cc` = [the Group's followers
    collection, carol's actor id], `object` the reply's id as a string. Deliveries: GoToSocial **one** (carol's personal
    inbox, which is also her follower inbox: GoToSocial publishes no shared inbox, so the follower and author inboxes
    collapse into one row); Mastodon **two** (its shared `/inbox` for the follower and carol's personal inbox as the
    author), both `delivered`: a Mastodon server therefore gets the boost twice, which it takes without harm (one
    boost in `reblogged_by`).
  - **Followers-only reply** (`private` with a mention of the Group): reached the app as `reply.received` with
    `audience: followers` on both servers (0.5 s), no `Announce` activity was queued for it and its `reblogged_by`
    stays empty (R-62, FR-002).
  - **`Announce{Update}`**: after carol's edit (with the mention kept) `reply.updated` arrived at once and an
    `Announce` whose object is `{id: <carol's Update id>, type: "Update", actor: carol, object: <reply id>}` was queued
    and `delivered` (2xx) on both servers (GoToSocial one delivery, Mastodon two per edit, one per inbox). The Update's
    id is GoToSocial's `…/statuses/{id}/activity#Update` and Mastodon's `…/statuses/{id}#updates/{unix time}`. Neither
    server showed any change: three seconds later `reblogged_by` still listed the Group, once (a no-op, as expected).
  - **`Undo{Announce}`**: after carol's deletion `object.deleted` arrived at once and the Group's `Undo` (its object the
    original `Announce`, same addressing as the boost) was queued and `delivered` (2xx) on both servers. The status was
    gone by then, so what the `Undo` changed on a timeline could not be seen: only the 2xx is confirmed.
  - **The author's `Update` and `Delete` reaching Megáfono as a booster (no mention, nobody here follows carol)**:
    **differs by server**. *Mastodon* sent both: carol edited her announced reply dropping the mention and Megáfono
    received the `Update` (`reply.updated` within 0.5 s); a second announced reply edited without the mention and
    deleted gave `object.deleted` within 0.5 s and the `Undo` of its announce (both `delivered`). *GoToSocial* sent
    neither: the `Update` of the reply edited without the mention did not arrive in 15 s, nor did the `Delete` of the
    second reply (the inbox log shows only the `Follow`, the two `Create`s, and the `Update` and `Delete` of the
    replies that still mentioned the Group). GoToSocial addresses an `Update` or `Delete` to the status's current
    audience (followers, mentions) and does not add the servers that boosted it, unlike Mastodon's `StatusReachFinder`.
    The R-62 rationale that GoToSocial "likewise" keeps a boosting server current was therefore wrong for GoToSocial.
    The effect: for a reply from a GoToSocial user that does not (or no longer) mention the Group, an edit or deletion
    does not reach Megáfono, so its `Announce{Update}` / `Undo` is never sent, and servers that got the reply through the
    Group's boost learn of the change only from the author's own server (followers) or by refetching. No production change
    was made; see the report of T022 and the limits in `docs/operating.md`.
  - **Two edits in one second (Mastodon)**: in a later run the test's two edits of carol's reply fell within the same
    second, and the second `reply.updated` never came: Mastodon names an `Update` after the edit's second
    (`…#updates/{unix time}`) and writes `updated` to the second, so the second edit carried the first one's id and
    time and Megáfono took it for a repeat (002's `{updateId}|{updated}` marker). Two edits by a person within one
    second do not happen; the test now waits over a second between them.
  - **Inbox answers**: Megáfono answered 202 to every `Create`, `Update` and `Delete` the servers sent.
  - **Timing**: every event and every delivery was seen within one poll tick (0.5 s) on both servers.
