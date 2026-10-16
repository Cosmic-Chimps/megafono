# Feature Specification: Blocks

**Feature Branch**: `004-blocks` (worked on the CodeAlta worktree branch `alta/salty-petra-nx7w`)

**Created**: 2026-10-15

**Status**: Implemented

**Input**: CodeAlta task `2026-10-10-megafono-per-actor-and-per-instance-blocks-before-public-com-583k`:
"`POST /internal/actors/{id}/blocks` and an instance-wide domain block: refuse inbound activities from blocked
actors/domains (before any work), stop deliveries to them, answer 404 to their fetches, remove follows both ways
(vision "Audience and privacy" rules). Interop test with GoToSocial." Motivation: public reply threads on series
accounts are the first place strangers' content enters an app (Frunchy), and an operator must be able to stop a
spamming account or server at the protocol level.

## Overview

Feature 001 left blocks out. Today an app cannot stop a remote account from following its users, replying into
their threads or fetching their posts, and an operator cannot cut off a whole server. The vision's moderation scope
is exactly this: per-user and per-instance blocks (vision non-goals, constitution Principle II).

This feature gives the app two kinds of block. A **user block** is set by one of the app's users and covers that
user only: a remote actor or a whole remote domain. An **instance block** is set for the whole installation and
covers every local user: a remote domain (the usual case) or a single remote actor. Either way the blocked side
stops reaching the user (or anyone, for an instance block), the user's posts stop reaching it, and the follow
relationships between them end in both directions.

Vision references: FR-031 (block in the internal API), FR-012 (Block among the handled activities: outbound here),
"Audience and privacy" rule "A Block (by the user or the instance) removes the follow both ways, stops deliveries,
and makes fetches from that actor or domain answer 404", FR-042 (the operator CLI will call the instance block;
the CLI itself is a separate task); constitution Principles II (privacy), III (verify before trust) and V
(durable, idempotent delivery).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A user blocks a remote account (Priority: P1)

A remote account harasses alex. Alex blocks it in the app; the app calls Megáfono once. The account no longer
follows alex and alex no longer follows it, on both servers; its follows, replies and fetches no longer reach alex,
and alex's posts no longer go to it.

**Why this priority**: it is the basic safety tool for any user once strangers can reply into threads.

**Independent Test**: a GoToSocial user and alex follow each other; alex blocks the GoToSocial user; GoToSocial
shows neither follow; a later reply or follow from that user never reaches the app.

**Acceptance Scenarios**:

1. **Given** bob (remote) and alex follow each other, **When** the app blocks bob for alex, **Then** both follow
   relationships end in Megáfono, bob's server receives one signed `Block` from alex, and bob's server ends the
   follows on its side too.
2. **Given** alex blocked bob, **When** bob sends alex a follow request, a reply to one of alex's posts, an edit of
   an earlier reply or any other activity aimed at alex, **Then** Megáfono drops it without telling bob and the app
   hears nothing.
3. **Given** alex blocked bob, **When** alex publishes a post, **Then** nothing is delivered to bob's own inbox; a
   delivery of alex's that was still waiting for bob's inbox is cancelled.
4. **Given** alex blocked bob, **When** bob's key signs a fetch of alex's actor, collections or posts (public ones
   included), **Then** the answer is 404.
5. **Given** bob had replied in alex's threads before the block, **Then** those replies leave the reply and thread
   collections of alex's posts; after an unblock they come back.
6. **Given** alex blocked bob, **When** the app asks to follow bob for alex, **Then** it is refused as blocked.
7. **Given** alex blocked bob, **When** the app blocks bob again, **Then** it gets the same block back and nothing is
   sent again.

---

### User Story 2 - The operator blocks a whole server (Priority: P1)

A server sends spam. The operator blocks its domain for the whole installation. Nothing from that server reaches
any user, nothing goes to it, and every follow between it and local users ends.

**Why this priority**: the task names it; a spam server is the common case and per-user blocks do not scale to it.

**Independent Test**: block the GoToSocial domain for the instance; a GoToSocial user's reply to a local public post
is refused and never reaches the app; GoToSocial can no longer fetch local actors; a new local post is not delivered
there.

**Acceptance Scenarios**:

1. **Given** users on spam.example follow alex and carol, and carol follows one of them, **When** the operator blocks
   spam.example, **Then** every follow relationship between a local user and an actor of spam.example (or of a
   subdomain) ends, and each affected user's app hears `follow.removed`.
2. **Given** spam.example is blocked, **When** any request signed by a key of spam.example (or of a subdomain)
   reaches an inbox, **Then** it is refused before Megáfono fetches a key, parses the body or does anything else.
3. **Given** spam.example is blocked, **Then** no delivery is sent to it: those waiting are cancelled at once and
   later ones are never sent, whoever published them.
4. **Given** spam.example is blocked, **When** a request signed by one of its keys fetches any actor, collection or
   object, **Then** the answer is 404; and Megáfono itself fetches nothing from spam.example (no key, actor, object
   or handle lookups).
5. **Given** spam.example is blocked, **Then** replies from it leave every thread collection.
6. **Given** the operator blocks a single remote actor for the instance, **Then** the same rules apply to that actor
   for every local user.

---

### User Story 3 - A user blocks a whole server (Priority: P2)

Alex does not want anything from spam.example but the operator has not blocked it. Alex blocks the domain for
alex's account only.

**Why this priority**: useful, but the instance block covers the urgent case.

**Independent Test**: alex has a follower and a follow on a server; alex blocks its domain; the follower's server
receives a `Reject` of its follow, the followed account an `Undo` of alex's follow; nothing from that server reaches
alex afterwards.

**Acceptance Scenarios**:

1. **Given** two actors of spam.example follow alex and alex follows a third, **When** alex blocks spam.example,
   **Then** the three relationships end in Megáfono, each follower's server is sent a `Reject` of its follow and the
   followed actor an `Undo` of alex's follow.
2. **Given** alex blocked spam.example, **Then** activities from that domain aimed at alex are dropped silently,
   deliveries of alex's to that domain are cancelled and not sent, and fetches of alex's actor or posts signed by
   its keys answer 404; other local users are not affected.

---

### User Story 4 - Blocks can be listed and lifted (Priority: P2)

The app shows a user's blocks and lets them unblock; the operator does the same for instance blocks.

**Why this priority**: a block that cannot be lifted is a trap, but it matters less than the block itself.

**Independent Test**: list blocks; lift an actor block and see the remote server receive `Undo{Block}`; check that
the follows were not restored and that the blocked actor's activities reach the user again.

**Acceptance Scenarios**:

1. **When** the app lists alex's blocks or the instance blocks, **Then** it gets every block with its target and
   date.
2. **When** the app lifts alex's block of bob, **Then** bob's server is sent an `Undo` of the `Block`, bob's
   activities and fetches are handled normally again, and no follow is restored.
3. **When** the app lifts a domain block (user or instance), **Then** nothing is sent and the domain is handled
   normally again.
4. **When** the app lifts a block that does not exist, **Then** it is told so.

### Edge Cases

- A request signed by an instance-blocked domain is refused even when its signature would not verify: the claim
  alone decides, and claiming to be a blocked server only gets the claimant refused.
- A user block of an actor cannot catch fetches that the remote server signs with its own instance key (as
  Mastodon and GoToSocial do for most fetches); only an instance block of the domain catches those. Unsigned
  fetches of public posts cannot be attributed to anyone and are still answered. The docs say so.
- Blocking an actor whose server is down or unknown still records the block and applies it locally; the `Block` is
  sent only when Megáfono can reach the actor's document (otherwise it is skipped, and nothing is lost: the follows
  are already ended locally and deliveries stopped).
- An instance block sends nothing to the blocked server: its users may still see stale follows on their side. Only
  a user block of an actor sends `Block`, and only a user block of a domain sends `Reject`/`Undo` of follows.
- Blocking a domain covers its subdomains (`spam.example` covers `a.spam.example`).
- The app's own domain cannot be blocked; a local actor cannot be blocked.
- A delivery already being sent when the block commits may still arrive; nothing later does.
- A block outlives the follow relationships it ended: lifting it restores none of them.
- Deleting a local actor (003) keeps its blocks with its tombstone; they no longer matter.
- Once delivered, a remote server can keep showing what it already received; a block stops what comes next. The
  docs say so, like the followers-only limit.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** The app can block, for one of its users, a remote actor (by actor id) or a remote domain, list that
  user's blocks and lift one. The block call is idempotent: a repeat answers the existing block and sends nothing.
- **FR-002** The app can block, for the whole instance, a remote domain or a remote actor, list the instance blocks
  and lift one, with the same idempotency.
- **FR-003** Creating a block ends, in the same durable step, every pending or accepted follow relationship in both
  directions between the covered local users and the blocked actor or the actors of the blocked domain, and queues
  a `follow.removed` event for each to the affected user.
- **FR-004** A user block of an actor sends one signed `Block` to that actor, in the same durable step when the
  actor is known or can be fetched; lifting it sends `Undo{Block}`. A user block of a domain sends a `Reject` of
  each ended inbound follow and an `Undo` of each ended outbound follow. Instance blocks send nothing.
- **FR-005** A domain block covers the domain and its subdomains, compared on the host name without port.
- **FR-006** Inbound requests whose signing key belongs to an instance-blocked domain are refused (403) before any
  key fetch, body parsing or other work; requests from an instance-blocked actor are refused (403) as soon as the
  signer is known, before body parsing.
- **FR-007** Inbound activities from an actor or domain that a user blocked are dropped (202, nothing done, nothing
  sent to the app) when they are aimed at that user: follow requests, replies and edits in the user's threads, and
  anything posted to the user's personal inbox. They keep working for other local users.
- **FR-008** No delivery is sent to a blocked target: creating a block cancels the matching waiting deliveries
  (instance blocks: every delivery to the domain or to the actor's inbox; user blocks: the user's deliveries to the
  domain or to the actor's own inbox), and later deliveries to it are never queued or never sent. The block's own
  `Block`, `Reject` and `Undo` are the only exception.
- **FR-009** A fetch signed by a key of a blocked actor or domain answers 404 for the actor document, its
  collections, objects, activities and reply and thread collections: instance blocks for every local actor, user
  blocks for the blocking user's.
- **FR-010** Megáfono fetches nothing from an instance-blocked domain (keys, actors, objects, WebFinger), and the app
  cannot follow or resolve a blocked target: following an actor the user or the instance blocked is refused as
  blocked, resolving a handle of an instance-blocked domain likewise.
- **FR-011** Replies from a blocked actor or domain are left out of the reply and thread collections of the
  blocking user's posts (every post, for an instance block) while the block exists.
- **FR-012** A block and every other write for the same local user are serialized, so no activity of the user is
  queued for the blocked target after the block commits; an instance block that races a delivery is caught before
  sending.

### Key Entities

- **Block**: who blocks (one local user, or the instance), what is blocked (one remote actor id, or one domain),
  when, and for a user's actor block the `Block` activity that announced it (so it can be undone).

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** On GoToSocial, after a user block, neither follow between the two accounts remains, and a later reply
  from the blocked account never reaches the app (tested end to end).
- **SC-002** On GoToSocial, after an instance block of its domain, a reply from it to a local public post never
  reaches the app and its fetches of a local actor fail (tested end to end).
- **SC-003** In 100% of tested cases, a fetch signed by a blocked actor or domain answers 404, including for public
  posts and followers-only posts the blocked actor could see before.
- **SC-004** In 100% of tested cases, nothing is delivered to a blocked target after the block, whoever publishes.
- **SC-005** A repeated block call sends nothing more: the remote server receives the `Block` exactly once.

## Assumptions

- The app identifies remote actors by actor id (from webhooks and follower lists); blocking by handle is not
  offered, so a block never depends on the remote server being up.
- 403 is the answer to an instance-blocked sender, as Mastodon does for suspended domains: senders treat it as
  final and stop retrying. User blocks answer 202 and drop, so the blocked actor is not told by the inbox.
- A user block of an actor sends `Block`, as Mastodon and GoToSocial do; both end the follows in both directions on
  receipt. The cost, stated in the docs: the blocked actor's server learns of the block.
- `follow.removed` is sent for every follow a block ends, including outbound ones (the event carries the
  direction), so apps that keep follower lists from webhooks stay right without special cases.
- Inbound `Block` activities from remote actors (a remote user blocking a local one) are out of scope; they stay
  acknowledged and dropped as today and are proposed as a follow-up.
- Out of scope: the operator CLI command (FR-042, its own task), blocking by handle, mute, reports, flags and any
  moderation beyond blocks (vision non-goals), hiding the blocked actor's content the app already received.
