---

description: "Task list for 002-replies-threads"
---

# Tasks: Replies and Threads

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md) (R-20 to R-27),
[data-model.md](data-model.md), [contracts/](contracts/) (agreed with Frunchy, 2026-10-10), [quickstart.md](quickstart.md)

**Tests**: REQUIRED (constitution Principle VI). Integration tests against Postgres and Kestrel with the stub
remote server; interop on GoToSocial (every run) and Mastodon (`Suite=mastodon`). Write each story's tests first.
Name tests after the spec's FR/SC ids. Verify with `dotnet build` **and** `dotnet build -c Release`.

**Base**: branch `002-replies-threads` (from 001's final commit). Reuse 001's modules: `Outbox`, `Deliveries`,
`Events`/`Outbox.enqueueEvent`, `RemoteActors`, `HttpSignatures`, `Sanitizer`, `Federation/Http.fs`, the inbox
pipeline (`Inbox.fs`, `InboxCommon.fs`), and the interop fixtures (`MegafonoInterop.fs`, shared instance key,
unique app user ids per test).

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Foundational (blocks every story)

- [X] T001 Extend the schema in `src/Megafono/Infrastructure/Database.fs` per data-model.md (`objects.in_reply_to`, `objects.thread_root`, the `replies` table with its check that exactly one of `local_object`/`remote_actor` is set, and its indexes), idempotent on existing databases; extend `tests/Megafono.Tests/Integration/SchemaTests.fs`
- [X] T002 [P] Add inbound audience parsing to `src/Megafono/Domain/Audience.fs` (public if Public in `to`; unlisted if in `cc`; followers if `to` holds the author's followers URL; else direct; R-22) with unit tests in `tests/Megafono.Tests/Unit/AudienceTests.fs`
- [X] T003 [P] Add builders to `src/Megafono/Domain/ActivityStreams.fs`: `replies` Collection with an embedded first `CollectionPage` (`items` as ids, `next` URL), further pages, the context collection, `context`/`conversation` on every object, `interactionPolicy.canReply` = Public, and a Mention tag (`href`, `name: "@user@host"`) (R-20, R-23, R-24); unit tests in `tests/Megafono.Tests/Unit/RepliesBuildersTests.fs`
- [X] T004 [P] Add event payloads `reply.received`, `reply.updated`, `object.deleted` to `src/Megafono/Domain/Events.fs` per contracts/app-contract.md (sanitized content, `summary` as text, `sensitive` default false, `parent`, `thread`, `audience`, attachments); unit tests in `tests/Megafono.Tests/Unit/EventsTests.fs`
- [X] T005 Create `src/Megafono/Infrastructure/Threads.fs`: `resolveParent` (one of our object IRIs → that object and its root; else a live recorded reply → its root; deleted → `Gone`; else `Unknown`), `recordRemoteReply`, `recordLocalReply`, `markDeleted`, and the collection query (local first, then remote, oldest first, keyset pages of 50, public/unlisted, live); integration tests in `tests/Megafono.Tests/Integration/ThreadsTests.fs`
- [X] T006 Extend `tests/Megafono.Tests/Fixtures/StubRemote.fs` so a stub can send signed `Create{Note}` replies (with `inReplyTo`, `to`/`cc`, `summary`, `sensitive`, attachments, an extension), `Update{Note}` and `Delete` (bare id and Tombstone), and serve the Note at its id

**Checkpoint**: foundation ready.

---

## Phase 2: User Story 1 - Replies from the fediverse reach the app (P1) 🎯 MVP

**Independent Test**: real GTS and Mastodon accounts reply to a Group's thread post; the fake app receives
`reply.received`; an edit gives `reply.updated`; a delete gives `object.deleted`.

- [X] T007 [P] [US1] Integration tests in `tests/Megafono.Tests/Integration/InboundRepliesTests.fs`: a reply to our object → one `reply.received` to the root's author with every contract field; a reply to a recorded reply → same thread, `parent.local=false`; a reply to an unknown or deleted object → 202 and no event; `attributedTo` ≠ signer → refused; duplicate delivery → one event; a followers-only reply → event with `audience: followers`; content sanitized, `summary` text, `sensitive`; extension under the app's namespace passed through (FR-001 to FR-003, FR-006)
- [X] T008 [P] [US1] Integration tests in `tests/Megafono.Tests/Integration/InboundReplyChangesTests.fs`: `Update` by the author → `reply.updated` with new content; by someone else, of an unknown or deleted reply → ignored; `Delete` (id or Tombstone) by the author → `object.deleted` once; repeat → nothing (FR-004, FR-005)
- [X] T009 [US1] Implement `src/Megafono/Federation/InboxReplies.fs`: `Create{Note|Article}` (embedded object trusted only on the signer's host, else fetched signed; `Threads.resolveParent`; record + event + dedup marker in one transaction), `Update{Note|Article}` and `Delete` of recorded replies, all authorized against the author
- [X] T010 [US1] Dispatch `Create`, `Update` of a Note/Article and `Delete` from `src/Megafono/Federation/Inbox.fs` to `InboxReplies` (keep `Update{Person}` going to `InboxActors`); make T007–T008 pass
- [X] T011 [US1] Interop tests in `tests/Megafono.Interop.Tests/RepliesTests.fs` (GTS every run; Mastodon variant with `Suite=mastodon`): a Group (`type: Group`, unlocked) publishes a public thread post; a remote user resolves it and replies with a content warning → `reply.received` within 60 s; another remote user replies to that reply → `reply.received` with `parent.local=false`; the first edits → `reply.updated`; deletes → `object.deleted` (SC-001, SC-006)

**Checkpoint**: comments from the fediverse reach the app.

---

## Phase 3: User Story 2 - The app's users reply into threads (P1)

**Independent Test**: a local user replies to the Group's thread post and to a remote reply; a remote follower sees
it threaded and the remote author receives it.

- [X] T012 [P] [US2] Integration tests in `tests/Megafono.Tests/Integration/OutboundRepliesTests.fs`: `inReplyTo` unknown → 400, deleted → 410, audience `followers`/`direct` → 400; `to`/`cc` mirrored per R-23; Mention of a remote parent author; `context`/`conversation` = the root's context; `inReplyTo` on the object; recipients = replier's followers' inboxes + the remote parent author's personal inbox (exactly once each); 201 includes `thread`; a local reply is recorded and appears in the parent's collection; PUT/DELETE of a reply go to the same recipients (FR-010 to FR-013)
- [X] T013 [US2] Implement reply publishing in `src/Megafono/Internal/Objects.fs` (validation via `Threads.resolveParent`, addressing, Mention, recipients via `Outbox`, `objects.in_reply_to`/`thread_root`, `Threads.recordLocalReply`, `thread` in the response; same recipients for Update/Delete of a reply); make T012 pass
- [X] T014 [US2] Interop tests in `tests/Megafono.Interop.Tests/RepliesTests.fs`: a GTS user follows local user alex; alex replies to the Group's thread post with a content warning → the GTS follower sees it with `in_reply_to_id` set and the warning; alex replies to a GTS user's reply → that GTS user sees it (mention/notification) threaded under their reply; Mastodon variant (SC-002)

---

## Phase 4: User Story 3 - A reader sees the whole thread (P2)

**Independent Test**: a Mastodon account following nobody in the thread opens it and sees every public reply.

- [ ] T015 [P] [US3] Integration tests in `tests/Megafono.Tests/Integration/RepliesCollectionTests.fs`: shape per contracts/federation.md (Collection, embedded first page, `items` as ids, `next` only when more); 120 replies → 3 pages of ≤50; local replies first; followers-only, direct and deleted replies never listed; a followers-only object's collection is 404 to the unauthorized and served to an accepted follower; `/ap/contexts/{root}` lists root and public replies; every object document carries `replies`, `context`, `conversation`, `interactionPolicy` (FR-020 to FR-023, SC-004)
- [ ] T016 [US3] Implement in `src/Megafono/Federation/Objects.fs`: embed `replies` (first page) in served documents (computed at serve time, not stored), `GET /ap/objects/{id}/replies[?page=N]` and `GET /ap/contexts/{rootId}` with the object's fetch rules (and its rate-limit hook); make T015 pass
- [ ] T017 [US3] Interop test (Mastodon, `Suite=mastodon`) in `tests/Megafono.Interop.Tests/ThreadLoadingTests.fs`: a thread with a local reply and a GTS reply; a third Mastodon account that follows nobody resolves the thread post and reads `/api/v1/statuses/:id/context` → sees the local reply at once (Create-time fetch); the GTS reply appears after the crawl (Mastodon fetches all replies only for statuses ≥ 5 min old: mark this assertion `Explicit` with `Suite=mastodon-slow` if it needs the wait, and record the measured behavior in research.md R-20) (SC-003)
- [ ] T018 [US3] Interop test: the test host serves an "episode page" with the alternate link and `Link` header to the thread post; pasting it into Mastodon search finds the post; on GTS, searching the post's own URL finds it (SC-005)

---

## Phase 5: User Story 4 - Series and other non-user accounts (P2)

- [X] T019 [P] [US4] Interop test in `tests/Megafono.Interop.Tests/GroupActorTests.fs`: GTS and Mastodon find a `Group` actor (Mastodon's API reports `group: true`), follow it (accepted at once), and its next public post reaches the follower's home timeline (FR-030)

---

## Phase 6: Polish

- [ ] T020 [P] Document replies in `docs/operating.md` and `README.md`: what the app receives and sends, that a reply hidden or removed by the app stays on other servers (FR-041), that pasted page URLs resolve on Mastodon only, and that replies which never reach Megáfono are not seen
- [ ] T021 [P] Record 002 in `docs/vision.md` (US4 replies done, likes/mentions still open) and in `CLAUDE.md` (active feature, new modules)
- [ ] T022 Run [quickstart.md](quickstart.md), record results, and check that every FR and SC of spec.md is named by a test

---

## Dependencies & Execution Order

- Phase 1 blocks everything (T001 first; T002–T004 in parallel; T005 needs T001; T006 any time).
- US1 (Phase 2) next: it is the MVP and its interop fixture (Group thread post) is reused by US2–US4.
- US2 and US3 can run in parallel after US1 (US2 edits `Internal/Objects.fs`, US3 `Federation/Objects.fs`); US4 any
  time after Phase 1.
- Polish last.

## Implementation Strategy

Delegated to Sonnet sub-agents in their own worktrees based on `002-replies-threads`; each result is reviewed
(diff against this file, the contracts and the constitution), built in Debug and Release, tested, and merged before
the next group. Contract changes go to the Frunchy session before they are made.
