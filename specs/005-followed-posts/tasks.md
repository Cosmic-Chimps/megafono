---

description: "Task list for 005-followed-posts"
---

# Tasks: Posts from followed accounts (the app's feed)

**Input**: Design documents from `specs/005-followed-posts/`: [plan.md](plan.md), [spec.md](spec.md) (clarified
2026-10-18), [research.md](research.md) (R-51 to R-60), [data-model.md](data-model.md), [contracts/](contracts/),
[quickstart.md](quickstart.md)

**Tests**: REQUIRED (constitution Principle VI). Integration tests run against real Postgres and Kestrel with the fake
app (`tests/Megafono.Tests/Fixtures/FakeApp.fs`) and the stub remote server (`Fixtures/StubRemote.fs`); signed requests
come from `Fixtures/Signed.fs` (`note`, `setAddressing`, `createNote`, `createNoteWithContext`, `updateNote`,
`deleteNote`), seeding from `Fixtures/Seed.fs` and `Fixtures/Publishing.fs`. Interop on GoToSocial (default suite) and
Mastodon (`[<Trait("Suite", "mastodon")>]`). Name tests after the spec's FR/SC ids, as `InboundRepliesTests.fs` does.
Write each story's tests first and see them fail before its implementation task. New test files go into
`tests/Megafono.Tests/Megafono.Tests.fsproj` (or the interop `.fsproj`) in compile order.

**Verification for every task**: `dotnet build`, `dotnet build -c Release` (no warnings) and
`dotnet test --project tests/Megafono.Tests` all green; 002's reply tests (`InboundRepliesTests`,
`InboundReplyChangesTests`, `CapturedRepliesTests`) unchanged and green (SC-005).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 to US4 of spec.md

---

## Phase 1: Setup

**Purpose**: confirm the baseline

- [X] T001 Baseline on `alta/silent-olive-bf5b` (main at `6d572bc`): `dotnet build`, `dotnet build -c Release` clean and `dotnet test --project tests/Megafono.Tests` green; record the test count in this task's line (baseline: 668 tests, 0 failed; both builds 0 warnings)

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: schema, event shapes, the shared note reading and the single-read dispatch every story uses. No story
work starts before this phase is done.

- [X] T002 Schema in `src/Megafono/Infrastructure/Database.fs` per data-model.md: `feed_posts` (PK `(id, local_actor)`, checks on `audience` and `state`) and `extension_namespaces`, created with its one-time backfill from `objects.document->'@context'` inside a `DO` block guarded by the table's absence (string-valued terms of object elements, Megáfono's own terms `ostatus`, `conversation`, `gts`, `interactionPolicy`, `canReply`, `automaticApproval` left out)
- [X] T003 Schema tests in `tests/Megafono.Tests/Integration/SchemaTests.fs`: add both tables to the expected table list (and to `HostTests.fs` if it lists tables); `feed_posts` rejects a bad `audience`/`state` and a second row for the same `(id, local_actor)` but accepts the same post for two users; the backfill: drop `extension_namespaces`, insert an object whose `@context` declares `{"app":"https://app.example/ns#"}`, run `Database.initializeSchema` → the namespace is in the table and `gts`/`ostatus` values are not; running `initializeSchema` again changes nothing
- [X] T004 [P] Events in `src/Megafono/Domain/Events.fs`: `PostReceived`/`PostUpdated`/`PostDeleted` (`post.received`, `post.updated`, `post.deleted`); a `PostObject` record (id, url, type, content, summary, sensitive, language, published, updated, audience, `InReplyTo: string option`, attachments); `postBody eventType actor post extensions` and `postDeletedBody actor postId` (object `{ id }`); share the common note fields with `replyBody` through one private helper so `reply.received`'s JSON stays byte-for-byte the same (contracts/app-contract.md)
- [X] T005 [P] Unit tests in `tests/Megafono.Tests/Unit/EventsTests.fs`: `post.received` envelope has every field of the contract with `inReplyTo` null when absent and no `parent`/`thread`; `post.updated` has `updated` set; `post.deleted` has only `object.id`; `extensions` passes through or is null; an existing `reply.received` expectation still matches exactly
- [X] T006 New `src/Megafono/Federation/InboxNotes.fs` (compile after `InboxCommon.fs` in `src/Megafono/Megafono.fsproj`): move from `InboxReplies.fs` the note-type check, `isNoteUpdate`, the object resolution (`noteOf`: embedded when on the signer's host, else `RemoteActors.fetchObject` with id match; Note/Article attributed to the signer), `iris`, `time`, `flag`, `urlOf`, `attachments`, `language`, `audienceOf`; add `addressedTo (activity) (note) : string list` (the note's `to`+`cc`, else the activity's) and a pure `extensionData (namespaces: Set<string>) (activityContext) (note) : JsonNode option` that 002's `extensionsOf` now calls with the thread root's namespaces. `InboxReplies.fs` uses the moved functions; no behaviour change (002 tests green)
- [X] T007 Split `src/Megafono/Federation/InboxReplies.fs` for one read (R-54): `createReply env signer activity note` (from `handleCreate` after the object is resolved), `lookupReply env replyId` (exposed), `updateReply env signer activity note earlier` (from `handleUpdate` after the lookup and the read), `handleDelete` unchanged; move the precondition checks (activity id, actor = signer, id on the signer's host) and the read into a dispatch in `src/Megafono/Federation/Inbox.fs` (`Create` → read once → `createReply`; `Update{Note}` → lookup, read once → `updateReply`; `Delete` → `handleDelete`), with an outcome combinator (`Handled` if either path handled, else `Ignored` with both reasons) ready for the feed path. All 002 tests green
- [X] T008 New `src/Megafono/Infrastructure/FeedPosts.fs` (compile after `Threads.fs`): the queries of plan.md "Feed path": `followerCandidates tx signer` (live users with an `accepted` outbound follow), `lockUsers tx users` (`… WHERE app_user_id = ANY(@users) AND deleted_at IS NULL ORDER BY app_user_id FOR NO KEY UPDATE`, returns the locked ids), `qualifying tx users signer host mustFollow` (still following when `mustFollow`, not covered by `Blocks.coveredSql "u" "@signer" "@host"`), `insertRows tx postId signer audience published users` (`ON CONFLICT DO NOTHING RETURNING local_actor`, sorted), `liveRows tx postId signer`, `markUpdated`, `markDeleted`; `recordNamespace tx ns` and `knownNamespaces tx (candidates: string list) : Set<string>`
- [X] T009 [P] Fixture `addFollowing db localUser stub name status` in `tests/Megafono.Tests/Fixtures/Publishing.fs` (an `outbound` follow row and the stub actor's `remote_actors` row with its real key, like `addFollower`), and a `feedNote`/`createPost` helper in `tests/Megafono.Tests/Fixtures/Signed.fs` if `note` cannot express a post without `inReplyTo` (a null `inReplyTo` omitted from the JSON)

**Checkpoint**: schema, events and the shared reading exist; 002 behaves exactly as before.

---

## Phase 3: User Story 1 - A followed account's new post reaches the app (Priority: P1) 🎯 MVP

**Goal**: a `Create{Note|Article}` by an account local users follow (accepted) produces one `post.received` per
qualifying user, recipients from follows and addressing only, once per post id, with sanitized content and extensions.

**Independent Test**: with the stub remote as bob's server, alex and carol following bob: post public, unlisted,
followers-only and direct notes from bob and check exactly which users get which events (quickstart.md §1).

### Tests for User Story 1 (write first, see them fail)

- [ ] T010 [P] [US1] New `tests/Megafono.Tests/Integration/InboundPostsTests.fs` (US1 part): alex and carol follow bob (accepted): a public `Create` on `/inbox` → 202 and one `post.received` for each with every field of the contract (sanitized content, summary as text, `sensitive`, `language` from `contentMap`, `published`, `updated` null, `audience`, `inReplyTo` null, attachment URLs), signature valid (FR-001, FR-007, R-51); unlisted and followers-only → `audience` `unlisted`/`followers` (FR-002); dave pending, rejected or removed and erin following nobody → nothing for them (FR-009); a direct post addressed to alex only → alex gets it with `audience` `direct`, carol nothing (FR-002); a direct post addressed to erin (no follow) → nothing at all; the same `Create` twice, and the same post under another activity id on `/ap/actors/carol/inbox` → still one event per user (FR-004, SC-003); a reply by bob to a third party's post → `post.received` with `inReplyTo` (FR-012); a `Question` or `Page` → nothing; a `Create` whose object is embedded with another host's id → fetched from its origin (stub) and attributed check applies; bob's post declaring a namespace that alex published with (publish with `extensions` first) under another prefix → `extensions` `{namespace, prefix, data}`; any other namespace → null (FR-008); an author nobody follows → 202, no row in `feed_posts`

### Implementation for User Story 1

- [ ] T011 [US1] New `src/Megafono/Federation/InboxPosts.fs` (compile after `InboxReplies.fs`, before `Inbox.fs`): `createPost env signer activity note` per plan.md "Feed path / create" (R-52, R-55, R-57): audience via `InboxNotes.audienceOf`; candidates (for `direct`, only users whose `Ids.actorId domain user` is in `addressedTo`); none → `Ignored`; one transaction: `lockUsers`, `qualifying … mustFollow=true`, `insertRows`, extensions once from `knownNamespaces`, `Outbox.enqueueEvent` per returned user in sorted order with `Events.postBody PostReceived`; commit; `Handled` when a row was inserted
- [ ] T012 [US1] Wire `createPost` into the `Create` dispatch of `src/Megafono/Federation/Inbox.fs` after the reply path, sharing the one read; combine the outcomes
- [ ] T013 [P] [US1] Record the app's namespaces in `src/Megafono/Internal/Objects.fs`: in the publish and the update transactions, `FeedPosts.recordNamespace tx parsed.Extensions.Namespace` when `Extensions` is Some (R-58); test in `tests/Megafono.Tests/Integration/PublishTests.fs` (or `UpdateDeleteTests.fs`) that a publish with `extensions` adds the row and a repeat adds none

**Checkpoint**: US1 works alone: T010 green; 002 tests green.

---

## Phase 4: User Story 2 - Edits and deletions follow the post (Priority: P1)

**Goal**: `Update` → `post.updated` to recipients who still follow; `Delete` → `post.deleted` to every recipient;
GoToSocial's reused `Update` ids handled; nothing fetched for unknown posts.

**Independent Test**: after US1's post reached alex and carol, send edits and a delete from bob, with alex unfollowing
in between (quickstart.md §1).

### Tests for User Story 2

- [ ] T014 [P] [US2] `tests/Megafono.Tests/Integration/InboundPostsTests.fs` (US2 part): an `Update{Note}` of bob's post → `post.updated` for alex and carol with the new content, `updated` set, the original `audience` and `published` (FR-005); the same activity id with two different `updated` times → two events per user; the same edit twice → one (R-55); alex's follow `removed`, then an edit → only carol, then a `Delete` (IRI, and in another test a `Tombstone`) → both alex and carol get one `post.deleted` with `object.id` (FR-006, clarified Q4); a later `Update` or a repeated `Delete` → nothing; an `Update`/`Delete` of a post never passed on, or signed by carl for bob's post → nothing and the stub saw no fetch for the unknown `Update` (FR-005, R-54); an `Update` whose re-read object has another id → nothing

### Implementation for User Story 2

- [ ] T015 [US2] `src/Megafono/Federation/InboxPosts.fs`: `lookupPost env postId signer` (live rows exist, no lock), `updatePost env signer activity note` (lock row users sorted; marker `post|{updateId}|{updated}` via `InboxCommon.markProcessed`; rows' `updated_at`; `post.updated` with stored audience/published to users with `qualifying … mustFollow=true`) and `deletePost env signer activity` (lock sorted; all live rows → `deleted`; `post.deleted` to users with `qualifying … mustFollow=false`) per plan.md (R-55, R-56, R-57)
- [ ] T016 [US2] `src/Megafono/Federation/Inbox.fs`: `Update{Note}` dispatch looks up the reply and the feed rows before any fetch, reads the object once when either exists (id must match), runs `updateReply` and/or `updatePost`; `Delete` runs `InboxReplies.handleDelete` and `InboxPosts.deletePost`; outcomes combined

**Checkpoint**: US1 and US2 together: the feed is complete for ordinary posts.

---

## Phase 5: User Story 3 - Blocks, deleted users and threads are respected (Priority: P1)

**Goal**: 003/004 rules hold on the feed path, and a reply into one of the app's threads reaches its root author only
as 002's reply events.

**Independent Test**: integration tests with a user block next to an accepted follow, an instance block, a deleted
user, and a reply by bob to alex's published post with carol also following bob.

### Tests for User Story 3

- [ ] T017 [P] [US3] `tests/Megafono.Tests/Integration/InboundPostsTests.fs` (US3 part): alex blocks bob through `POST /internal/actors/u-1/blocks` (follow ends) → bob's next post reaches carol only; a block row seeded next to alex's still-accepted follow (actor block, and in another case a domain block) → nothing for alex on post, edit or delete, carol as usual, and alex's row still turns `deleted` on the Delete (R-56); an instance block of bob's domain → 403 and no event (FR-009); alex deleted through `DELETE /internal/actors/u-1` after following bob → nothing for alex, carol as usual; bob replies to alex's published post (alex and carol follow bob) → alex gets `reply.received` only and no `feed_posts` row, carol gets `post.received`; bob's edit and delete of it → alex `reply.updated`/`object.deleted`, carol `post.updated`/`post.deleted` (FR-003, R-53); a reply by bob to a deleted post of alex's (`Gone`) → an ordinary feed post for alex and carol
- [ ] T018 [P] [US3] A concurrency test in `tests/Megafono.Tests/Integration/InboundPostsTests.fs`: two posts from two followed authors with overlapping local followers sent at the same time (and one alongside an instance block of a third domain) complete without a deadlock error and with every expected event (R-57)

### Implementation for User Story 3

- [ ] T019 [US3] `src/Megafono/Federation/InboxPosts.fs` `createPost`: leave out the root author when `Threads.resolveParent tx domain inReplyTo` gives `Resolved` (R-53); confirm the block filter and the deleted-user path from T008/T011/T015 make T017 and T018 pass; fix the lock order if T018 shows a deadlock

**Checkpoint**: all P1 stories pass; privacy tests of SC-004 green.

---

## Phase 6: User Story 4 - Boosts are not passed on (Priority: P3)

**Goal**: `Announce` from a followed account is acknowledged and dropped (R-59).

- [ ] T020 [P] [US4] Test in `tests/Megafono.Tests/Integration/InboundPostsTests.fs`: an `Announce` signed by bob (of his own post and of a third party's) → 202, no event, nothing fetched, no `feed_posts` row (FR-012). No code change is expected; if the test fails, keep `Announce` unhandled in `src/Megafono/Federation/Inbox.fs`

---

## Phase 7: Polish & cross-cutting

- [ ] T021 New interop `tests/Megafono.Interop.Tests/FeedTests.fs` (add to the interop `.fsproj` after `BlockTests.fs`), on GoToSocial and, under `[<Trait("Suite", "mastodon")>]`, on Mastodon, with `Fixtures/FederationContext.fs`, `FediClient.fs` (`PostStatus`, `EditStatus`, a delete call if missing: `DELETE /api/v1/statuses/{id}`) and `MegafonoInterop.fs`: a local user follows the remote account through `POST /internal/actors/{id}/following` with `{"target": "@user@host"}` (approve on the remote side when locked) and waits for `follow.accepted`; the account posts public and followers-only, edits, deletes → `post.received` ×2, `post.updated`, `post.deleted` for that user within 30 s each (SC-001); a second local user who does not follow gets nothing (SC-002); after the user blocks the account, its next post produces no event (SC-002). Run with `Megafono:Testing:CaptureInbound` as the existing interop tests do
- [ ] T022 Copy the captured `Create`, `Update` and `Delete` of a followed account's post from both servers into `tests/Megafono.Tests/Fixtures/payloads/` (`gts-post-create.json`, …, `mastodon-post-delete.json`) and add `tests/Megafono.Tests/Integration/CapturedPostsTests.fs` replaying them with their real signatures, as `CapturedRepliesTests.fs` does: each produces the expected feed event for a seeded follower of the captured actor; also replay the existing `gts-reply-create.json` and `mastodon-reply-create.json` with a seeded follower who is not the thread's author → `post.received` with `inReplyTo` (Principle VI)
- [ ] T023 Record what GoToSocial 0.22.2 and Mastodon 4.7.3 sent (inbox used, addressing of followers-only and direct posts, `Update` ids and `updated`, `Delete` shape) in `specs/005-followed-posts/research.md` R-60 "Observed"
- [ ] T024 [P] Docs: `docs/operating.md` (a "Posts from followed accounts" section: who gets which event, per-user fan-out, edits vs deletions after an unfollow, thread replies, `extensions` and the app's namespaces, boosts and backfill not supported, the followers-only limitation), `docs/vision.md` (FR-012 Create from followed accounts built in specs/005-followed-posts; the webhook event list gains `post.received`, `post.updated`, `post.deleted`; the inbox-query open question note: still events only), `README.md` if it lists webhook events, `CLAUDE.md` (latest feature 005 and its modules)
- [ ] T025 Full verification per `specs/005-followed-posts/quickstart.md`: `dotnet build`, `dotnet build -c Release`, `dotnet test --project tests/Megafono.Tests`, the interop suite with `--filter-not-trait "Suite=mastodon"` and `--filter-trait "Suite=mastodon"` (new `FeedTests` plus the existing replies, follow and block tests); `scripts/count-integration-lines.sh` still passes; set the spec's status to Implemented and tick `checklists/requirements.md` notes if needed

---

## Dependencies & execution order

- **Setup (T001)** → **Foundational (T002–T009)**: T002 → T003; T004 → T005; T006 → T007 (both touch
  `InboxReplies.fs`/`Inbox.fs`); T008 after T002; T009 any time.
- **US1** needs all of Foundational. **US2** needs US1's `InboxPosts.fs` and rows. **US3** needs US1 and US2 (its
  tests cover posts, edits and deletes). **US4** needs only Foundational (a test).
- **Polish**: T021 needs US1–US3; T022 and T023 follow T021; T024 once the stories are settled; T025 last.

```text
T001 → T002 → T003      ┐
       T004 → T005      ├→ US1 (T010 → T011 → T012, T013) → US2 (T014 → T015 → T016) → US3 (T017, T018 → T019)
       T006 → T007      │                                                              → T021 → T022, T023 → T025
       T008, T009       ┘   US4 (T020) any time after Foundational                       T024 ┘
```

## Parallel examples

- **Foundational**: T004/T005 (`Events.fs`, `EventsTests.fs`) and T009 (fixtures) beside T002/T003 (schema), while
  T006 → T007 run in sequence (same files).
- **US1**: T010 (tests) beside T013 (`Internal/Objects.fs`); T011 then T012 (`InboxPosts.fs`, then `Inbox.fs`).
- **US3/US4**: T017, T018 and T020 are test additions to one file: write them in one pass.

Writing tasks run one at a time in the shared worktree (CodeAlta: one writing sub-agent at a time unless each has its
own worktree); "parallel" marks what does not conflict.

## Implementation strategy

1. **MVP = US1**: Foundational, then US1; verify T010 and the 002 suite. A feed of new posts is usable on its own.
2. Add US2 (edits and deletions) and US3 (blocks, deleted users, thread replies): all P1, needed before release.
3. US4 is a guard test.
4. Interop on GoToSocial, then Mastodon; captured payloads; docs; full verification.
5. Commit per completed phase, in the repo's style (`feat(005): …`, `test(005): …`, `docs(005): …`), ticking the tasks
   here.
