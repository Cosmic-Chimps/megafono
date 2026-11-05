# Feature Specification: Group actors announce the replies they receive

**Feature Branch**: `006-group-announce` (worked on the CodeAlta worktree branch `alta/canny-mira-kkng`)

**Created**: 2026-11-04

**Status**: Draft

**Input**: CodeAlta task `2026-10-27-group-actors-announce-the-replies-they-receive-8dxr`: "Frunchy wants one instance's
series account (`@frieren@frunchy.lacalabaza.xyz`) to follow the same series on another Frunchy
(`@frieren@otherfrunchy.io`) and receive its episode threads **and their comments**. Following works since 0.2.0
(`POST /internal/actors/{id}/following`, `post.received`), so the threads arrive. The comments don't: a reply on the
other instance is addressed to that thread's owner and the replier's followers, and docs/operating.md says a `Group`
actor "does not boost its followers' replies, so it is not a Lemmy-style community", and inbound `Announce` is
"acknowledged and dropped". 1. Group actors announce the replies they receive (FEP-1b12, as Lemmy communities do):
when a verified reply to an object a local `Group` published arrives (`reply.received`), the Group sends an `Announce`
of it to its followers. An edit or delete of that reply follows (Announce of the Update/Delete). Opt-in per actor or
per app setting, so Person actors and apps that don't want it are unchanged. 2. Inbound Announce from a followed
Group: when a followed `Group` announces a reply, pass the announced object on as a webhook (`post.received` with the
announcer recorded, or a new `post.announced`), deduplicated by the object id, verified by fetching the object from its
origin (not trusting the Announce body). 3. `extensions` on the announced object come through as for posts, so
Frunchy can carry its series and episode ids. Contract and wire shapes in a spec; tell the Frunchy session (it
consumes this) when it is released."

## Overview

Since 002 a `Group` actor (one account per series, in Frunchy) is a plain account: people follow it and reply in its
threads, and the app that owns the Group gets every reply as `reply.received`. But a reply is addressed to the
thread's owner and to the replier's own followers, so an account that follows the Group (another instance of the same
app, say) sees the Group's episode posts (005) and none of the conversation under them.

With this feature a `Group` that the app opts in announces the replies in its threads to its followers, the way a
Lemmy community does (FEP-1b12): an `Announce` of the reply for a new one, an `Announce` of the edit, and the
retraction of the announce (`Undo`) when the reply is deleted. Mastodon and GoToSocial followers see the reply boosted
by the Group. And when one of the app's users follows a remote `Group` that announces, the announced posts reach the
app as feed events (`post.received`, `post.updated`, `post.deleted`) with the announcing Group recorded, after
Megáfono fetched each post from its own server, so Frunchy A's series account following Frunchy B's receives B's
threads and their comments.

Vision references: FR-003 (Group actors), FR-012 (inbound activity handling; `Announce` was "acknowledged and
dropped"), FR-032 (a webhook for every inbound event), the app-extension rule, the "Audience and privacy" rules.
Constitution Principles I (protocol-free app, additive contract), II (privacy: only public and unlisted replies are
announced, blocks respected on both sides), III (verify before trust: the announced object is read from its origin),
V (durable, idempotent) and VI (interop on real servers). The constitution's scope list named "boosts of remote
posts" out of scope; amendment 1.1.0 brings a Group's announces of the replies in its own threads, and a followed
Group's announces, into scope (research R-69).

## Clarifications

Decided in this spec from the task's text, since no interactive clarification was asked for:

- The opt-in is per actor, through the app's actor lookup (`announcesReplies: true`), and only a `Group` may opt in.
  An app that never sets it is unchanged.
- The Group announces **every public or unlisted reply in its threads**, however deep, whether it arrived from
  another server or was published through the app by one of the app's own users (Frunchy B's comments on its own
  episodes are what Frunchy A wants). Followers-only and direct replies are never announced. The Group's own replies
  are not announced (its followers get them as the Group's posts).
- A deletion is announced as the retraction every server understands, `Undo` of the `Announce`, not as an `Announce`
  of the `Delete`: Mastodon and GoToSocial followers drop the boost, and a Megáfono follower turns it into
  `post.deleted` without fetching anything. An edit is an `Announce` of the `Update` (FEP-1b12 shape), which Megáfono
  followers turn into `post.updated` after re-reading the reply from its origin; Mastodon and GoToSocial get the
  author's own `Update` directly because they deliver edits to the servers that boosted a post.
- Announced posts reach the app as the existing feed events with one new top-level field, `announcedBy` (the Group,
  or `null` for a post that came straight from a followed account), not as a new event type: one feed code path
  in the app, and the author stays in `actor`.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A Group boosts the replies in its threads (Priority: P1)

The app marks its series account (a `Group`) as announcing. Carol, on Mastodon, follows it. Bob replies to one of the
Group's episode posts. Carol's home timeline shows bob's reply boosted by the Group. When bob edits his reply the
Group announces the edit; when he deletes it the Group retracts its boost.

**Why this priority**: it is the half that makes the Group a community rather than a plain account, and the half other
servers see.

**Independent Test**: on GoToSocial and on Mastodon, a user replies publicly to an announcing Group's post; the server
lists the Group among the accounts that boosted the reply within 30 seconds. A followers-only reply is not boosted.

**Acceptance Scenarios**:

1. **Given** a `Group` the app marked `announcesReplies` and carl who follows it, **When** bob's public reply to one of
   the Group's posts arrives and becomes `reply.received`, **Then** the Group sends one signed `Announce` of the reply
   (by its id, never a copy of its content) to carl's server and to bob's own inbox, addressed to the public and the
   Group's followers, queued together with the reply's record and event.
2. **Given** the same, **When** bob's reply is unlisted, **Then** the `Announce` is addressed like an unlisted post;
   **When** it is followers-only or direct, **Then** nothing is announced.
3. **Given** bob's reply was announced, **When** bob edits it, **Then** the Group sends an `Announce` of the `Update`
   (naming the reply, with the edit's own id) to its followers' servers and bob's inbox; **When** bob deletes it,
   **Then** the Group sends `Undo` of its `Announce` to the same.
4. **Given** one of the app's own users replies in the Group's thread through the app, **Then** the Group announces
   that reply, its edits and its deletion in the same way, to its followers' servers.
5. **Given** a `Group` without `announcesReplies`, a `Person` or `Service` actor, or a reply the Group itself wrote,
   **Then** nothing is announced; a lookup answer with `announcesReplies: true` on a non-Group is an invalid answer.
6. **Given** a reply arrived before the Group was marked announcing, **When** it is edited or deleted, **Then** nothing
   is announced for it.
7. **Given** a follower's server that the Group's user or the instance blocks, **Then** no `Announce` goes there (as
   for any delivery).

---

### User Story 2 - A followed Group's announces reach the app (Priority: P1)

Frunchy A's series account follows Frunchy B's. On B, dave comments on an episode and B's Group announces it. A's app
receives dave's comment as a feed post for A's series account, with dave as the author and B's Group as the announcer,
with B's series and episode ids under `extensions`; dave's edit and deletion follow.

**Why this priority**: it is what the task is for; US1 alone helps Mastodon followers but not the other Frunchy.

**Independent Test**: the stub remote plays a `Group` that alex follows; it announces a note served by the stub; the
app receives `post.received` for alex with the note's author in `actor` and the Group in `announcedBy`; an
`Announce{Update}` gives `post.updated`, `Undo{Announce}` gives `post.deleted`.

**Acceptance Scenarios**:

1. **Given** alex has an accepted follow of a remote `Group`, **When** the Group sends `Announce` of a Note or
   Article, **Then** Megáfono reads the object from its own server (never from the `Announce`), checks it is
   attributed to an actor of that server and is public or unlisted, and the app receives `post.received` for alex with
   the author in `actor`, the Group in `announcedBy`, and the object's fields and `extensions` as for any feed post.
2. **Given** the same, **When** the Group sends an `Announce` whose object is the `Update` of that note, **Then**
   Megáfono reads the note again from its origin and the app receives `post.updated` for alex; **When** the Group
   sends `Undo` of its `Announce`, **Then** the app receives `post.deleted` for alex, with nothing fetched.
3. **Given** the Group announces the same object again under another activity id, or the same `Announce` twice,
   **Then** alex is told once; a repeated `Announce` id is not even fetched again.
4. **Given** bea follows the Group and also follows dave directly, **When** dave edits the comment, **Then** bea gets
   one `post.updated` (from dave's own `Update`), not a second one from the Group's announce of it.
5. **Given** the announced object is a reply into one of the app's own threads, **Then** the thread's author gets
   002's reply events only, the other followers of the Group get the feed events (as 005 FR-003).

---

### User Story 3 - Trust, privacy and blocks hold on both sides (Priority: P1)

Nothing is boosted or passed on that the rules of 001 to 005 would not allow.

**Why this priority**: a Group that boosts is a loudspeaker; a follower that trusts announces is a door.

**Independent Test**: integration tests send Announces from a `Person`, from a Group nobody follows, of an object
attributed to another host than its id, of an object on this instance, of a followers-only object, of an object whose
author alex or the instance blocks, and from a Group alex blocks; none produces an event.

**Acceptance Scenarios**:

1. **Given** an `Announce` from an actor that is not a `Group`, or from a Group no live local user follows, **Then**
   it is acknowledged and dropped and nothing is fetched (005's rule for boosts stands for everyone else).
2. **Given** the announced object's `id` is on another host than its `attributedTo`, or the object is not a Note or
   Article, or it is one of this instance's own objects, or its origin does not serve it, or it is followers-only or
   direct, **Then** nothing is passed on.
3. **Given** alex blocks the author or the author's domain, or the instance does, **Then** alex hears nothing of the
   announced post, its edit or its deletion; the instance never contacts a blocked domain.
4. **Given** alex blocks the Group, **Then** the follow has ended and alex hears nothing; **Given** the instance blocks
   the Group's domain, **Then** its `Announce` is refused with 403 as any request from it.
5. **Given** a deleted local user who followed the Group, **Then** nothing is sent for that user.
6. **Given** the reply's author is blocked by the Group's user or the instance, **Then** the reply never became
   `reply.received` and is not announced either.

### Edge Cases

- A Group announcing a reply hosted on its own server (the comment of one of its instance's users) is the main case:
  the announced object's host may be the Group's own host, any other host, but never this instance's.
- A Group's announce of a post that a user already received from the author (the user follows both): the user is
  told once, by whichever arrived first; a later edit reaches the user once, from the author.
- The Group is deleted (003) while an announce is queued: the deletion cancels its pending deliveries; nothing is
  announced for a deleted Group.
- A reply's author is blocked by the Group's user after the reply was announced: the announce already sent is not
  retracted (limitation, stated in the docs). The author's later edits are dropped as today, so nothing more is
  announced.
- A Mastodon or GoToSocial server receiving an `Announce` of an `Update`: it is harmless there (at worst a repeat of
  a boost it already has); its copy of the reply is kept current by the author's server, which sends edits to the
  servers that boosted the post.
- An `Announce` of a `Delete` activity (Lemmy's shape for deletions) is acknowledged and dropped: deletions are
  announced here as `Undo{Announce}`.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** The app's actor lookup MAY send `announcesReplies` (boolean, default `false`). It is honoured only for a
  `Group`; an answer with `announcesReplies: true` for another type is an invalid answer, as any other malformed
  profile. The value is stored with the profile and refreshed as the profile is; it does not appear in the actor
  document.
- **FR-002** When a public or unlisted reply into a thread whose root an announcing Group published is recorded,
  whether it arrived verified from another server (002) or was published through the app by one of the app's other
  users, the Group MUST send an `Announce` naming the reply by id (never embedding its content), addressed like the
  reply's audience (public: to the public, cc the Group's followers and the author; unlisted: to the followers, cc the
  public and the author), delivered to the Group's followers' servers (one request per server) and, for a remote
  reply, to the author's own inbox. It MUST be queued in the transaction that records the reply, so a crash cannot
  record a reply without its announce or the reverse. Followers-only and direct replies, and the Group's own replies,
  MUST NOT be announced.
- **FR-003** An edit of an announced reply MUST be followed by an `Announce` whose object is the `Update` (the edit's
  id, the author, the reply's id) and a deletion by an `Undo` of the Group's `Announce`, both to the recipients of
  FR-002 as they are at that moment. A reply that was never announced produces nothing on edit or deletion.
- **FR-004** When a `Group` that live local users follow (accepted outbound follow) sends an `Announce` of a Note or
  Article (the object given by id, embedded, or wrapped in a `Create`), Megáfono MUST read the object from its own
  origin (signed) and accept it only when its `id` is the one announced, it is a Note or Article attributed to an
  actor on the object's own host, it is not one of this instance's objects and it is public or unlisted; then it
  MUST tell the app with one `post.received` per qualifying user (FR-005), with the author in `actor` and the Group
  in `announcedBy`, the object's fields and `extensions` as in 005.
- **FR-005** The users told are the live local users with an accepted follow of the Group, except the author of a
  thread of the app's the object replies into (they get 002's reply events), users who block the Group, the author
  or either domain, and deleted users. Nothing MUST be fetched before Megáfono knows the signer is a `Group` with at
  least one such follower; an instance-blocked author's domain MUST never be contacted.
- **FR-006** Each user MUST be told about an announced post at most once per post id, whatever the `Announce` id;
  an `Announce` id seen before MUST be dropped without a fetch.
- **FR-007** An `Announce` of an `Update` from the Group MUST produce `post.updated` (the object re-read from its
  origin) for the users who received the post through this Group, still follow the Group, do not block the Group or
  the author, and do not follow the author directly (those hear from the author). An `Undo` of the Group's `Announce`
  MUST produce `post.deleted` for the users who received the post through this Group and block neither the Group nor
  the author, with nothing fetched. Distinct edits MUST each be passed on; repeats MUST NOT.
- **FR-008** An `Announce` from an actor that is not a `Group`, from a Group nobody follows, of an object that is not
  a Note or Article, of a `Delete`, or that fails any check of FR-004 MUST be acknowledged with 202 and dropped, as
  today.
- **FR-009** Data on an announced object under a namespace the app has published with MUST be passed back under
  `extensions` exactly as for a followed account's post (005 FR-008).
- **FR-010** The contract change MUST be additive: `announcesReplies` on the lookup answer, `announcedBy` on
  `post.received`, `post.updated` and `post.deleted` (`null` when the post came straight from a followed account);
  every existing event keeps its meaning and shape.
- **FR-011** Megáfono MUST NOT keep the announced object's content. It MAY keep which announce it sent for a reply
  (to follow it with the edit and the retraction) and which Group brought a post to a user (to route edits and
  deletions).
- **FR-012** Deliveries of announces follow every rule of 001 and 004: signed by the Group, shared inboxes, retries,
  no delivery to a blocked server or actor, none for a deleted Group.

### Key Entities

- **Announcing Group**: a local `Group` actor the app marked `announcesReplies`.
- **Announced reply**: a reply in an announcing Group's thread and the `Announce` the Group sent for it.
- **Announced feed post**: a feed post (005) that reached a user through a followed Group's announce: the post's id,
  its author, the announcing Group, how it was addressed, live or deleted.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** Against GoToSocial and Mastodon, a public reply to an announcing Group's post is listed by the replier's
  server as boosted by the Group within 30 seconds; a followers-only reply is not boosted; the edit's announce and
  the retraction are accepted (2xx) by the server.
- **SC-002** The `Announce`, `Announce{Update}` and `Undo{Announce}` Megáfono sends are, byte for byte, shapes that
  Megáfono's own inbox turns into `post.received`, `post.updated` and `post.deleted` for a follower of the Group
  (tested with the sender's own builders on the receiving side).
- **SC-003** Every rule of FR-004, FR-005 and FR-008 has a test that tries to break it (a non-Group announcer, a Group
  nobody follows, an object attributed elsewhere, our own object, a followers-only object, a blocked author, a
  blocked Group, an instance-blocked domain, a deleted user, a thread's root author), and none produces an event or
  a fetch it should not.
- **SC-004** Existing suites stay green; 002's and 005's events are unchanged apart from the new `announcedBy` field
  (`null`) on the feed events.
- **SC-005** Repeats (the same `Announce` twice, another `Announce` id of the same object, the same edit's announce
  twice) produce exactly one event per change and user.

## Assumptions

- The app marks a Group announcing through its actor lookup; no internal API call or operator setting is added. An
  app can turn it off again the same way: nothing more is announced from then on, earlier announces stay.
- Other servers deliver the author's edits and deletions to the server that boosted a post (Mastodon and GoToSocial
  do), so their followers of the Group do not depend on the Group's `Announce{Update}`.
- A Megáfono follower verifies an announced object by fetching it from its origin with the instance actor's
  signature, as 002 does for a note that is not embedded; a server that refuses unsigned or instance-signed fetches
  of public posts gets its posts dropped.
- The announced object's author is any actor type; the announcer must be a `Group` as its own actor document says.
- No backfill: announces sent before the follow was accepted are not fetched.
