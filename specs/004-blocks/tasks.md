---

description: "Task list for 004-blocks"
---

# Tasks: Blocks

**Input**: Design documents from `specs/004-blocks/`: [plan.md](plan.md), [spec.md](spec.md) (clarified 2026-10-15),
[research.md](research.md) (R-40 to R-50), [data-model.md](data-model.md), [contracts/](contracts/),
[quickstart.md](quickstart.md)

**Tests**: REQUIRED (constitution Principle VI). Integration tests run against real Postgres and Kestrel with the fake
app (`tests/Megafono.Tests/Fixtures/FakeApp.fs`) and the stub remote server (`Fixtures/StubRemote.fs`); signed requests
come from `Fixtures/Signed.fs`, seeding from `Fixtures/Seed.fs`, publishing from `Fixtures/Publishing.fs`. Interop on
GoToSocial (default suite) and Mastodon (`Suite=mastodon`). Name tests after the spec's FR/SC ids (as
`ActorDeletionTests.fs` does). Write each story's tests first and see them fail before the implementation task. New
test files must be added to `tests/Megafono.Tests/Megafono.Tests.fsproj` (or the interop `.fsproj`) in compile order.

**Verification for every task**: `dotnet build`, `dotnet build -c Release` (no warnings) and
`dotnet test --project tests/Megafono.Tests` all green.

**Starting point**: checkpoint commit `4532b8b` already holds the schema, `Infrastructure/Blocks.fs`, the `Block`/`Undo`
builders, the filtered enqueue, `RemoteError.Blocked`, the 409 on follow and the six calls in `Internal/Blocks.fs`
(URL targets only). Tasks below that touch that code complete and test it; they do not rewrite it.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 to US5 of spec.md

---

## Phase 1: Setup

**Purpose**: confirm the baseline the tasks build on

- [X] T001 Baseline at `4532b8b`: `dotnet build`, `dotnet build -c Release` clean and `dotnet test --project tests/Megafono.Tests` 546/546 with `blocks` in the expected tables of `tests/Megafono.Tests/Integration/SchemaTests.fs` and `HostTests.fs`

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: the pieces every story uses. No story work starts before this phase is done.

- [X] T002 Schema tests in `tests/Megafono.Tests/Integration/SchemaTests.fs`: `blocks` rejects a row with both or neither of `actor`/`domain` (check violation), `ux_blocks_target` rejects a second identical user block and a second identical instance block (NULL owner) but allows the same target for two owners, `activities` accepts `activity_type = 'Block'`, and running `initializeSchema` twice keeps the replaced check (code in `src/Megafono/Infrastructure/Database.fs`, done)
- [X] T003 [P] Unit tests for `Blocks.normalizeDomain`, `hostName`, `domainMatches` in new `tests/Megafono.Tests/Unit/BlocksTests.fs`: lowercase, trailing dot, IDN to punycode (`bücher.example` → `xn--bcher-kva.example`), refusals (port, scheme, path, `@`, spaces, empty label, label over 63, leading/trailing `-`), `domainMatches "spam.example"` true for `spam.example`, `a.spam.example`, `a.spam.example:8443`, false for `notspam.example` and `spam.example.org` (FR-005)
- [X] T004 [P] Unit tests for `ActivityStreams.block` and `ActivityStreams.undoBlock` in new `tests/Megafono.Tests/Unit/BlockBuildersTests.fs`: documents exactly as `specs/004-blocks/contracts/federation.md` (context, id, type, actor, object, `to` the blocked actor; Undo embeds the Block with its id) (code in `src/Megafono/Domain/ActivityStreams.fs`, done)
- [X] T005 Target parsing in `src/Megafono/Internal/Blocks.fs` (R-48): `actor` accepts an `https` URL (http only when `RemoteActors.Env.AllowPrivateAddresses`) or a handle (`@user@host` / `user@host`, parsed with `RemoteActors.parseHandle`); a handle is resolved before any transaction with `RemoteActors.resolveTarget` (errors through `Internal/RemoteActors.resolutionError`, so an instance-blocked host answers 409 `blocked` with no request); a URL is used as given but, when `RemoteActors.getActor` returns the actor, its canonical `Id` is the stored target. Apply to user and instance POST and to both DELETEs (lift by handle resolves from the stored `remote_actors.handle` first, then `resolveTarget`)
- [X] T006 [P] `claimedKeyId (ctx: HttpContext) : string option` in `src/Megafono/Federation/Http.fs`: the `keyId` of the request's `Signature` header via `HttpSignatures.parseSignatureHeader`, None when absent or unparsable (used by T013, T017, T019)

**Checkpoint**: blocks can be created by URL or handle; helpers for enforcement exist.

---

## Phase 3: User Story 1 - A user blocks a remote account (Priority: P1) 🎯 MVP

**Goal**: alex blocks bob (by URL or handle): follows end both ways with `follow.removed`, bob's server gets one
`Block`, nothing from bob reaches alex, nothing of alex's goes to bob, bob's signed fetches of alex get 404, bob's
replies leave alex's collections.

**Independent Test**: with the stub remote as bob's server: block bob for alex, then check the follow rows, the events,
the `Block` the stub received, a Follow and a reply from bob (202, nothing recorded), a publish by alex (no delivery to
bob's inbox), signed fetches by bob (404), and the replies collection.

### Tests for User Story 1 (write first, see them fail)

- [X] T007 [P] [US1] Call tests in new `tests/Megafono.Tests/Integration/BlockTests.fs`: POST by URL → 201 with the block object; by handle → same block (200); pending and accepted follows in both directions become `removed` with one `follow.removed` per row carrying direction and actor (FR-003, FR-012); the stub remote receives exactly one signed `Block` at bob's own inbox and `blocks.block_activity` is set (FR-004, SC-006); a repeat sends nothing and adds no rows; a pending delivery of alex's to bob's own inbox becomes `skipped` with `last_error = 'blocked'` while one to the shared inbox stays (FR-007); a publish by alex afterwards queues nothing to bob's own inbox (SC-005); unreachable actor by URL → 201, no `Block`; handle on a down server → 502 `remote_unreachable`; 400 for both/neither field, a bad URL, an own-domain actor; 404 unknown user; 410 deleted user (003); `POST /internal/actors/alex/following` of bob → 409 `blocked` (FR-009); a user block of carol for alex leaves bob's other local users untouched
- [X] T008 [P] [US1] Enforcement tests in new `tests/Megafono.Tests/Integration/BlockEnforcementTests.fs` (user part): after alex blocks bob, a Follow from bob to alex through `/inbox` → 202, no follow row, no event; a reply Create to alex's post and an Update of bob's earlier reply → 202, not recorded, no `reply.received`/`reply.updated` (FR-006, FR-010); any activity to `/ap/actors/alex/inbox` signed by bob → 202 and dropped; the same Follow from bob to carol still works; GETs signed by bob's key of alex's actor, followers/following/outbox, a public object, a followers-only object bob could see before, its activity, `/replies` and `/ap/contexts/{id}` → 404 (FR-008, SC-004); unsigned and other-signer fetches unchanged; bob's earlier replies leave the replies and context collections of alex's posts (counts included) and come back after lifting (FR-011)

### Implementation for User Story 1

- [X] T009 [US1] Complete the user actor block path in `src/Megafono/Internal/Blocks.fs` so T007 passes (handles and canonical id from T005; everything else from `4532b8b`)
- [X] T010 [US1] `src/Megafono/Federation/InboxFollows.fs` `handleFollow`: after `Outbox.lockLiveActor`, `Blocks.isBlockedBy tx local.AppUserId signer.Id signer.Host` → roll back, `Ignored "blocked"` (R-46)
- [X] T011 [US1] `src/Megafono/Federation/InboxReplies.fs` `handleCreate` and `handleUpdate`: inside the transaction, `Outbox.lockLiveActor` on the thread's root author, then `Ignored "blocked"` when `isBlockedBy` holds for the root author or for the local parent author (`parent.LocalUser` / `row.ParentUser`)
- [X] T012 [US1] `src/Megafono/Federation/Inbox.fs`: for a personal inbox, after `resolveSigner` and the existing 404/410 refusal, drop with 202 when `Blocks.isKeyRefused` (or `isBlockedBy` on the signer) holds for that user, before the body is parsed
- [X] T013 [P] [US1] `src/Megafono/Federation/Actors.fs` (actor document and the three collections) and `src/Megafono/Federation/Objects.fs` (`guarded`, for objects, activities, replies, contexts): answer `notFound` when `claimedKeyId` is Some and `Blocks.isKeyRefused dataSource keyId (Some author)` (instance and user blocks), before any other work (R-44)
- [X] T014 [P] [US1] `src/Megafono/Infrastructure/Threads.fs` `repliesPage` and `contextPage`: count and list only replies not covered (`Blocks.coveredSql "ro.local_actor" "rp.remote_actor" "ra.host"`, joining the thread root `objects ro` and `remote_actors ra`; local replies are never covered) (R-47)

**Checkpoint**: US1 works alone: T007 and T008 (user part) green.

---

## Phase 4: User Story 2 - The operator blocks a whole server (Priority: P1)

**Goal**: an instance block of a domain (subdomains included) or of one actor cuts it off for every local user: 403
before any work, no deliveries, 404 fetches, no outbound fetches, follows of every user ended with events, nothing sent.

**Independent Test**: block the stub remote's domain for the instance; check every user's follows and events, the
skipped deliveries, an inbound POST (403, the stub saw no key fetch), signed fetches (404), a handle lookup (409).

### Tests for User Story 2

- [X] T015 [P] [US2] Call tests in `tests/Megafono.Tests/Integration/BlockTests.fs` (instance part): `POST /internal/blocks` with a domain → 201; follows of alex and carol with actors of that domain and of a subdomain become `removed` with events to each user (FR-002, FR-003); every pending delivery to those hosts, whoever the author, becomes `skipped`; nothing is queued to the blocked server (FR-004); an instance actor block covers only that actor; repeat → 200 and nothing new; own domain or a parent of it → 400; `GET /internal/remote-actors?handle=@x@blocked.example` and following such a handle → 409 `blocked`, and the stub remote saw no request (FR-009)
- [X] T016 [P] [US2] Enforcement tests in `tests/Megafono.Tests/Integration/BlockEnforcementTests.fs` (instance part): a POST to `/inbox` signed with a key on the blocked domain (or a subdomain) → 403 and the stub remote received no key fetch, also when the signature is invalid; an instance-blocked actor on another domain → 403 (FR-006); GETs signed by a blocked key of any local actor's documents and of `/ap/actor` → 404 (FR-008); a thread fetch of a note on the blocked domain is never made (FR-009); replies from the domain leave every collection (FR-011); a delivery inserted for the blocked host after the block (bypassing the enqueue filter, as a race would) is skipped by the worker with `last_error` "blocked" (FR-013, SC-005)

### Implementation for User Story 2

- [X] T017 [US2] `src/Megafono/Federation/Inbox.fs`: after `checkWithoutKey`, `Blocks.isKeyRefused dataSource parsed.KeyId None` → 403 before `resolveSigner`; after `resolveSigner`, `Blocks.isInstanceBlocked dataSource signer.Id signer.Host` → 403 (R-43); update the pipeline comment at the top of the module
- [X] T018 [P] [US2] `src/Megafono/Workers/Deliveries.fs` `process'`: before `skipReason`, `Blocks.instanceBlocksDelivery env.DataSource inbox host` → `Skipped "blocked"` (R-45 step 3)
- [X] T019 [P] [US2] `src/Megafono/Federation/Actors.fs` `instanceActor`: 404 when `claimedKeyId` is refused by instance blocks (`isKeyRefused … None`)
- [X] T020 [US2] Make T015 pass in `src/Megafono/Internal/Blocks.fs` (instance POST/DELETE with handles from T005; the rest from `4532b8b`)

**Checkpoint**: US1 and US2 both work; the MVP for public comment threads is complete.

---

## Phase 5: User Story 3 - A user blocks a whole server (Priority: P2)

**Goal**: alex blocks spam.example for alex only; the servers concerned get `Reject`/`Undo` of the ended follows.

**Independent Test**: alex has two followers and one follow on the stub remote's domain; block the domain for alex;
check the activities the stub receives and that carol is unaffected.

- [X] T021 [P] [US3] Tests in `tests/Megafono.Tests/Integration/BlockTests.fs`: user domain block → each inbound follow on the domain or a subdomain gets a `Reject` embedding its Follow id, each outbound one an `Undo{Follow}`, each at the actor's own inbox; the follows are `removed` with events; alex's pending deliveries to the domain are skipped, carol's are not; afterwards a Follow from that domain to alex is dropped and one to carol is handled; a signed fetch of alex's post by a key of that domain → 404 (FR-004 to FR-008)
- [X] T022 [US3] Make T021 pass in `src/Megafono/Internal/Blocks.fs` (domain path from `4532b8b`)

---

## Phase 6: User Story 4 - A remote account blocks a local user (Priority: P2)

**Goal**: an inbound `Block` from bob of alex ends the follows between them, with two `follow.removed`.

**Independent Test**: bob and alex follow each other; post a signed `Block` from bob; check the follow rows and events.

- [X] T023 [P] [US4] Tests in new `tests/Megafono.Tests/Integration/InboundBlockTests.fs`: a `Block` signed by bob with `object` alex (to the personal and to the shared inbox) → 202, both follows `removed`, one `follow.removed` per row, nothing queued for delivery (FR-011, FR-012); the same activity id again → nothing more; a `Block` whose `actor` is not the signer, whose object is another server's actor, an unknown local id or a deleted actor (003) → 202, nothing changes; an `Undo{Block}` → 202, no follow restored
- [X] T024 [US4] New `src/Megafono/Federation/InboxBlocks.fs` (compile after `InboxFollows.fs` in `src/Megafono/Megafono.fsproj`): `handleBlock env signer activity` per data-model.md "Inbound Block" (actor = signer, id on the signer's host, object a local actor via `localActorId`; one transaction with `markProcessed`, `Outbox.lockLiveActor`, follows in both directions to `removed`, `follow.removed` events); dispatch `"Block"` to it in `src/Megafono/Federation/Inbox.fs`. `Undo{Block}` keeps going through `InboxFollows.handleUndo` (already 202)

---

## Phase 7: User Story 5 - Blocks can be listed and lifted (Priority: P2)

**Goal**: list user and instance blocks; lift them; `Undo{Block}` for a lifted user actor block.

**Independent Test**: create blocks, list, lift each kind, check what the stub remote receives and that the blocked
side is handled again.

- [X] T025 [P] [US5] Tests in `tests/Megafono.Tests/Integration/BlockTests.fs`: `GET /internal/actors/alex/blocks` and `GET /internal/blocks` list each block newest first with `actor`/`domain`/`createdAt`; `DELETE …?actor=` (by URL and by handle) → 204 and the stub receives an `Undo` embedding the original `Block` id; `DELETE …?domain=` → 204, nothing sent; a second DELETE → 404; no follow is restored; after lifting, a Follow from bob reaches alex again and bob's signed fetch is answered; 400 without exactly one parameter; 404/410 for unknown/deleted users
- [X] T026 [US5] Make T025 pass in `src/Megafono/Internal/Blocks.fs` (lifting by handle from T005; listing and lifting from `4532b8b`)

---

## Phase 8: Polish & cross-cutting

- [ ] T027 Interop `tests/Megafono.Interop.Tests/BlockTests.fs` (R-50), on GoToSocial and, under `[<Trait("Suite", "mastodon")>]`, on Mastodon, using `Fixtures/FederationContext.fs`, `FediClient.fs` (add `block`/`unblock` client calls: `POST /api/v1/accounts/{id}/block` and `/unblock`) and `MegafonoInterop.fs`: (a) the remote user and an unlocked local actor follow each other; the app blocks the remote user by handle; poll the remote relationship until neither follow remains; a reply from the remote user to a local public post never produces `reply.received` (SC-001); (b) instance block of the remote server's domain: a reply never reaches the app and a remote lookup of a local actor fails; lift the block in a `finally` (SC-002); (c) the remote user blocks the local actor; within a minute both follows are `removed` in Megáfono and the app received two `follow.removed` (SC-003)
- [ ] T028 Record the interop observations (what GoToSocial 0.22.2 and Mastodon 4.7.3 did with `Block`, `Undo{Block}` and the 403) in `specs/004-blocks/research.md` R-40, R-43 and R-49
- [ ] T029 [P] Docs: `docs/operating.md` (a "Blocks" section: user and instance calls, handles vs URLs, what each kind sends, the 403/202/404 answers, the limits stated plainly: instance-key fetches, unsigned public fetches, what remote servers keep), `docs/vision.md` (FR-031 block and FR-012 inbound Block built in specs/004-blocks), `README.md` if it lists the internal API, `CLAUDE.md` (latest feature 004 and its modules)
- [ ] T030 Full verification per `specs/004-blocks/quickstart.md`: `dotnet build`, `dotnet build -c Release`, `dotnet test --project tests/Megafono.Tests`, the interop suite with `--filter-not-trait "Suite=mastodon"` and with `--filter-trait "Suite=mastodon"` for the new tests plus the existing follow, publish, replies and deletion ones; `scripts/count-integration-lines.sh` still passes; set spec status to Implemented

---

## Dependencies & execution order

- **Setup (T001)** → **Foundational (T002–T006)** → user stories.
- **US1 (P1)** and **US2 (P1)** depend only on Foundational. They share `Inbox.fs` (T012 and T017) and `Actors.fs`
  (T013 and T019), so their implementation tasks for those files run one after the other; their tests can be
  written in parallel.
- **US3** depends on US1's enforcement (T010–T014) for its "afterwards" checks. **US4** depends only on
  Foundational. **US5** depends on T005 (handles) and on US1's enforcement for its "handled again" checks.
- **Polish**: T027 needs US1, US2 and US4; T028 follows T027; T029 can start once US1/US2 are settled; T030 last.

```text
T001 → T002..T006 → ┬─ US1 (T007,T008 → T009..T014) ─┬─ US3 (T021 → T022)
                    ├─ US2 (T015,T016 → T017..T020) ─┤  US5 (T025 → T026)
                    └─ US4 (T023 → T024) ────────────┴─ T027 → T028, T029 → T030
```

## Parallel examples

- **Foundational**: T003 (`Unit/BlocksTests.fs`), T004 (`Unit/BlockBuildersTests.fs`) and T006 (`Federation/Http.fs`)
  together, while T002 and T005 run in sequence.
- **US1**: T007 and T008 (two test files) together; then T013 (`Actors.fs`, `Objects.fs`) and T014 (`Threads.fs`)
  alongside T010/T011 (`InboxFollows.fs`, `InboxReplies.fs`).
- **US2**: T015 and T016 together; T018 (`Deliveries.fs`) and T019 alongside T017 once T012 has landed in `Inbox.fs`.
- **Across stories**: US4 (new files `InboxBlocks.fs`, `InboundBlockTests.fs`) can run beside US1/US2, apart from its
  one-line dispatch in `Inbox.fs`.

Writing tasks in this repository run one at a time in the shared worktree (CodeAlta: one writing sub-agent at a
time unless each has its own worktree); "parallel" marks what does not conflict.

## Implementation strategy

1. **MVP = US1 + US2** (both P1): the protection public comment threads need. Deliver Foundational, then US1, verify
   T007/T008, then US2, verify T015/T016.
2. Add US4 (keeps follow lists right when remote users block), then US3 and US5.
3. Interop (T027) on GoToSocial first, then Mastodon; docs; full verification.
4. Commit per completed phase, in the repo's style (`feat(004): …`, `test(004): …`), ticking the tasks here.
