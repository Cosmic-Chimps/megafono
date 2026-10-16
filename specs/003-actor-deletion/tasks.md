---

description: "Task list for 003-actor-deletion"
---

# Tasks: Actor Deletion

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md) (R-30 to R-35),
[data-model.md](data-model.md), [contracts/](contracts/)

**Tests**: REQUIRED (constitution Principle VI). Integration tests against Postgres and Kestrel with the fake app and
the stub remote server; interop on GoToSocial (every run) and Mastodon (`Suite=mastodon`). Name tests after the
spec's FR/SC ids. Verify with `dotnet build`, `dotnet build -c Release` and `dotnet test --project
tests/Megafono.Tests`.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Foundational

- [X] T001 Schema: `public_key_pem` and `enc_private_key` nullable in `src/Megafono/Infrastructure/Database.fs`
  (idempotent `ALTER … DROP NOT NULL`); extend `tests/Megafono.Tests/Integration/SchemaTests.fs`
- [X] T002 [P] `ActivityStreams.deleteActor` and `ActivityStreams.actorTombstone` per contracts/federation.md, with
  unit tests
- [X] T003 `AppClient`: `LookupResult.Deleted`; `ById` answers it from the stored row without calling the app;
  `ByHandle` turns an answer with a deleted id into `NotFound`; storing never revives or updates a deleted row
  (`StoreIn` returns an option); tests in `tests/Megafono.Tests/Integration/AppClientTests.fs` (the app still
  answers 200 for a deleted id: no new key, row unchanged, `Deleted`/`NotFound`)
- [X] T004 `Outbox.lockLiveActor` (FOR SHARE, live only) and `Outbox.enqueueEvent` returning `None` for a deleted
  actor; update every caller and every `LookupResult` match so the build is clean (`Deleted` treated as `NotFound`
  where nothing else is specified)

## Phase 2: User Story 1 - delete call and the Delete deliveries (P1) 🎯 MVP

- [X] T005 [US1] Integration tests first, in `tests/Megafono.Tests/Integration/ActorDeletionTests.fs`: one signed
  `Delete{actor}` per server for accepted and pending followers and accepted and pending follows, and none to a
  server that only received a delivery; shared inbox preferred, one per host (FR-002, FR-003); 202 body; repeat → same body and no new rows
  (FR-004); unknown id → 404; the call works with the user unknown to the fake app (FR-001)
- [X] T006 [US1] `DELETE /internal/actors/{id}` in `src/Megafono/Internal/Actors.fs`: the transaction of
  data-model.md (FOR UPDATE, inboxes, Delete activity and deliveries, skip other deliveries, drop events, objects
  and replies deleted and erased, activity bodies emptied, follows deleted, profile cleared, `deleted_at`)

## Phase 3: User Story 2 - a deleted actor stays gone (P1)

- [X] T007 [US2] Tests: actor, followers, following, outbox → 410 actor Tombstone with `formerType` (FR-005); signed
  POST to the personal inbox → 410; public object → 410, followers-only object → 404 even to a former follower's
  signed fetch (FR-007); WebFinger of the handle → 404 while the fake app still answers, and resolves a new id that
  takes the handle (FR-006); internal calls → 410 `gone` (FR-008)
- [X] T008 [US2] `Federation/Actors.fs` tombstones; `Federation/Inbox.fs` personal inbox 410; internal handlers 410
  in `Internal/Actors.fs`, `Internal/Objects.fs`, `Internal/Follows.fs`, with `lockLiveActor` in their write
  transactions (FR-012)

## Phase 4: User Story 3 - stop speaking and erase (P2)

- [X] T009 [US3] Tests: earlier pending deliveries become `skipped` with "actor deleted" (FR-009); queued webhooks
  dropped and none queued after, including when a `Delete` delivery fails for good; an inbound Follow through the
  shared inbox → 404 and no event (FR-010); follow rows gone, profile fields cleared, documents `{}`, Create/Update
  bodies empty (FR-011); keys kept while a `Delete` delivery is pending and cleared by
  `Housekeeping.clearDeletedActorKeys` once all ended (SC-004)
- [X] T010 [US3] `Housekeeping.clearDeletedActorKeys`, the shared delivery key cache (evicted on clearing), wired in
  `Program.fs`

## Phase 5: Interop and polish

- [ ] T011 Interop `tests/Megafono.Interop.Tests/ActorDeletionTests.fs`: on GoToSocial, and on Mastodon
  (`Suite=mastodon`), a remote user follows an unlocked local actor that published a public post; the app deletes
  the actor; poll until the remote server no longer has the account (lookup/search fails, relationship gone)
  (SC-001)
- [X] T012 [P] Docs: `docs/operating.md` (deleting a user, what remote servers keep: the limit stated plainly),
  `docs/vision.md` (US6 built in specs/003-actor-deletion), `CLAUDE.md` (latest feature), the README if it lists the
  internal API; `.specify/feature.json` points at this feature
- [ ] T013 Full verification: `dotnet build`, `dotnet build -c Release`, `dotnet test --project tests/Megafono.Tests`,
  interop GoToSocial and Mastodon suites for the new tests and the existing follow/publish ones
