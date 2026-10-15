# Implementation Plan: Actor Deletion

**Branch**: `003-actor-deletion` (CodeAlta worktree branch `alta/russet-ocelot-q4dw`) | **Date**: 2026-10-15 |
**Spec**: [spec.md](spec.md)

## Summary

`DELETE /internal/actors/{id}` marks the actor deleted and, in the same transaction, queues one `Delete{actor}` per
server that knows it (R-31), cancels its queued deliveries and webhooks, deletes its follow rows and erases its
profile and post copies (R-35). Lookups of a deleted id answer a new `Deleted` result without asking the app, so the
actor document, collections and personal inbox answer 410 and the id is never revived (R-33). Every internal write
for an actor takes a share lock on a live row, so nothing is created after the `Delete` (R-34). Housekeeping clears
the keys once the `Delete` deliveries have ended (R-32). Design in [research.md](research.md) R-30 to R-35, tables in
[data-model.md](data-model.md), contracts in [contracts/](contracts/).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged) · **Dependencies**: unchanged · **Storage**: Postgres; two columns
become nullable, no new table · **Testing**: xUnit v3; integration with the stub remote; interop on GoToSocial
(every run) and Mastodon (`Suite=mastodon`) · **Scale**: one `Delete` per server; a few hundred servers at most
(SC-004 reference of 001)

## Constitution Check

*Before Phase 0 and again after Phase 1 design: passes both times.*

| Gate | Answer |
| --- | --- |
| Protocol-free app | One call, no ActivityPub in the app; nothing app-specific. |
| Contract | Additive: a new call and 410 answers for a state that did not exist before ([contracts/internal-api.md](contracts/internal-api.md)). |
| Privacy | Deleted followers-only objects stay 404 to everyone; the existing fetch guard is reused, and a test fetches one after deletion. |
| Trust | No new inbound input; the personal inbox of a deleted actor refuses after signature verification. No new outbound fetch. |
| Identity | The row is a permanent tombstone; lookups and profile storage refuse deleted ids (test with the app still answering). |
| Delivery | `Delete` and its deliveries commit with the deletion; repeats queue nothing; queued work of the actor is skipped under a row lock that serializes other writes. |
| Interop | GoToSocial and Mastodon: a follower's server drops the account after the `Delete`. |
| Operations | No new dependency or operator step; housekeeping does the key clearing. |
| Scope | US6 of the vision, in scope for v1. |

## Design

- **`AppClient`**: `LookupResult` gains `Deleted`. `ById` answers it for a row with `deleted_at` set, before the
  fresh-cache check and without calling the app. `ByHandle` never returns it (the handle may belong to a new id):
  an app answer whose id is deleted becomes `NotFound`. `store` returns `None` for a deleted id (`updateKnown`
  already filters; `insertNew`'s `ON CONFLICT … DO UPDATE` gets `WHERE local_actors.deleted_at IS NULL`), and
  `StoreIn` returns an option. A new `Outbox.lockLiveActor tx id : Task<bool>` (`FOR SHARE`, `deleted_at IS NULL`)
  is the write guard.
- **`Outbox.enqueueEvent`** returns `Task<string option>`: `None` (nothing queued) when the actor is deleted;
  callers already ignore the value.
- **`Internal/Actors.fs`**: the new `DELETE` handler and the transaction of data-model.md; `PUT` answers 410 for a
  deleted id.
- **`ActivityStreams`**: `deleteActor domain appUserId activityId now` and `actorTombstone domain appUserId
  formerType deleted`.
- **`Federation/Actors.fs`**: `find` returns `Live | Gone of type * deletedAt | Missing`; actor and collections
  answer 410 tombstone for `Gone`. `Federation/Inbox.fs`: personal inbox of a deleted actor → 410. Shared-inbox
  handlers treat `Deleted` like `NotFound`.
- **Internal writes** (`Internal/Objects.fs` publish/update/delete, `Internal/Follows.fs` follow/unfollow/answers/
  lists, `Internal/Actors.fs` refresh): `Deleted` → 410 `gone`; their transactions call `lockLiveActor`.
- **`Workers/Housekeeping.fs`**: `clearDeletedActorKeys dataSource : Task<string list>` (data-model.md), run with
  the hourly pruning; the ids are evicted from the delivery key cache, which becomes a DI singleton
  (`Deliveries.KeyCache`) shared by the delivery worker and housekeeping.
- **`Federation/Objects.fs`**: no change needed (objects are marked deleted); covered by tests.

## Project Structure

Touched: `src/Megafono/Infrastructure/{Database,AppClient,Outbox}.fs`, `Domain/ActivityStreams.fs`,
`Federation/{Actors,Inbox,InboxFollows,WebFinger}.fs`, `Internal/{Actors,Objects,Follows}.fs`,
`Workers/{Deliveries,Housekeeping}.fs`, `Program.fs`. New tests: `tests/Megafono.Tests/Integration/
ActorDeletionTests.fs`, `tests/Megafono.Interop.Tests/ActorDeletionTests.fs`; extended `SchemaTests.fs`,
`AppClientTests.fs`, a unit test for the builders. Docs: `docs/operating.md`, `docs/vision.md` (US6 decided),
`CLAUDE.md`, `README.md` if it lists the internal API.

## Complexity Tracking

None.
