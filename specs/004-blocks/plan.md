# Implementation Plan: Blocks

**Branch**: `004-blocks` (CodeAlta worktree branch `alta/salty-petra-nx7w`) | **Date**: 2026-10-15 |
**Spec**: [spec.md](spec.md)

## Summary

A new `blocks` table holds user blocks (`local_actor` set) and instance blocks (`local_actor` NULL) of one remote
actor or one domain (subdomains included, R-42). New internal calls create, list and lift them
([contracts/internal-api.md](contracts/internal-api.md)). Creating a block ends the follows both ways with
`follow.removed` events, cancels matching deliveries, and for user blocks sends `Block` (actor) or `Reject`/`Undo`
of the ended follows (domain) (R-40, R-41). Enforcement: the inbox refuses instance-blocked signers with 403 before
any key fetch and handlers drop activities aimed at a user who blocks the signer (R-43); fetches signed by a blocked
key answer 404 (R-44); `Outbox.enqueueDeliveries` filters blocked inboxes and the delivery worker re-checks instance
blocks (R-45); outbound fetches to instance-blocked domains fail; reply and context collections leave out blocked
authors (R-47). User blocks lock the actor row like 003's delete (R-46).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged) · **Dependencies**: unchanged · **Storage**: Postgres; one new
table, one replaced check constraint · **Testing**: xUnit v3; integration with the fake app and the stub remote;
interop on GoToSocial (every run) · **Scale**: blocks are rare writes; every check is an indexed `EXISTS`

## Constitution Check

*Before Phase 0 and again after Phase 1 design: passes both times.*

| Gate | Answer |
| --- | --- |
| Protocol-free app | The app names an actor id or a domain; Megáfono does the protocol. Nothing app-specific. |
| Contract | Additive: six new calls, a new error code, 409 on two calls for a new state, `follow.removed` with `outbound` ([contracts/](contracts/)). |
| Privacy | Fetch 404 for blocked keys on every fetch path (claim-based, fails closed); deliveries filtered at enqueue and re-checked at send; a test per rule tries to break it (fetch of a followers-only post by a former follower after the block, a publish racing the block). |
| Trust | Instance-blocked senders are refused before any key fetch or parsing; nothing new is trusted from a claim except a refusal. No outbound fetch to a blocked domain. |
| Identity | No id changes; `Block` and `Undo` get permanent activity ids. |
| Delivery | Block, follow changes, events and the block's own activities commit together; repeats send nothing; restart-safe like every delivery. |
| Interop | GoToSocial: user block ends follows both ways and drops a later reply; instance block refuses a reply and fetches. |
| Operations | No new dependency; the CLI wrapper is the separate FR-042 task. |
| Scope | Per-user and per-instance blocks are exactly the v1 moderation scope. |

## Design

- **`Infrastructure/Blocks.fs`** (new, after `Outbox.fs`): `normalizeDomain` (trim, lowercase, IDN → ASCII, host
  name check, no port), `hostName` (authority → host), `domainMatches domain host`, `coveredSql localActorExpr
  actorIdExpr hostExpr` (the `EXISTS` fragment of data-model.md; `localActorExpr` `NULL` for instance blocks only),
  `isKeyRefused dataSource keyId localUser option` (claim check of R-43/R-44: key id host against domain blocks; key
  id owner from `remote_actors.key_id`, else the key id without fragment, against actor blocks), `isBlockedBy tx
  localUser actor` (user or instance block covers a `RemoteActor`), `isInstanceBlocked dataSource actorId host`,
  `instanceBlocksDelivery dataSource inbox host`, `instanceBlocksHost dataSource host`.
- **`Database.fs`**: the table, indexes and constraint of data-model.md (idempotent `DO $$` for the constraint).
- **`ActivityStreams`**: `block activityId localActor remoteActor` and `undoBlock activityId localActor blockRef`
  per contracts/federation.md.
- **`Outbox`**: `enqueueDeliveries` leaves out covered inboxes for the activity's author (R-45 step 2);
  `enqueueDeliveriesUnfiltered` keeps today's behavior for the block's own activities.
- **`RemoteActors`**: `RemoteError.Blocked`; `fetchDocument` (every key, actor, object and WebFinger fetch) refuses an
  instance-blocked host before connecting. `Internal/RemoteActors.resolutionError` maps it to 409 `blocked`.
- **`Internal/Blocks.fs`** (new): the six calls and the transactions of data-model.md. For an actor block the actor
  document is obtained before the transaction (`RemoteActors.getActor`); failure only means no `Block` is sent.
- **`Internal/Follows.fs`**: `follow` answers 409 `blocked` when `isBlockedBy` (checked in the transaction after
  `guardLive`).
- **`Federation/Inbox.fs`**: after `checkWithoutKey`, `isKeyRefused … None` → 403; after `resolveSigner`,
  `isInstanceBlocked signer` → 403; a personal inbox whose user blocks the signer → 202 dropped.
- **`Federation/InboxFollows.fs`**: `handleFollow` → `Ignored` when the target blocks the signer (after
  `lockLiveActor`). **`InboxReplies.fs`**: `handleCreate`/`handleUpdate` lock the root author's row and drop when
  the root author or the local parent author blocks the signer. Other handlers need nothing (follows are already
  `removed`).
- **`Federation/Http.fs`**: `claimedKeyId ctx` (the `keyId` of a `Signature` header, if any). **`Actors.fs`**,
  **`Objects.fs`**: 404 when `isKeyRefused` for the served actor / the object's author; `/ap/actor` with instance
  blocks only.
- **`Threads.fs`**: `repliesPage` and `contextPage` exclude replies covered for the thread root's author (R-47).
- **`Workers/Deliveries.fs`**: `instanceBlocksDelivery` before sending → `Skipped "blocked"` (R-45 step 3).
- **`Program.fs`**: `Internal.Blocks.endpoints`.

## Project Structure

New: `src/Megafono/Infrastructure/Blocks.fs`, `src/Megafono/Internal/Blocks.fs`,
`tests/Megafono.Tests/Unit/BlocksTests.fs`, `tests/Megafono.Tests/Integration/BlockTests.fs` (and
`BlockEnforcementTests.fs`), `tests/Megafono.Interop.Tests/BlockTests.fs`. Touched: `Database.fs`,
`ActivityStreams.fs`, `Outbox.fs`, `RemoteActors.fs`, `Threads.fs`, `Federation/{Http,Actors,Objects,Inbox,
InboxFollows,InboxReplies}.fs`, `Internal/{Follows,RemoteActors}.fs`, `Workers/Deliveries.fs`, `Program.fs`,
`SchemaTests.fs`. Docs: `docs/operating.md`, `docs/vision.md`, `README.md`, `CLAUDE.md`, `.specify/feature.json`.

## Complexity Tracking

None.
