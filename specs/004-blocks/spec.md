# Feature Specification: Blocks

**Feature Branch**: `004-blocks` (worked on the CodeAlta worktree branch `alta/salty-petra-nx7w`)

**Created**: 2026-10-15

**Status**: Clarified

**Input**: User description: "Per-actor and per-instance blocks, Megáfono feature 004. Source:
.alta/tasks/2026-10-10-megafono-per-actor-and-per-instance-blocks-before-public-com-583k.md, docs/vision.md
("Audience and privacy" block rule, FR-012 Block, the blocks row of the internal API), constitution Principle II.
Builds on 003 (actor deletion) on main. A local user can block a remote actor, and the operator can block a whole
remote domain for the instance, through the internal API: POST /internal/actors/{id}/blocks (actor by handle or URL,
or a domain), an instance-wide domain block, unblock and list, all idempotent. A block removes follows both ways
(pending and accepted), cancels queued deliveries to the blocked actor or domain and sends no more, makes every fetch
signed by a blocked actor or domain answer 404 (never 403), and refuses inbound activities from them as early as
possible, before any webhook. Their replies leave our threads' replies collections and no reply.received is produced
for them. Also handle an inbound Block from a remote actor (FR-012): the follows between that actor and the local
user are removed and the app is told. Decide in clarify: whether a per-user block sends a Block activity to the
remote server (Mastodon and GoToSocial do), whether domain blocks cover subdomains, and what webhooks the app gets.
Tested end to end against GoToSocial and Mastodon. Out of scope: moderation beyond per-user and per-instance blocks
(vision non-goal), reports, the operator CLI (a separate task; it will call the same block code later)."

## Overview

Feature 001 left blocks out. Today an app cannot stop a remote account from following its users, replying into their
threads or fetching their posts, and an operator cannot cut off a whole server. Public reply threads (002) are the
first place strangers' content enters an app such as Frunchy, so this is needed before they go live. The vision's
moderation scope is exactly this: per-user and per-instance blocks.

A **user block** is set by one of the app's users and covers that user only: one remote actor or a whole remote
domain. An **instance block** is set by the operator for the whole installation and covers every local user: a
whole remote domain, or one remote actor. Either way the blocked side stops reaching the covered users, their posts stop reaching it, and
the follow relationships between them end in both directions. Separately, when a remote actor blocks one of the
app's users, Megáfono ends the follows between them and tells the app.

Vision references: FR-031 (block in the internal API), FR-012 (inbound Block), the "Audience and privacy" rule "A
Block (by the user or the instance) removes the follow both ways, stops deliveries, and makes fetches from that actor
or domain answer 404", FR-042 (the operator CLI, a separate task, will reuse the instance block); constitution
Principles II (privacy), III (verify before trust) and V (durable, idempotent delivery).

## Clarifications

### Session 2026-10-15

- Q: When a user blocks a remote actor, does Megáfono send a `Block` activity to that actor's server? → A: Yes:
  one signed `Block` to the actor's own inbox, and `Undo{Block}` when the block is lifted (as Mastodon and
  GoToSocial do).
- Q: Do domain blocks (instance and user) also cover subdomains? → A: Yes: a block of spam.example covers
  spam.example and every host under it; a block of a.spam.example covers only that host and the hosts under it.
- Q: Which webhooks does the app get when a block ends follows? → A: `follow.removed` for each follow a block ends,
  in either direction, for user, instance and remote blocks alike; no new event type.
- Q: Besides domains, can the instance block a single remote actor for every local user? → A: Yes: an instance
  block names a domain or an actor; an instance actor block, like an instance domain block, sends nothing to the
  remote server.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A user blocks a remote account (Priority: P1)

A remote account harasses alex. Alex blocks it in the app; the app calls Megáfono once, naming the account by its
handle or its URL. The account no longer follows alex and alex no longer follows it; its follow requests, replies
and fetches no longer reach alex, and alex's posts no longer go to it.

**Why this priority**: it is the basic safety tool for any user once strangers can reply into threads.

**Independent Test**: a GoToSocial (and a Mastodon) user and alex follow each other; alex blocks that user; the remote
server no longer shows the follows; a later reply from that user never reaches the app.

**Acceptance Scenarios**:

1. **Given** bob (remote) and alex follow each other, and carol (remote) has a pending follow request to alex,
   **When** the app blocks bob and then carol for alex, **Then** every one of those relationships, pending or
   accepted, ends in both directions, with one `follow.removed` webhook per ended follow.
2. **Given** alex blocked bob, **When** bob sends alex a follow request, a reply to one of alex's posts, an edit of an
   earlier reply or any other activity aimed at alex, **Then** Megáfono drops it before it can produce a webhook, and
   the app hears nothing; in particular no `reply.received` is produced.
3. **Given** alex blocked bob, **When** alex publishes, **Then** nothing is delivered to bob; deliveries of alex's
   still waiting for bob are cancelled.
4. **Given** alex blocked bob, **When** a fetch signed by bob asks for alex's profile, collections or posts (public
   ones included), **Then** the answer is 404, never 403.
5. **Given** bob had replied in alex's threads before the block, **Then** those replies leave the reply collections
   of alex's posts while the block lasts.
6. **Given** alex blocked bob, **When** the app asks to follow bob for alex, **Then** it is refused as blocked.
7. **Given** alex blocked bob, **When** the app blocks bob again (by handle or by URL), **Then** it gets the same
   block back and nothing happens a second time.
8. **Given** bob's server is down, **When** the app blocks bob by URL, **Then** the block is recorded and applied
   locally at once; blocking by handle needs bob's server to tell which actor the handle names.
9. **When** the app blocks bob for alex, **Then** bob's server receives exactly one signed `Block` from alex, so it
   ends the follows on its side too (it learns of the block; the docs say so); **when** the block is lifted, it
   receives an `Undo` of that `Block`.

---

### User Story 2 - The operator blocks a whole server (Priority: P1)

A server sends spam. The operator blocks its domain for the whole instance. Nothing from that server reaches any
user, nothing goes to it, and every follow between it and local users ends.

**Why this priority**: the task names it; a spam server is the common case and per-user blocks do not scale to it.

**Independent Test**: block the GoToSocial (and the Mastodon) domain for the instance; a reply from a user there to a
local public post never reaches the app; that server can no longer fetch local actors; a new local post is not
delivered there.

**Acceptance Scenarios**:

1. **Given** actors of spam.example follow alex and carol, and carol follows one of them, **When** the operator
   blocks spam.example, **Then** every follow relationship, pending or accepted, between a local user and an actor
   of spam.example ends, and each affected user's app receives `follow.removed` for each.
2. **Given** spam.example is blocked, **When** a request from it reaches Megáfono's inboxes, **Then** it is refused
   as early as possible: before Megáfono contacts spam.example, reads the activity or produces any webhook.
3. **Given** spam.example is blocked, **Then** no delivery is sent to it: those waiting are cancelled and later ones
   are never sent, whoever published them.
4. **Given** spam.example is blocked, **When** a request signed by one of its keys fetches any local actor,
   collection or object, **Then** the answer is 404; and Megáfono itself fetches nothing from spam.example.
5. **Given** spam.example is blocked, **Then** replies from it leave every thread's reply collections.
6. **Given** spam.example is blocked, **Then** a.spam.example is blocked too, with every rule above; a block of
   a.spam.example alone leaves spam.example and b.spam.example unaffected.
7. **Given** one spam account on an otherwise fine server, **When** the operator blocks that actor for the instance,
   **Then** every rule above applies to that actor for every local user, the rest of its server is unaffected, and
   nothing is sent to it.

---

### User Story 3 - A user blocks a whole server (Priority: P2)

Alex wants nothing from spam.example although the operator has not blocked it. Alex blocks the domain for alex's
account only.

**Why this priority**: useful, but the instance block covers the urgent case.

**Independent Test**: alex has a follower and a follow on a server; alex blocks its domain; both relationships end;
nothing from that server reaches alex afterwards, while another local user is unaffected.

**Acceptance Scenarios**:

1. **Given** two actors of spam.example follow alex and alex follows a third, **When** alex blocks spam.example,
   **Then** the three relationships end, and the servers concerned are told the follows ended.
2. **Given** alex blocked spam.example, **Then** activities from that domain aimed at alex are dropped before any
   webhook, deliveries of alex's to that domain are cancelled and not sent, and fetches of alex's profile or posts
   signed by its keys answer 404; other local users are not affected.

---

### User Story 4 - A remote account blocks a local user (Priority: P2)

Bob, on a Mastodon server, blocks alex. Bob's server sends Megáfono a `Block`. The follows between bob and alex end,
and the app learns that bob no longer follows alex and that alex no longer follows bob.

**Why this priority**: without it the app keeps showing follows that no longer exist on the remote side, and alex's
followers-only posts keep being addressed to bob (vision FR-012).

**Independent Test**: a GoToSocial (and a Mastodon) user and alex follow each other; the remote user blocks alex; the
follows end in Megáfono and the app receives the webhooks decided below.

**Acceptance Scenarios**:

1. **Given** bob and alex follow each other, **When** bob's server sends a verified `Block` of alex signed by bob,
   **Then** both relationships end and the app receives one `follow.removed` for each (inbound and outbound).
2. **Given** bob blocked alex, **When** bob's server later sends `Undo` of that `Block`, **Then** it is acknowledged;
   no follow is restored.
3. **Given** a `Block` that names someone else's actor, or that is not signed by its own actor, **Then** nothing
   changes.

---

### User Story 5 - Blocks can be listed and lifted (Priority: P2)

The app shows a user's blocks and lets them unblock; the operator does the same for instance blocks.

**Why this priority**: a block that cannot be lifted is a trap, but it matters less than the block itself.

**Independent Test**: list blocks; lift one; check that the blocked side's activities reach the user again and that
no follow was restored.

**Acceptance Scenarios**:

1. **When** the app lists alex's blocks or the instance blocks, **Then** it gets every block with what is blocked and
   since when.
2. **When** the app lifts alex's block of bob, **Then** bob's activities and fetches are handled normally again, bob's
   server is told if it had been told of the block, and no follow is restored.
3. **When** the app lifts a domain block, **Then** the domain is handled normally again.
4. **When** the app lifts a block that does not exist, **Then** it is told so; lifting twice is harmless.

### Edge Cases

- Requests that claim to come from an instance-blocked domain are refused on the claim alone, before it is checked:
  claiming to be a blocked server only gets the claimant refused.
- A user block of one actor cannot catch fetches that the remote server signs with its own server key (as Mastodon
  and GoToSocial do for most fetches); only a domain block catches those. Unsigned fetches of public posts cannot be
  attributed to anyone and are still answered. The docs say so.
- Blocking by handle when the remote server is down fails with a clear error; blocking by URL always works.
- The app's own domain and the app's own actors cannot be blocked.
- A delivery already being sent when the block takes effect may still arrive; nothing later does.
- A block outlives the follow relationships it ended: lifting it restores none of them.
- A remote `Block` of a local user who has already blocked that actor, or of a deleted local actor (003), changes
  nothing more.
- Deleting a local actor (003) keeps its blocks with its tombstone; they no longer matter.
- Once delivered, a remote server can keep showing what it already received; a block stops what comes next. The docs
  say so, like the followers-only limit.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** The app can block, for one of its users, a remote actor named by handle or by actor URL, or a remote
  domain; list that user's blocks; and lift one. Blocking is idempotent: a repeat, by handle or URL, answers the
  existing block and changes nothing.
- **FR-002** The app (on behalf of the operator) can block a remote domain or a remote actor (by handle or URL) for
  the whole instance, list the instance blocks and lift one, with the same idempotency.
- **FR-003** Creating a block ends, in the same durable step, every pending or accepted follow relationship in both
  directions between the covered local users and the blocked actor or the actors of the blocked domain.
- **FR-004** What the remote side is told: a user block of an actor sends one signed `Block` to that actor, in the
  same durable step, whenever the actor's inbox is known or can be found, and lifting it sends `Undo{Block}`; a user
  block of a domain tells each server concerned that the ended follows are over; an instance block tells the blocked
  server nothing.
- **FR-005** A domain block (instance or user) covers the domain and every subdomain of it; matching ignores letter
  case and port, and internationalized names match their ASCII form.
- **FR-006** Inbound activities from an instance-blocked domain are refused before Megáfono contacts that domain,
  reads the activity or produces any webhook; activities from an instance-blocked actor are refused as soon as the
  sender is known, before the activity is read. Inbound activities from an actor or domain a user blocked are dropped
  when they are aimed at that user (follow requests, replies and reply edits in the user's threads, anything sent to
  the user's own inbox), before any webhook and without telling the sender; they keep working for other users.
- **FR-007** No delivery is sent to a blocked target: creating a block cancels the matching waiting deliveries
  (instance block: every delivery to the domain or to the actor's own inbox; user block: that user's deliveries to the actor or domain), and
  later deliveries to it are never sent. Only what a block itself sends to tell the remote side is let through.
- **FR-008** A fetch signed by a key of a blocked actor or domain answers 404, never 403, for the actor document, its
  collections, objects, activities and reply collections: an instance block for every local actor, a user block for
  the blocking user's.
- **FR-009** Megáfono fetches nothing from an instance-blocked domain, and the app cannot follow or look up a blocked
  target: following an actor the user or the instance blocked, or resolving a handle of an instance-blocked domain,
  is refused as blocked.
- **FR-010** Replies from a blocked actor or domain are left out of the reply collections of the blocking user's posts
  (every post, for an instance block) while the block lasts; no `reply.received` (or any other webhook) is produced for
  their new replies.
- **FR-011** A verified inbound `Block` whose actor is its signer and whose object is a live local actor ends every
  pending or accepted follow between the two, in both directions, with `follow.removed` webhooks (FR-012); its
  `Undo` is acknowledged and restores nothing.
- **FR-012** Every follow that a block ends (user, instance or remote block, either direction) produces one
  `follow.removed` webhook to the local user concerned, naming the direction and the remote actor, in the same
  durable step as the change. No other webhook announces a block.
- **FR-013** A block and every other write for the same local user are serialized: no activity of the user is queued
  for the blocked target after the block takes effect.

### Key Entities

- **Block**: who blocks (one local user, or the instance), what is blocked (one remote actor, or one domain), since
  when, and whether the remote side was told (so that lifting can tell it too).
- **Remote block**: a remote actor's block of a local user, known only through its effect on the follows and the
  webhooks; Megáfono does not keep it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** On GoToSocial and on Mastodon, after a user block, a reply from the blocked account to the user's public
  post never reaches the app, and the follows between them are gone in Megáfono (tested end to end).
- **SC-002** On GoToSocial and on Mastodon, after an instance block of the server's domain, a reply from it never
  reaches the app and its attempts to look up a local actor fail (tested end to end).
- **SC-003** On GoToSocial and on Mastodon, when a remote user blocks a local user, the follows between them end in
  Megáfono and the app receives its `follow.removed` webhooks within one minute (tested end to end).
- **SC-004** In 100% of tested cases, a fetch signed by a blocked actor or domain answers 404, including for public
  posts and for followers-only posts the blocked actor could see before.
- **SC-005** In 100% of tested cases, nothing is delivered to a blocked target after the block, whoever publishes.
- **SC-006** A repeated block call changes nothing more: anything sent to the remote side is sent exactly once.

## Assumptions

- Instance-blocked senders get a refusal that tells them to stop retrying (Mastodon answers 403 for suspended
  domains, and senders treat it as final). User blocks answer as if the activity was accepted, so the blocked actor
  is not told by the inbox, and the same shared-inbox activity may concern other users.
- Blocking by handle resolves the handle first, so it needs the remote server; the URL form never does.
- What the app already received before a block (earlier replies, webhooks already delivered) stays with the app;
  Megáfono does not recall it.
- Out of scope: the operator CLI (FR-042, its own task, will call the same code), mute, reports, flags and any
  moderation beyond blocks (vision non-goals).
