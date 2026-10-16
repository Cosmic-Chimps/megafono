---

description: "Task list for 004-blocks"
---

# Tasks: Blocks

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md) (R-40 to R-47),
[data-model.md](data-model.md), [contracts/](contracts/)

**Tests**: REQUIRED (constitution Principle VI). Integration tests against Postgres and Kestrel with the fake app and
the stub remote server; interop on GoToSocial. Name tests after the spec's FR/SC ids. Verify with `dotnet build`,
`dotnet build -c Release` and `dotnet test --project tests/Megafono.Tests`.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Foundational

- [ ] T001 Schema: `blocks` table, indexes and the `activities` type check with `Block` in
  `src/Megafono/Infrastructure/Database.fs` (idempotent); extend `tests/Megafono.Tests/Integration/SchemaTests.fs`
- [ ] T002 [P] `ActivityStreams.block` and `ActivityStreams.undoBlock` per contracts/federation.md, with unit tests
- [ ] T003 `Infrastructure/Blocks.fs`: domain normalization and matching, the covered-SQL fragment and the queries of
  plan.md; unit tests for normalization and matching in `tests/Megafono.Tests/Unit/BlocksTests.fs`
- [ ] T004 `Outbox.enqueueDeliveries` filtered, `enqueueDeliveriesUnfiltered`; `RemoteError.Blocked` refused in
  `RemoteActors.fetchDocument`, mapped to 409 `blocked`; every match updated so the build is clean

## Phase 2: User Stories 1-4 - the block calls (P1/P2) 🎯 MVP

- [ ] T005 Integration tests first, `tests/Megafono.Tests/Integration/BlockTests.fs`: user actor block → 201, follows
  both ways `removed`, `follow.removed` events with directions, one signed `Block` to the actor's inbox, pending
  deliveries of the user to that inbox `skipped`; repeat → 200 and nothing new (FR-001, FR-003, FR-004, SC-005);
  unreachable actor → block recorded, no `Block`; user domain block → `Reject` per inbound and `Undo` per outbound
  follow, subdomain covered (FR-005); instance domain and actor blocks → every user's follows removed with events,
  every matching delivery skipped, nothing sent (FR-002); list; lift → 204, `Undo{Block}` for the actor block, no
  follow restored, 404 for none (US4); validation 400s; 404/410 for unknown/deleted users; follow of a blocked target
  → 409 (FR-010); a publish after the block queues nothing to the blocked inbox or domain (FR-008)
- [ ] T006 `Internal/Blocks.fs` and the 409 in `Internal/Follows.fs`; wire in `Program.fs`

## Phase 3: Enforcement (P1)

- [ ] T007 Integration tests, `tests/Megafono.Tests/Integration/BlockEnforcementTests.fs`: instance-blocked domain →
  403 with no key fetch (the stub remote sees no request) also for an invalid signature; instance-blocked actor →
  403 (FR-006); user block → Follow, reply Create/Update dropped with 202 and no event, personal inbox 202, another
  user unaffected (FR-007); fetches of actor, collections, public and followers-only objects, activities, replies and
  context signed by a blocked key → 404, by others unchanged (FR-009, SC-003); no outbound fetch to an
  instance-blocked domain (FR-010); collections leave out blocked replies and show them again after lifting (FR-011);
  a delivery queued before an instance block commits is skipped at send time (FR-012, SC-004)
- [ ] T008 `Federation/Inbox.fs`, `InboxFollows.fs`, `InboxReplies.fs`, `Federation/Http.fs`, `Actors.fs`,
  `Objects.fs`, `Threads.fs`, `Workers/Deliveries.fs`

## Phase 4: Interop and polish

- [ ] T009 Interop `tests/Megafono.Interop.Tests/BlockTests.fs` on GoToSocial: (a) a GoToSocial user and an unlocked
  local actor follow each other; the app blocks the user; poll until GoToSocial shows neither follow; a reply from
  the user to a local public post never reaches the app (SC-001); (b) instance block of the GoToSocial domain: a
  reply to a local public post never reaches the app, a GoToSocial lookup of a local actor fails, a new post is not
  delivered; the block is lifted in a `finally` (SC-002). Record observations in research.md R-40
- [ ] T010 [P] Docs: `docs/operating.md` (blocks: user and instance calls, what each sends, the limits stated
  plainly: instance-key fetches, unsigned public fetches, what remote servers keep), `docs/vision.md` (FR-031 block
  built in specs/004-blocks), `README.md` if it lists the internal API, `CLAUDE.md` (latest feature);
  `.specify/feature.json` points at this feature
- [ ] T011 Full verification: `dotnet build`, `dotnet build -c Release`, `dotnet test --project tests/Megafono.Tests`,
  interop GoToSocial suite for the new tests and the existing follow/publish/replies ones
