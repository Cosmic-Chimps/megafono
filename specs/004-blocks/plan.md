# Implementation Plan: Blocks

**Branch**: `004-blocks` (CodeAlta worktree branch `alta/salty-petra-nx7w`) | **Date**: 2026-10-15 |
**Spec**: [spec.md](spec.md) (clarified 2026-10-15)

**Input**: Feature specification from `specs/004-blocks/spec.md`

## Summary

A new `blocks` table holds user blocks (`local_actor` set) and instance blocks (`local_actor` NULL) of one remote
actor or one domain, subdomains included (R-42). Six internal calls create, list and lift them; actors are named by
URL or handle (R-48) ([contracts/internal-api.md](contracts/internal-api.md)). Creating a block ends the follows both
ways with one `follow.removed` per row, cancels matching deliveries, and for user blocks sends `Block` (actor) or
`Reject`/`Undo` of the ended follows (domain) (R-40, R-41). Enforcement: the inbox refuses instance-blocked signers
with 403 before any key fetch, and handlers drop activities aimed at a user who blocks the signer (R-43); fetches
signed by a blocked key answer 404 (R-44); `Outbox.enqueueDeliveries` filters blocked inboxes and the delivery worker
re-checks instance blocks (R-45); outbound fetches to instance-blocked domains fail; reply and context collections
leave out blocked authors (R-47). User blocks lock the actor row like 003's delete (R-46). An inbound `Block` ends
the follows between its signer and the local actor, with `follow.removed` events (R-49). Interop on GoToSocial and
Mastodon (R-50).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged)

**Primary Dependencies**: unchanged (Oxpecker, raw Npgsql, System.Text.Json, .NET `RSA`)

**Storage**: Postgres; one new table, one replaced check constraint

**Testing**: xUnit v3 on Microsoft.Testing.Platform; integration with the fake app and the stub remote; interop on
GoToSocial (default suite) and Mastodon (`Suite=mastodon`)

**Target Platform**: Linux container, as before

**Project Type**: web service (federation port 8080, internal API port 8081)

**Performance Goals**: no measurable cost on the hot paths: every check is one indexed `EXISTS` on a small table

**Constraints**: refusal before any work for instance-blocked senders (FR-006); 404 never 403 (FR-008)

**Scale/Scope**: blocks are rare writes; a few thousand rows at most (imported domain lists)

No NEEDS CLARIFICATION remain: the four open points were decided in `/speckit-clarify`, the rest in
[research.md](research.md).

## Constitution Check

*Before Phase 0 and again after Phase 1 design: passes both times.*

| Gate | Answer |
| --- | --- |
| Protocol-free app | The app names an actor (URL or handle) or a domain; Megáfono does the protocol. Nothing app-specific. |
| Contract | Additive: six new calls, a new error code `blocked`, 409 on two existing calls for a new state, `follow.removed` with `outbound` and for new causes ([contracts/](contracts/)). |
| Privacy | Fetch 404 for blocked keys on every fetch path (claim-based, fails closed); deliveries filtered at enqueue and re-checked at send; tests try to break each rule (a former follower's signed fetch of a followers-only post after the block, a publish after the block, a delivery queued before an instance block). |
| Trust | Instance-blocked senders refused before any key fetch or parsing; a claim is only ever trusted to refuse. An inbound `Block` is acted on only when its actor is its verified signer. No outbound fetch to a blocked domain. |
| Identity | No id changes; `Block` and `Undo` get permanent activity ids. |
| Delivery | Block row, follow changes, events and the block's own activities commit together; repeats send nothing; restart-safe like every delivery. |
| Interop | GoToSocial and Mastodon: user block ends follows and drops a later reply; instance block refuses a reply and lookups; a remote block ends the follows in Megáfono. |
| Operations | No new dependency or operator step; the CLI wrapper is the separate FR-042 task. |
| Scope | Per-user and per-instance blocks are exactly the v1 moderation scope. |

## Design

- **`Infrastructure/Blocks.fs`** (new, after `Outbox.fs`): `normalizeDomain`, `hostName`, `domainMatches`,
  `coversOwnDomain`, `coveredSql localActorExpr actorIdExpr hostExpr` (the `EXISTS` fragment of data-model.md;
  `"NULL"` for instance blocks only), and the queries `isKeyRefused dataSource keyId localUser option` (R-43/R-44
  claim check), `isBlockedBy tx localUser actorId host`, `isInstanceBlocked dataSource actorId host`,
  `instanceBlocksDelivery dataSource inbox host`, `instanceBlocksHost dataSource host`.
- **`Database.fs`**: the table, indexes and constraint of data-model.md.
- **`ActivityStreams`**: `block` and `undoBlock` per contracts/federation.md.
- **`Outbox`**: filtered `enqueueDeliveries`, `enqueueDeliveriesUnfiltered` (R-45).
- **`RemoteActors`**: `RemoteError.Blocked`, refused in `fetchDocument` before connecting;
  `Internal/RemoteActors.resolutionError` maps it to 409 `blocked`.
- **`Internal/Blocks.fs`** (new): the six calls and the transactions of data-model.md; target parsing (URL, handle via
  `RemoteActors.resolveTarget`, domain via `normalizeDomain`).
- **`Internal/Follows.fs`**: `follow` answers 409 `blocked` (checked in the transaction after `guardLive`).
- **`Federation/Inbox.fs`**: instance claim check after `checkWithoutKey` → 403; signer check after `resolveSigner` →
  403; personal inbox of a user who blocks the signer → 202; dispatch `Block` to the new handler.
- **`Federation/InboxBlocks.fs`** (new, after `InboxFollows.fs`): `handleBlock` (R-49). `Undo{Block}` keeps going
  through `InboxFollows.handleUndo`, which already acknowledges it.
- **`InboxFollows.handleFollow`**: `Ignored` when the target blocks the signer (after `lockLiveActor`).
  **`InboxReplies.handleCreate`/`handleUpdate`**: lock the root author's row, drop when the root author or the local
  parent author blocks the signer.
- **`Federation/Http.fs`**: `claimedKeyId ctx`. **`Actors.fs`**, **`Objects.fs`**: 404 when `isKeyRefused`.
- **`Threads.fs`**: `repliesPage`/`contextPage` exclude covered replies (R-47).
- **`Workers/Deliveries.fs`**: `instanceBlocksDelivery` before sending → `Skipped "blocked"`.
- **`Program.fs`**: `Internal.Blocks.endpoints`.

## Project Structure

### Documentation (this feature)

```text
specs/004-blocks/
├── spec.md              # clarified 2026-10-15
├── plan.md              # this file
├── research.md          # R-40 to R-50
├── data-model.md
├── quickstart.md
├── contracts/           # internal-api.md, federation.md, app-contract.md
├── checklists/requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code (repository root)

```text
src/Megafono/
├── Infrastructure/Blocks.fs        # new
├── Internal/Blocks.fs              # new
├── Federation/InboxBlocks.fs       # new
└── touched: Infrastructure/{Database,Outbox,RemoteActors,Threads}.fs, Domain/ActivityStreams.fs,
    Federation/{Http,Actors,Objects,Inbox,InboxFollows,InboxReplies}.fs, Internal/{Follows,RemoteActors}.fs,
    Workers/Deliveries.fs, Program.fs, Megafono.fsproj
tests/Megafono.Tests/
├── Unit/BlocksTests.fs                         # new: normalization, matching, builders
├── Integration/BlockTests.fs                   # new: the calls and their effects
├── Integration/BlockEnforcementTests.fs        # new: inbox, handlers, fetches, collections, worker
└── Integration/SchemaTests.fs                  # extended
tests/Megafono.Interop.Tests/
└── BlockTests.fs                               # new: GoToSocial + Mastodon (Suite=mastodon)
```

Docs: `docs/operating.md`, `docs/vision.md` (FR-031 and FR-012 built), `README.md`, `CLAUDE.md`.

**Structure Decision**: the existing single-service layout; one new module per layer, as 002 and 003 did.

## Implementation status

Uncommitted partial code from an interrupted first run exists in the worktree (schema, `Blocks.fs`, builders, the
filtered enqueue, `RemoteError.Blocked`, part of `Internal/Blocks.fs`). It follows research R-40 to R-47 and is
reviewed and completed by the first implementation task, not discarded.

## Complexity Tracking

None.
