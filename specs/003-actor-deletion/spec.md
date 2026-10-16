# Feature Specification: Actor Deletion

**Feature Branch**: `003-actor-deletion` (worked on the CodeAlta worktree branch `alta/russet-ocelot-q4dw`)

**Created**: 2026-10-15

**Status**: Implemented

**Input**: User description: "Actor deletion (vision US6), Megáfono feature 003. Source:
.alta/tasks/2026-10-10-megafono-actor-deletion-vision-us6-before-frunchy-goes-live-wdtq.md, docs/vision.md US6,
constitution Principle IV. When the app calls DELETE /internal/actors/{id}, Megáfono sends Delete{actor} to every
server that follows or is followed by the actor (shared inboxes, one per server, durable and retry-safe like other
deliveries). Afterwards the actor id and all its published objects answer 410 Gone, the id is never reused (a new
user with the same app id is refused), follow rows are removed, and no more app events are produced for that
actor. The private key must stay usable until the Delete deliveries have finished or given up (they need to be
signed), and is destroyed after that. The call is idempotent. It must be tested end to end: after deletion, the
account disappears on GoToSocial and Mastodon. Out of scope: deleting remote actors, account migration (Move),
blocks (feature 004)."

## Overview

Feature 001 left vision US6 out. Today, when an app deletes one of its users, the Megáfono actor stays: after the
24 h lookup fallback it answers 404, but no `Delete` is ever sent, so remote servers keep the profile, the follow
relationships and the posts. Apps that promise erasure (Frunchy has `/account/delete`) cannot keep that promise.

This feature gives the app one call. Megáfono tells every server that follows or is followed by the actor that it is
gone, stops speaking for it, answers 410 Gone for its id and its posts, erases what it stored about it, and never
lets the id come back.

Vision references: US6 (leaving cleanly), FR-031 (actor deletion in the internal API), FR-021 (shared inboxes),
FR-023 (queued, retried delivery); constitution Principles II (privacy), IV (identity is permanent) and V (durable,
idempotent delivery).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The app deletes a user and the fediverse forgets them (Priority: P1)

A user deletes their account on the app. The app calls Megáfono once. Within minutes the account disappears from
the Mastodon and GoToSocial servers that followed it or that it followed, together with its posts there.

**Why this priority**: it is the whole feature; without it the app's erasure promise is false on the fediverse.

**Independent Test**: a GoToSocial (and a Mastodon) user follows a local actor that published a post; the app deletes
the actor; the remote server no longer shows the account or the follow.

**Acceptance Scenarios**:

1. **Given** alex has followers on two servers and follows someone on a third, **When** the app deletes alex,
   **Then** each of the three servers receives exactly one signed `Delete` of alex's actor, sent to its shared inbox
   when it has one.
2. **Given** someone on a fourth server asked to follow alex and is still waiting for approval, **When** alex is
   deleted, **Then** that server receives the `Delete` too.
3. **Given** a follower's server is down, **When** alex is deleted, **Then** the `Delete` is retried with the usual
   backoff for 48 hours, like any delivery, and a restart of Megáfono neither loses it nor sends it twice.
4. **Given** alex was deleted, **When** the app calls delete again, **Then** it gets the same answer and nothing is
   sent a second time.
5. **Given** Megáfono never had an actor for the id, **When** the app calls delete, **Then** it is told there is
   nothing to delete and can treat the user as done.

---

### User Story 2 - A deleted actor stays gone (Priority: P1)

After deletion the actor id, its collections and its posts answer 410 Gone, so servers that missed the `Delete` learn
it on their next fetch. The id is never served again, even if the app keeps answering its lookup.

**Why this priority**: Principle IV. A revived id would hand the old followers and replies to whoever gets it.

**Independent Test**: delete an actor whose user the app still knows; fetch the actor, its collections, a public post
and a followers-only post; resolve the handle; try to publish for the id.

**Acceptance Scenarios**:

1. **Given** alex was deleted, **When** anyone fetches alex's actor id or its followers, following or outbox
   collections, **Then** the answer is 410 Gone with a tombstone.
2. **Given** alex had a public post, **When** it is fetched, **Then** the answer is 410 Gone.
3. **Given** alex had a followers-only post, **When** anyone fetches it, **Then** the answer is 404, never revealing
   that it existed, because nobody can be authorized as a follower any more (Principle II).
4. **Given** the app still answers alex's lookup with the same id, **When** a remote server fetches alex's actor id
   or resolves `@alex`, **Then** the actor is not recreated: the id answers 410 and the handle is not found.
5. **Given** a new app user takes the handle `alex` with a different immutable id, **Then** the handle resolves to
   the new actor; the old id keeps answering 410.
6. **Given** alex was deleted, **When** the app tries to publish, refresh the profile, follow, unfollow, answer a
   follow request or list followers for alex, **Then** it is refused with "gone".

---

### User Story 3 - Megáfono stops speaking for the user and erases what it kept (Priority: P2)

Once deleted, nothing more goes out for the actor except the `Delete` itself, nothing more comes in, the app hears
nothing more about the user, and Megáfono's copies of the user's data are erased.

**Why this priority**: erasure and quiet are what "leaving cleanly" means to the app, but the fediverse-facing part
(US1, US2) matters first.

**Independent Test**: delete an actor with queued deliveries of an earlier post, queued webhooks and followers; check
the queues, an inbound follow, the stored data, and the key before and after the `Delete` deliveries end.

**Acceptance Scenarios**:

1. **Given** alex had deliveries of earlier posts still queued, **When** alex is deleted, **Then** they are cancelled
   and never sent after the `Delete`.
2. **Given** alex had webhooks still queued, **When** alex is deleted, **Then** they are dropped; and no event is
   produced for alex afterwards, including a failure report about the `Delete` itself.
3. **Given** a remote user sends a follow request to the deleted alex, **Then** it is refused and the app hears
   nothing.
4. **When** alex is deleted, **Then** alex's follow relationships are removed and the display profile and the stored
   copies of alex's posts are erased at once.
5. **Given** a `Delete` delivery of alex is still pending, **Then** alex's private key is kept; **when** every `Delete`
   delivery has been delivered, given up or skipped, **then** the key is destroyed.

### Edge Cases

- The app deletes the user before calling Megáfono: the call does not consult the app, so it works the same.
- The app calls delete while a publish for the same user is in flight: either the publish completes first and its
  deliveries are cancelled by the deletion, or the deletion completes first and the publish is refused with "gone";
  no post ever follows the `Delete`.
- The actor has no followers and follows nobody: the call succeeds, sends nothing, and the key is destroyed at the
  next clean-up.
- A remote server missed the `Delete` (down for more than 48 h, or marked unreachable): its next fetch of the actor
  gets 410, which Mastodon and GoToSocial treat as deletion.
- Servers that only received the actor's replies or mentions, without any follow relationship, are not sent the
  `Delete`; they learn of it through 410 when they next fetch the actor or a post.
- Replies from remote users in the deleted actor's threads belong to their authors; nothing is sent about them.
- Remote servers have already shown the posts to their users. Megáfono cannot recall them beyond the `Delete`; the
  docs say so, like the followers-only limit.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** The app can delete the actor of one of its users with a single call identified by the user's immutable
  id. The call does not consult the app's actor lookup. It answers how many servers the `Delete` was queued for, or
  that no actor exists for the id.
- **FR-002** The deletion queues one `Delete` of the actor, signed with the actor's key, in the same durable step that
  marks the actor deleted: either both happen or neither (Principle V).
- **FR-003** The `Delete` goes to every server of a remote actor that follows the actor or asked to, and of every
  remote actor the actor follows or asked to follow; one delivery per server, to the shared inbox when known
  (FR-021), retried like every delivery (FR-023).
- **FR-004** The call is idempotent: a repeat gives the same answer and queues nothing.
- **FR-005** After deletion, the actor id and its followers, following and outbox collections answer 410 Gone with a
  tombstone naming the former actor type, and its personal inbox refuses deliveries with 410.
- **FR-006** The id is never reused or revived: a deleted id is answered as deleted without asking the app, and an app
  answer carrying a deleted id never recreates or changes the actor. The handle becomes free for a user with a
  different id.
- **FR-007** Every published object of the actor becomes deleted: it answers 410 Gone to anyone allowed to see it,
  which for a followers-only object is nobody, so those answer 404 (Principle II). Its replies leave their threads'
  collections.
- **FR-008** Calls from the app for a deleted actor are refused with "gone": publish, profile refresh, follow,
  unfollow, follow-request answers, and the follower and following lists.
- **FR-009** Queued deliveries of the actor's other activities are cancelled and its queued webhooks are dropped as
  part of the deletion. No event is produced for a deleted actor afterwards.
- **FR-010** Inbound activities aimed at a deleted actor are refused and never reach the app.
- **FR-011** Erasure: follow relationships are removed and the display profile and stored copies of the actor's posts
  are erased as part of the deletion. The private key stays usable until every `Delete` delivery has finished or
  given up, and is destroyed then. Only a tombstone of the actor (its id, handle, type and dates) is kept, so the id
  can never be reused.
- **FR-012** Deletion and every other write for the same actor are serialized, so nothing of the actor is sent or
  queued after its `Delete`.

### Key Entities

- **Deleted actor**: the permanent tombstone of a local actor: id, handle, type, creation and deletion dates. No
  profile, no follows, and no keys once its `Delete` deliveries have ended. It can never become live again.
- **Actor Delete**: the one activity announcing the deletion, with one delivery per server that follows or is
  followed by the actor.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** On GoToSocial and on Mastodon, the account of a deleted actor disappears for its follower within one
  minute of the delete call (tested end to end).
- **SC-002** In 100% of tested cases, a deleted actor's id is never answered with an actor again, whatever the app
  answers for it.
- **SC-003** A restart right after the delete call loses no `Delete` delivery and sends none twice to the same server
  (the delivery guarantee of 001's SC-005, unchanged).
- **SC-004** Once the `Delete` deliveries have ended, no key, follow relationship, profile field or post content of the
  deleted actor remains in Megáfono (tested).
- **SC-005** A repeated delete call sends nothing more: each server receives the `Delete` exactly once (tested).

## Assumptions

- The app calls delete when its user is deleted; Megáfono does not detect deletions on its own (a 404 from the lookup
  still only hides the actor, as in 001).
- No webhook is sent for the app's own deletion of a user: the app made the call. The vision's `actor.deleted` event
  stays reserved for remote actors.
- Recipients are limited to follow relationships, as requested; servers that only received replies or mentions learn
  of the deletion through 410 (see Edge Cases).
- "Published objects answer 410" applies to everyone allowed to see them; followers-only objects keep the
  constitution's 404-never-403 rule, which after deletion means 404 to everyone.
- Erasing the profile and the stored post copies goes with the key and follow removal: the app's erasure promise
  (the task's motivation) covers them, and a tombstone needs none of them.
- A Delete for each post is not sent: Mastodon and GoToSocial delete an actor's posts together with the actor.
- Out of scope: deleting remote actors, account migration (`Move`), blocks (feature 004), and undoing a deletion.
