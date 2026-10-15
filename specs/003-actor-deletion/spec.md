# Feature Specification: Actor Deletion

**Feature Branch**: `003-actor-deletion` (worked on the CodeAlta worktree branch `alta/russet-ocelot-q4dw`)

**Created**: 2026-10-15

**Status**: Implemented

**Input**: CodeAlta task "Megáfono: actor deletion (vision US6) before Frunchy goes live": `DELETE
/internal/actors/{id}` sends `Delete{actor}` to every follower and following server (shared inboxes); the actor id
answers 410 Gone afterwards and is never reused (Principle IV); keys and follow rows are removed; published objects
answer 410; app events stop. Interop tests on GoToSocial and Mastodon: the remote account disappears.

## Overview

Feature 001 left vision US6 out. Today, when an app deletes one of its users, the Megáfono actor stays: after the
24 h lookup fallback it answers 404, but no `Delete` is ever sent, so remote servers keep the profile, the follow
relationships and the posts. Apps that promise erasure (Frunchy has `/account/delete`) cannot keep that promise.

This feature gives the app one call. Megáfono tells every server that knows the actor that it is gone, stops
speaking for it, answers 410 Gone for its id and its posts, erases what it stored about it, and never lets the id
come back.

Vision references: US6 (leaving cleanly), FR-031 (actor deletion in the internal API), FR-021 (shared inboxes),
FR-023 (queued, retried delivery), constitution Principles IV (identity is permanent) and V (durable, idempotent
delivery).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The app deletes a user and the fediverse forgets them (Priority: P1)

A user deletes their account on the app. The app calls Megáfono once. Within minutes the account disappears from
the Mastodon and GoToSocial servers that followed it or that it followed, together with its posts there.

**Why this priority**: it is the whole feature; without it the app's erasure promise is false on the fediverse.

**Independent Test**: a GoToSocial (and a Mastodon) user follows a local actor that published a post; the app calls
`DELETE /internal/actors/{id}`; the remote server no longer shows the account or the follow.

**Acceptance Scenarios**:

1. **Given** alex has followers on two servers and follows someone on a third, **When** the app deletes alex,
   **Then** each of the three servers receives one signed `Delete` whose actor and object are alex's actor id,
   sent to its shared inbox when it has one.
2. **Given** a follower's server is down, **When** alex is deleted, **Then** the `Delete` is retried with the usual
   backoff for 48 hours, like any delivery.
3. **Given** alex was deleted, **When** the app calls delete again, **Then** it gets the same answer and nothing is
   sent a second time.
4. **Given** Megáfono never published an actor for the id, **When** the app calls delete, **Then** it gets 404 and
   treats the user as done.

---

### User Story 2 - A deleted actor stays gone (Priority: P1)

After deletion the actor id, its collections and its posts answer 410 Gone to everyone, so servers that missed the
`Delete` learn it on their next fetch. The id is never served again, even if the app keeps answering its lookup or
gives a new user the same handle.

**Why this priority**: Principle IV. A revived id would hand the old followers and replies to whoever gets it.

**Independent Test**: delete an actor whose user the fake app still knows; fetch the actor, its collections, a
public post and a followers-only post; resolve the handle; publish for the id.

**Acceptance Scenarios**:

1. **Given** alex was deleted, **When** anyone fetches alex's actor id, followers, following or outbox, **Then**
   the answer is 410 with a `Tombstone`.
2. **Given** alex had a public post, **When** it is fetched, **Then** the answer is 410 `Tombstone`; a
   followers-only post answers 404 to everyone, since nobody is a follower any more (Principle II).
3. **Given** the app still answers alex's lookup, **When** a remote server fetches alex's actor id or resolves
   `@alex`, **Then** the actor is not recreated: the id answers 410 and the handle 404.
4. **Given** a new app user takes the handle `alex` with a different immutable id, **Then** WebFinger resolves the
   handle to the new actor id; the old id keeps answering 410.
5. **Given** alex was deleted, **When** the app tries to publish, update a profile, follow, answer a follow request
   or list followers for alex, **Then** it gets 410 `gone`.

---

### User Story 3 - Megáfono stops speaking for the user and erases what it kept (Priority: P2)

Once deleted, nothing more goes out or comes in for the actor except the `Delete` itself, the app hears nothing more
about the user, and Megáfono's copies of the user's data are erased.

**Why this priority**: erasure and quiet are what "leaving cleanly" means to the app, but the fediverse-facing part
(US1, US2) matters first.

**Acceptance Scenarios**:

1. **Given** alex had queued deliveries of earlier posts, **When** alex is deleted, **Then** those deliveries are
   skipped and never sent after the `Delete`.
2. **Given** alex had webhooks still queued, **When** alex is deleted, **Then** they are dropped; and no new event
   is queued for alex afterwards, including `delivery.failed` for the `Delete` itself.
3. **Given** a remote user sends a Follow to the deleted alex, **Then** it is refused and the app hears nothing.
4. **When** alex is deleted, **Then** alex's follow rows are removed, the display profile (name, summary, icon,
   profile URL) and the stored copies of alex's posts and their activities are erased at once, and alex's key pair
   is erased as soon as every `Delete` delivery has ended (delivered, given up or skipped).

### Edge Cases

- The app deletes the user before calling Megáfono: the call does not consult the app, so it works the same.
- The app calls delete while a publish for the same user is in flight: the two are serialized; either the publish
  wins and its deliveries are skipped by the delete, or the delete wins and the publish answers 410.
- A remote server missed the `Delete` (down for more than 48 h): its next fetch of the actor gets 410, which Mastodon
  and GoToSocial treat as deletion.
- Replies from remote users in the deleted actor's threads: the threads' posts answer 410, and those replies are
  the remote authors' own; nothing is sent about them.
- The remote servers have already shown the posts to their users. Megáfono cannot recall them beyond the `Delete`;
  the docs say so, like the followers-only limit.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** `DELETE /internal/actors/{id}` deletes the actor of the app user `{id}` without consulting the app.
  It answers 202 with the number of `Delete` deliveries queued; 404 when Megáfono has no actor for the id.
- **FR-002** The call queues one `Delete{actor}` activity (actor = object = the actor id, addressed to Public),
  signed with the actor's key, in the same transaction that marks the actor deleted (Principle V).
- **FR-003** The `Delete` goes to every server that knows the actor: the servers of its followers and follow
  requesters, of the actors it follows or asked to follow, and of every inbox Megáfono delivered to for it before.
  One inbox per server, the shared inbox when known (FR-021); retried like every delivery (FR-023).
- **FR-004** The call is idempotent: a repeat answers 202 with the same body and queues nothing.
- **FR-005** After deletion, `/ap/actors/{id}` and its `followers`, `following` and `outbox` answer 410 with a
  `Tombstone` (`formerType` the actor type), and its personal inbox answers 410.
- **FR-006** The id is never reused or revived: lookups of a deleted id answer "deleted" without asking the app,
  and an app answer carrying a deleted id never recreates or updates the actor. The handle is freed for other ids.
- **FR-007** Every object of the actor becomes deleted (fetches answer as for a deleted object: 410 when the
  requester could have seen it, 404 otherwise); its replies leave their threads' collections.
- **FR-008** Internal calls for a deleted actor answer 410 `gone`: publish, profile refresh, follow, unfollow,
  follow-request answers, follower and following lists.
- **FR-009** Queued deliveries of the actor's other activities are skipped, and its queued webhooks are dropped, in
  the deleting transaction. No event is queued for a deleted actor afterwards.
- **FR-010** Inbound activities aimed at a deleted actor are refused (personal inbox 410; a Follow through the
  shared inbox 404) and never reach the app.
- **FR-011** Erasure: follow rows are removed and the display profile and the stored object documents and
  Create/Update activity bodies are cleared in the deleting transaction; the key pair is cleared once no `Delete`
  delivery of the actor is pending or sending. The actor row stays as a tombstone (id, handle, type, timestamps) so
  the id can never be reused.
- **FR-012** Deletion and every other write for the same actor are serialized, so no activity of the actor is
  created after its `Delete`.

### Key Entities

- **Deleted actor**: the `local_actors` row with `deleted_at` set, kept forever as a tombstone; no profile, no keys
  once its `Delete` is delivered, no follows.
- **Actor Delete activity**: an `activities` row of type `Delete` with no object, one per deleted actor.

## Success Criteria *(mandatory)*

- **SC-001** On GoToSocial and Mastodon, the account of a deleted actor disappears for a follower within the
  interop timeout after the delete call (tested).
- **SC-002** A deleted actor's id is never answered with an actor document again, whatever the app answers (tested).
- **SC-003** A restart after the delete call loses no `Delete` delivery and sends none twice (the delivery queue's
  SC-005 guarantee, unchanged).
- **SC-004** No key, follow row, profile field or post content of a deleted actor remains once its `Delete`
  deliveries have ended (tested).

## Assumptions

- The app calls delete when its user is deleted; Megáfono does not detect deletions on its own (a 404 from the
  lookup still only hides the actor, as in 001).
- No `actor.deleted` webhook is sent for the app's own deletion: the app made the call. The vision's
  `actor.deleted` event stays for remote actors, a later feature.
- Out of scope: per-object `Delete` activities (servers delete an actor's posts with the actor), account migration
  (`Move`), and undoing a deletion.
