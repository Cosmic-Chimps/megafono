# Research: Blocks

Numbering continues after 003 (R-30 to R-35). Decisions taken in `/speckit-clarify` (spec, Clarifications) are
marked "(clarified)".

## R-40 What a user block of an actor sends (clarified)

- **Decision**: one `Block` (`actor` the local actor, `object` the blocked actor id, addressed `to` the blocked
  actor), signed by the local actor, to the blocked actor's own inbox. Lifting the block sends `Undo` with the
  stored `Block` embedded. No `Undo{Follow}` or `Reject{Follow}` next to it.
- **Rationale**: it is what Mastodon (`BlockService` → `ActivityPub::BlockSerializer`) and GoToSocial send for a user
  block. On receipt, Mastodon's `ActivityPub::Activity::Block` unfollows in both directions and rejects a pending
  request before recording the block; GoToSocial deletes follows and follow requests in both directions. One activity
  therefore ends both follows on the remote side. Both verify it with the actor key they cached when the follow
  started, so the 404 the blocked actor gets on fetches (R-44) does not stop it.
- **Observed (interop)**: to be filled in by the GoToSocial and Mastodon tests.
- **Alternatives considered**: `Reject{Follow}` + `Undo{Follow}` (private, but the remote user can ask to follow again
  at once); nothing (stale follows and deliveries on the remote side). Both declined in clarify.

## R-41 What a domain block and an instance block send

- **Decision**: a user block of a domain sends `Reject` of every inbound follow it ends (embedding the stored Follow
  id, as the approve/reject call does) and `Undo{Follow}` of every outbound follow it ends, each to the remote actor's
  own inbox. An instance block, of a domain or of an actor (clarified), sends nothing.
- **Rationale**: Mastodon's user domain block (`AfterBlockDomainFromAccountService`) rejects the followers on that
  domain so their servers drop the follow; there is no "domain Block" activity. Mastodon sends nothing when an admin
  suspends a domain or an account; the blocked server is not owed a notice and Megáfono refuses its traffic anyway.
- **Alternatives considered**: one `Block` per actor of the domain (announces a block of people the user never named;
  lifting it would need as many `Undo`s).

## R-42 Matching a domain (clarified: subdomains covered)

- **Decision**: domains are stored lowercase, ASCII (IDN to punycode with `IdnMapping`), without port or trailing dot,
  validated as host names. A host (`remote_actors.host`, `deliveries.host`, the authority of a key id, all
  `host[:port]`) matches when its host name (port removed) equals the domain or ends with `.` + the domain. A domain
  that would cover the app's own domain (the domain itself or a parent of it, e.g. `example.com` for
  `app.example.com`) is refused.
- **Rationale**: Mastodon domain blocks cover subdomains; spam networks rotate them. Ports only appear in tests.

## R-43 Refusing inbound traffic before any work

- **Decision**: the inbox checks instance blocks right after the signature header is parsed (step 3 of the pipeline):
  the key id's host against instance domain blocks, and the key id against instance actor blocks (its owner from the
  cached `remote_actors.key_id`, or the key id without its fragment), and answers **403** before any key fetch. After
  the signer is resolved (step 5) its id and host are checked again. User blocks are checked by the handlers inside
  their transactions after the local actor row is locked (R-46) and answer 202 `Ignored`; a personal inbox of a user
  who blocks the signer is dropped right after step 5, before the body is parsed.
- **Rationale**: Mastodon answers 403 for suspended domains (`SignatureVerification`), and senders treat a 4xx other
  than 408/429 as final, so the blocked server stops retrying. A claim the signature has not proven is enough to
  refuse: someone claiming a blocked key only gets refused. User blocks must not be revealed by the inbox, and one
  shared-inbox activity may concern other local users.
- **Alternatives considered**: 202 for instance blocks (the sender never learns to stop); verifying before refusing
  (the key fetch to the blocked server is the work FR-006 forbids).

## R-44 Refusing fetches

- **Decision**: actor documents, collections, objects, activities and reply/context collections answer 404 when the
  request's `Signature` claims a key of a blocked target: instance blocks for every actor, user blocks of the actor
  served (or of the object's author). As in R-43 the claim decides, without verifying. `/ap/actor` is checked against
  instance blocks only.
- **Rationale**: the vision rule and Principle II (404, never 403). Mastodon and GoToSocial sign most fetches with
  their instance key, so a user block of one actor only catches fetches signed by that actor's own key; the docs say
  so.

## R-45 Stopping deliveries

- **Decision**: three layers.
  1. At block time, in the block's transaction: matching `pending`/`sending` deliveries become `skipped`,
     `last_error = 'blocked'`, lease cleared (instance: every delivery whose host matches the domain, or whose inbox is
     the blocked actor's own inbox; user: the same, limited to the user's activities, except the block's own).
  2. At enqueue time: `Outbox.enqueueDeliveries` leaves out inboxes a block covers for the activity's author (instance
     blocks and the author's user blocks). An actor block matches the actor's own inbox (`remote_actors.inbox`); a
     shared inbox is only left out by a domain block, since it serves other followers too. The block's own activities
     go through `Outbox.enqueueDeliveriesUnfiltered`.
  3. At send time, `Workers/Deliveries` skips (`Skipped "blocked"`) a delivery an instance block covers: it closes the
     race of an enqueue that committed just after step 1 (instance blocks lock no row).
- **Rationale**: user blocks never race (R-46). The send-time check is limited to instance blocks because the only
  activities sent to a blocked target, the block's own, come from user blocks.
- **Alternatives considered**: an advisory lock shared by every enqueue and taken exclusively by blocks: it deadlocks
  against the per-actor row locks of 003 (a publish holds its actor row and waits for the advisory lock while an
  instance block holds it and waits for that row to queue a `follow.removed`).

## R-46 Serializing a user block with the user's other writes

- **Decision**: the user block transaction locks the local actor row `FOR UPDATE`, as 003's delete does. Every write
  that queues an activity of the actor already takes `FOR SHARE` on it (`Outbox.lockLiveActor`, R-34), the inbound
  Follow included; the reply handlers take it on the thread's author before checking blocks; the inbound `Block`
  handler takes it on its target.
- **Rationale**: under READ COMMITTED a publish either commits before the block (its deliveries are skipped by R-45
  step 1) or waits and then sees the block at enqueue time.

## R-47 Hiding blocked replies from collections

- **Decision**: the reply and context collection queries leave out replies whose remote author is covered by an
  instance block or by a user block of the thread root's author. The rows stay; lifting the block shows them again.
- **Rationale**: the collections are public, and a blocked actor's replies in the user's threads are what the block
  is for. Deleting the rows would make an unblock lossy and the counts wrong.

## R-48 Blocking by handle (new in the respecified input)

- **Decision**: `actor` in a block call is an actor URL or a handle (`@user@host` or `user@host`, as the follow call
  accepts). A handle is resolved with `RemoteActors.resolveTarget` before the transaction; a URL is used as the id
  directly and fetched only to learn the inbox for the `Block` (failure there is not an error). The block is stored
  and deduplicated by actor id, so blocking by handle then by URL answers the same block. Resolution failures answer
  as the follow call does (`Internal/RemoteActors.resolutionError`); a handle on an instance-blocked domain answers
  409 `blocked` (it is never resolved). Lifting by handle resolves the same way, from the cache when possible.
- **Rationale**: apps hold handles in "add friend" style screens and actor ids from webhooks; both must work. The id
  is what everything else keys on, and the URL form keeps blocking possible while the remote server is down.

## R-49 Inbound `Block` (FR-011, new in the respecified input)

- **Decision**: `Block` whose `actor` is the signer, whose id is on the signer's host and whose `object` is a live local
  actor: in one transaction (processed marker, `lockLiveActor` on the target) every `pending`/`accepted` follow
  between them in either direction becomes `removed`, with one `follow.removed` per row (clarified). Nothing is sent
  back; Megáfono keeps no record of the remote block. `Undo{Block}` is acknowledged (202) and changes nothing; the
  existing Undo handler already answers 202 for an Undo that is not of a Follow. A `Block` of an unknown, deleted or
  non-local object is 202 `Ignored`.
- **Rationale**: Mastodon (`BlockService`) and GoToSocial deliver `Block` to the blocked actor's own inbox, and they
  have already ended the follows on their side; Megáfono only has to catch up so the app's lists and the followers
  audience stay right. Keeping no remote-block row is enough: a new follow from either side goes through the remote
  server, which refuses it while its block lasts.
- **Alternatives considered**: storing remote blocks to refuse the blocker's future activities (the blocker chose to
  stop talking; its server enforces that); a dedicated webhook (declined in clarify).

## R-50 Interop on GoToSocial and Mastodon

- **Decision**: one test module, run against both servers (GoToSocial in the default suite, Mastodon under
  `Suite=mastodon`, as 003 did): (a) user block: mutual follows, the app blocks the remote user, poll the remote
  relationship until neither follow remains, then a reply from the remote user to a local public post never produces
  `reply.received`; (b) instance block of the server's domain: a reply never reaches the app and a remote lookup of a
  local actor fails; the block is lifted in a `finally` so later tests in the shared network keep working; (c) remote
  block: the remote user blocks the local actor through the client API (`POST /api/v1/accounts/{id}/block`, same on
  both servers), and Megáfono shows both follows `removed` with two `follow.removed` webhooks within a minute.
- **Rationale**: SC-001 to SC-003; the client APIs and fixtures already exist in `Fixtures/FediClient.fs`.
