---

description: "Task list for 006-group-announce"
---

# Tasks: Group actors announce the replies they receive

**Input**: Design documents from `specs/006-group-announce/`: [plan.md](plan.md), [spec.md](spec.md),
[research.md](research.md) (R-61 to R-70), [data-model.md](data-model.md), [contracts/](contracts/),
[quickstart.md](quickstart.md)

**Tests**: REQUIRED (constitution Principle VI). Integration tests run against real Postgres and Kestrel with the fake
app (`tests/Megafono.Tests/Fixtures/FakeApp.fs`) and the stub remote server (`Fixtures/StubRemote.fs`); signed requests
come from `Fixtures/Signed.fs`, seeding from `Fixtures/Seed.fs` and `Fixtures/Publishing.fs`. Interop on GoToSocial
(default suite) and Mastodon (`[<Trait("Suite", "mastodon")>]`). Name tests after the spec's FR/SC ids, as
`InboundPostsTests.fs` does. Write each story's tests first and see them fail before its implementation task. New
test files go into `tests/Megafono.Tests/Megafono.Tests.fsproj` (or the interop `.fsproj`) in compile order.

**Verification for every task**: `dotnet build`, `dotnet build -c Release` (no warnings) and
`dotnet test --project tests/Megafono.Tests` all green; 002's and 005's tests unchanged and green (SC-004).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 to US3 of spec.md

---

## Phase 1: Setup

- [x] T001 Baseline on `alta/canny-mira-kkng` (main at `39994cf`): `dotnet build`, `dotnet build -c Release` clean and `dotnet test --project tests/Megafono.Tests` green; record the test count in this line — 721 passed, 0 failed, 0 skipped

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: schema, the lookup field, the builders, the event field and the shared helpers every story uses.

- [x] T002 Schema in `src/Megafono/Infrastructure/Database.fs` per data-model.md: `ALTER TABLE local_actors ADD COLUMN IF NOT EXISTS announces_replies boolean NOT NULL DEFAULT false`, `ALTER TABLE replies ADD COLUMN IF NOT EXISTS announce_activity uuid NULL REFERENCES activities (id)`, `ALTER TABLE feed_posts ADD COLUMN IF NOT EXISTS announced_by text NULL REFERENCES remote_actors (id)`, and `'Announce'` in the `activities` check, replaced once on databases that lack it (the `DO` block pattern 004 used for `'Block'`; generalize that block so it replaces the constraint whenever the definition lacks `'Announce'`, and still contains `'Block'`)
- [x] T003 Schema tests in `tests/Megafono.Tests/Integration/SchemaTests.fs`: the three columns exist with their defaults; `'Announce'` is accepted and the check is replaced once when the old definition (without `'Announce'`) is present (extend or copy the 004 test); `HostTests.fs`'s table count stays 14; `initializeSchema` twice changes nothing
- [x] T004 [P] `src/Megafono/Infrastructure/AppClient.fs`: `AnnouncesReplies: bool` on `AppProfile` and `LocalActor`; `parseProfile` reads `announcesReplies` with `optionalBool … false` and refuses `true` when `kind <> "Group"` with `"'announcesReplies' is only for Group actors"`; the column in `columns`, `readActor`, `bindProfile`, `updateKnown`, `insertNew` (`profileDiffers` leaves it out: it is not in the actor document, R-61). Fixture: `AnnouncesReplies: bool option` on `FakeApp.FakeUser` (sent only when Some, like `Discoverable`), `FakeApp.user` sets None
- [x] T005 [P] Tests in `tests/Megafono.Tests/Integration/AppClientTests.fs`: `parseProfile` absent/null → false, `true` with `"type":"Group"` → true, `true` with Person or Service → `Error "'announcesReplies' is only for Group actors"`, `"yes"`/`1` → the "must be true or false" error; a stored Group profile keeps `announces_replies` and a refresh that turns it off stores false; turning it on or off does not queue an actor `Update` (no `activities` row, `PUT /internal/actors/{id}` answers `changed: false`)
- [x] T006 [P] `src/Megafono/Domain/ActivityStreams.fs` (a new section "Group announces (feature 006; research R-62, R-63)"): `announceAddressing (groupFollowers: string) (author: string option) (audience: Audience.Received) : Audience.Addressing` (public → to [Public], cc [followers; author]; unlisted → to [followers], cc [Public; author]; author omitted when None); `announce domain groupUserId activityId now addressing (objectIri: string)`; `announceUpdate domain groupUserId activityId now addressing (updateId: string) (authorId: string) (objectIri: string)` (object `{id, type: "Update", actor, object}`); `undoAnnounce domain groupUserId activityId now addressing (announceId: string) (objectIri: string)` (object `{id, type: "Announce", actor: the group, object}`); plain AS `@context`, `published`, `to`/`cc` arrays, exactly the shapes of contracts/federation.md
- [x] T007 [P] Unit tests in new `tests/Megafono.Tests/Unit/AnnounceBuildersTests.fs` (add to the test `.fsproj` after `RepliesBuildersTests.fs`): each builder's fields and addressing for public and unlisted, with and without an author, the object a string for `announce` and the embedded `Update`/`Announce` for the others
- [x] T008 [P] `src/Megafono/Domain/Events.fs`: `postBody` and `postDeletedBody` take `announcedBy: ActorSummary option` and write a top-level `announcedBy` (the actor-summary shape of `actor`, or null) right after `actor`, on all three feed events; update the two callers in `src/Megafono/Federation/InboxPosts.fs` with `None`. Unit tests in `tests/Megafono.Tests/Unit/EventsTests.fs`: `announcedBy` null on the existing feed events, the Group's summary when given, `reply.received`'s exact JSON unchanged (the 005 SC-005 test still passes)
- [x] T009 `src/Megafono/Infrastructure/Outbox.fs`: `followerInboxes tx appUserId` (the distinct `COALESCE(shared_inbox, inbox)` of accepted inbound followers), replacing the private copies in `src/Megafono/Internal/Objects.fs` (`recipientInboxes`) and `src/Megafono/Internal/Actors.fs` (`followerInboxes`); behaviour unchanged (existing tests green)
- [x] T010 `src/Megafono/Infrastructure/Threads.fs`: `AnnounceActivity: Guid option` on `ReplyRow` (read by `findReply`), `setAnnounce tx replyId activityGuid`; `src/Megafono/Infrastructure/FeedPosts.fs`: `insertRows` takes `announcedBy: string option` (written to `announced_by`), `announcedRows tx postId group : Task<FeedRow list>` (live rows with `announced_by = @group`, sorted by user), `markDeletedAnnounced tx time postId group : Task<string list>` (live rows announced by the group → `deleted`), `qualifyingAnnounced tx users group groupHost author authorHost (mustFollowGroup: bool) (excludeAuthorFollowers: bool)` (live, accepted follow of the group when asked, not covered by `Blocks.coveredSql` for the group nor for the author, and no accepted outbound follow of the author when asked); the 005 callers pass `None`
- [x] T011 [P] Fixtures: `tests/Megafono.Tests/Fixtures/StubRemote.fs` lets a user be served with another actor type (`SetActorType(name, "Group")`, used by `actorDocument` and the PathStub key); `tests/Megafono.Tests/Fixtures/Publishing.fs`: `addFollowingTyped db localUser stub name status actorType` (the `remote_actors` row with that `actor_type`), `addFollowing` calling it with `"Person"`; `addFollower` likewise gets a typed variant if a test needs a Group follower (not expected)

**Checkpoint**: schema, lookup field, builders, event field and helpers exist; every existing test green.

---

## Phase 3: User Story 1 - A Group boosts the replies in its threads (Priority: P1) 🎯 MVP

**Goal**: an announcing Group queues `Announce` / `Announce{Update}` / `Undo{Announce}` for the replies in its threads,
remote and local, delivered to its followers' servers and the remote author's inbox.

**Independent Test**: with the stub as carl's (follower) and bob's (author) server: bob's public reply to the Group's
post produces `reply.received` and an `Announce` at carl's inbox and bob's inbox (quickstart §1).

### Tests for User Story 1 (write first, see them fail)

- [x] T012 [US1] New `tests/Megafono.Tests/Integration/GroupAnnounceTests.fs` (after `InboundPostsTests.fs` in the `.fsproj`): a fake user `g-1` `series` with `Type = "Group"` and `AnnouncesReplies = Some true`, looked up once so its row exists; carl follows it (`addFollower … "accepted"` with a shared inbox for carl's stub server, or none); the Group publishes a root; bob's public reply (`Signed.note` to the root) → `reply.received` for `g-1`, and within 15 s one POST at carl's inbox and one at bob's inbox whose body is an `Announce` with `actor` the Group's id, `object` the reply's id (a string), `to` `[Public]`, `cc` `[groupFollowers; bob]`, an `activities` row `'Announce'` with `local_actor = 'g-1'`, `object` NULL, audience `public`, and `replies.announce_activity` set (FR-002); an unlisted reply → `to` `[followers]`, `cc` `[Public; bob]`; a followers-only reply and a direct reply → no `Announce` row (FR-002); the Group's own reply through the app (`POST /internal/actors/g-1/objects` with `inReplyTo` its root) → none (FR-002); a reply to a Group without the flag, and to alex (Person) → none (FR-001); bob edits the announced reply (`updateNote`) → `reply.updated` and an `Announce` whose object is `{id = the Update's id; type = "Update"; actor = bob; object = reply id}` at both inboxes; bob deletes it → `object.deleted` and an `Undo` whose object is the original `Announce` (`id`, `type`, `actor`, `object`) at both (FR-003); a reply recorded before the flag (seed the row with `announces_replies = false`, then set true through the fake app and `PUT /internal/actors/g-1`) edited → no announce (FR-003); alex (a local user) publishes a reply into the Group's thread through the app → one `Announce` at carl's inbox only (no author inbox), with the object's IRI; alex's `PUT` → `Announce{Update}` with our Update activity id; alex's `DELETE` → `Undo{Announce}` (FR-002, FR-003, R-64); a follower server the Group's user blocks by domain (`POST /internal/actors/g-1/blocks` with `{"domain": …}` of a second stub) gets no announce delivery (FR-012)

### Implementation for User Story 1

- [x] T013 [US1] New `src/Megafono/Infrastructure/Announces.fs` (compile after `FeedPosts.fs`): `groupAnnounces tx groupId : Task<bool>` (`SELECT 1 FROM local_actors WHERE app_user_id = @g AND deleted_at IS NULL AND actor_type = 'Group' AND announces_replies FOR KEY SHARE`, R-64); `queueAnnounce tx time domain group replyIri (audience: Audience.Received) (author: (string * string option)) : Task<Guid>` (author id and its inbox when remote): the `activities` row (`'Announce'`, `Audience.receivedName audience`, object NULL, body from `ActivityStreams.announce` with `announceAddressing (Ids.actorFollowers domain group) (Some authorId) audience`) and `Outbox.enqueueDeliveries` to `Outbox.followerInboxes tx group @ Option.toList authorInbox`; `queueAnnounceUpdate … updateId authorId authorInbox replyIri announceGuid`; `queueUndoAnnounce … announceGuid authorId authorInbox replyIri` (`'Undo'`). A private helper shared by the three for the insert and the deliveries
- [x] T014 [US1] Hooks in `src/Megafono/Federation/InboxReplies.fs`: `createReply`: after `recordRemoteReply` returned true and before the commit, when `Audience.isListable audience` and `Announces.groupAnnounces tx parent.RootAuthor` → `queueAnnounce` with `(signer.Id, Some signer.Inbox)`, `Threads.setAnnounce`; `updateReply`: when `row.AnnounceActivity` is Some → `queueAnnounceUpdate` with the inbound `updateId`; `handleDelete`: when Some → `queueUndoAnnounce`. Hooks in `src/Megafono/Internal/Objects.fs`: publish, after `recordLocalReply`, when `p.RootAuthor <> actor.AppUserId` and `groupAnnounces tx p.RootAuthor` → `queueAnnounce` with `(Ids.actorId domain actor.AppUserId, None)` and `setAnnounce`; update and delete: `Threads.findReply tx (Ids.objectId domain objectGuid)` → `AnnounceActivity` Some → `queueAnnounceUpdate` (the Update's id `Ids.activityId domain activityGuid`) / `queueUndoAnnounce`, in the same transaction, before the commit. The reply's audience for a local reply is `stored.Audience` turned into `Audience.Received`

**Checkpoint**: US1 works alone: T012 green; 002 and 005 tests green.

---

## Phase 4: User Story 2 - A followed Group's announces reach the app (Priority: P1)

**Goal**: `Announce` / `Announce{Update}` / `Undo{Announce}` from a followed remote Group become `post.received` /
`post.updated` / `post.deleted` with `announcedBy`, the object read from its origin.

**Independent Test**: the stub plays a Group (`SetActorType("series", "Group")`) that alex follows
(`addFollowingTyped … "Group"`); it announces bob's note served by the stub (quickstart §1).

### Tests for User Story 2 (write first)

- [x] T015 [P] [US2] New `tests/Megafono.Tests/Integration/InboundAnnouncesTests.fs` (after `GroupAnnounceTests.fs`): the announce bodies are built with the sender's own builders (`ActivityStreams.announce` etc., `actor` the stub Group's id, ids on the stub's host) so SC-002 holds. alex (u-1) and bea (u-2) follow the stub Group `series` (accepted); the Group announces bob's public note (`feedNote` served with `ServeNote`, `attributedTo` bob on the stub) → `post.received` for alex and bea with `actor` bob, `announcedBy` the Group (id, handle, name), every field of 005's object, `extensions` when the note declares a namespace alex published with (FR-004, FR-009); `feed_posts` rows with `announced_by` = the Group and `remote_actor` = bob; the note was fetched once, signed, and bob's actor once; the same Announce again, and another Announce id of the same note → one event per user and no second fetch for the repeated id (FR-006, SC-005); the Group announces an `Update` of the note (the stub now serves the note with new content and `updated`) → `post.updated` for alex and bea with the new content, `updated` set, the stored audience and `published`; bea also has an accepted follow of bob → the next `Announce{Update}` gives `post.updated` to alex only (FR-007, R-67); the same `Announce{Update}` twice → once; the Group sends `Undo{Announce}` → `post.deleted` for alex (and bea) with `announcedBy`, nothing fetched, rows `deleted`; a later `Announce{Update}` or repeated `Undo` → nothing (FR-007); the announced note replies into alex's published thread → alex, the root author of that thread, gets nothing from the Announce (he gets 002's `reply.*` events only when the note reaches him as a Create) and bea gets `post.received` (FR-005, R-53)
- [x] T016 [P] [US3] In the same file, the privacy tests of SC-003 (write first): an `Announce` from bob (a Person alex follows) → 202, no event, nothing fetched (the existing `InboundPostsTests` test stays); from a Group nobody follows (accepted) → nothing fetched; of an object on `megafono.test` (one of alex's own objects) → nothing, not fetched; of a note whose `attributedTo` is on another host than its id (a second stub serves the note attributed to the first stub's bob) → fetched, dropped; of a `Question` → dropped; of a followers-only note (`to` bob's followers) and of a direct one → dropped; of a `Delete` activity → dropped, nothing fetched; alex blocks bob (seeded block next to the follow of the Group) → nothing for alex, bea as usual, on announce, edit and undo; alex blocks the Group through `POST /internal/actors/u-1/blocks` → follow ended, nothing for alex; the instance blocks bob's host... (bob and the Group share the stub host: use a second stub for bob, and block its domain) → nothing fetched from it, nothing for anyone; the instance blocks the Group's domain → 403; alex deleted → nothing for alex, bea as usual

### Implementation for User Story 2

- [x] T017 [US2] New `src/Megafono/Federation/InboxAnnounces.fs` (after `InboxPosts.fs`, before `Inbox.fs`) per plan.md "Inbound" and R-65 to R-67: `handleAnnounce env signer activity` and `handleUndo env signer activity`; the target and kind from the object; the read-only pre-checks (signer type Group, processed id, follower candidates) before `RemoteActors.fetchObject`; the trust checks on the fetched note (`id`, Note/Article via the readers of `InboxNotes`, `attributedTo` host = target host, `RemoteActors.getActor`, listable audience from `Audience.fromAddressing author.FollowersUrl`); the transaction as 005's `createPost` with `FeedPosts.lockUsers`, `qualifyingAnnounced`, `markProcessed announceId`, `insertRows … (Some signer.Id)`, `Events.postBody … (Some (actorSummary signer))`; the edit path with `announcedRows`, `markProcessed`, `qualifyingAnnounced … excludeAuthorFollowers=true`; the undo path with `announcedRows`, `markDeletedAnnounced`, `postDeletedBody … (Some group)`. Extensions via `FeedPosts.knownNamespaces` on the activity's and the note's `@context`, as `InboxPosts.extensionsOf`
- [x] T018 [US2] `src/Megafono/Federation/Inbox.fs`: dispatch `Announce` → `InboxAnnounces.handleAnnounce`; `Undo` whose `object` is a `JsonObject` with `type = "Announce"` → `InboxAnnounces.handleUndo`, every other `Undo` → `InboxFollows.handleUndo` as before; update the pipeline comment (step 10). `InboundPostsTests`'s existing "Announce from bob is dropped" test must still pass (bob is a Person)

**Checkpoint**: US1 and US2 together: a Megáfono-to-Megáfono conversation round-trips through the test doubles.

---

## Phase 5: User Story 3 - Trust, privacy and blocks (Priority: P1)

- [x] T019 [US3] Make T016 pass: confirm the pre-fetch checks, the block filters for the Group and the author, the instance-block refusal of the fetch (`RemoteActors.fetchObject` → `Blocked`), the deleted-user path and R-53's exclusion; fix what T016 shows

**Checkpoint**: all P1 stories pass; the privacy tests of SC-003 green.

---

## Phase 6: Polish & cross-cutting

- [x] T020 Constitution amendment 1.1.0 in `.specify/memory/constitution.md` (R-69): the scope line narrowed, the Sync Impact Report, version and Last Amended updated (done by the planning session)
- [x] T021 [P] Interop: `tests/Megafono.Interop.Tests/Fixtures/FediClient.fs` gains `RebloggedBy(statusId) : Task<Account list>` (`GET /api/v1/statuses/{id}/reblogged_by`); new `tests/Megafono.Interop.Tests/GroupAnnounceTests.fs` (after `FeedTests.fs` in the interop `.fsproj`), on GoToSocial and under `Suite=mastodon` on Mastodon, modelled on `GroupActorTests.fs`: a Group `announcesReplies` fake user; carol follows it; the Group publishes a public post; carol replies publicly (`PostReply`) → within 30 s `RebloggedBy` of carol's reply lists the Group (SC-001; also check the `deliveries` row of the `Announce` is `delivered`); carol's followers-only reply (`private`) → no `Announce` activity row; carol edits her public reply → an `Announce` of the `Update` queued and `delivered`; carol deletes it → an `Undo` queued and `delivered`; print what each server showed (`observe` as in `FeedTests.fs`) for R-70. Run with `MEGAFONO_CAPTURE_DIR` as the existing interop tests do and keep a captured `Announce`-related inbound request if the servers send one (an author's `Update` to the booster)
- [x] T022 Record what GoToSocial 0.22.2 and Mastodon 4.7.3 did (`reblogged_by`, the answers to `Announce{Update}` and `Undo{Announce}`, whether the author's `Update`/`Delete` reached Megáfono as the booster) in `specs/006-group-announce/research.md` R-70 "Observed"
- [x] T023 [P] Docs: `docs/operating.md` (replace the "A `Group` actor … is not a Lemmy-style community" bullet; a new section "Group actors that announce replies" after "Replies and threads": the lookup flag, what is announced and to whom, edits and deletions, what other servers show, limits (announces are not retracted on a later block; turning it off keeps earlier ones); in "Posts from followed accounts": announced posts, `announcedBy`, who gets what, the author's edits vs the Group's, and the "What is not passed on" bullet rewritten: boosts by people are still dropped, a followed Group's announces are passed on), `docs/vision.md` (the non-goals line per R-69; FR-012: `Announce` from a followed Group built in specs/006-group-announce; FR-003 mentions announcing Groups), `README.md` line 34–36 (feed sentence mentions announced posts), `CLAUDE.md` (latest feature 006 and its modules, 005 demoted to "Before it"), and one `changelog.d/006-group-announce.md` (`### Added` for the two sides and the lookup field; `### Changed` for `announcedBy` on the feed events)
- [x] T024 Full verification per `specs/006-group-announce/quickstart.md`: `dotnet build`, `dotnet build -c Release` (0 warnings), `dotnet test --project tests/Megafono.Tests`, the interop suite with `--filter-not-trait "Suite=mastodon"` and `--filter-trait "Suite=mastodon"`, `scripts/count-integration-lines.sh`, `.github/scripts/changelog.sh check`; set the spec's status to Implemented; record the counts in this line. Done 2026-11-04: both builds 0 warnings, `Megafono.Tests` 766/766, interop GoToSocial 24 (21 passed, 2 explicit-only skipped, one flake green on re-run), Mastodon 22/22 after the test waits a second between two edits (R-70), integration lines 70, changelog check in order

---

## Dependencies & execution order

- **Setup (T001)** → **Foundational (T002–T011)**: T002 → T003; T004 → T005; T006 → T007; T008 any time; T009, T010,
  T011 any time after T002.
- **US1** (T012 → T013 → T014) needs T002, T004, T006, T009, T010, T011. **US2** (T015, T016 → T017 → T018) needs T008,
  T010, T011 and, for SC-002, T006. **US3** (T019) needs T017/T018.
- **Polish**: T020 any time (done first by the planning session); T021 needs US1; T022 follows T021; T023 once the
  stories are settled; T024 last.

```text
T001 → T002 → T003  ┐
       T004 → T005  ├→ US1 (T012 → T013 → T014) ──┐
       T006 → T007  │                              ├→ T021 → T022 → T024
       T008         ├→ US2 (T015, T016 → T017 → T018) → US3 (T019) ┘   T023 ┘
       T009, T010, T011 ┘
```

## Implementation strategy

1. Foundational, then US1 (the Group boosts): verify T012 and the 002/005 suites.
2. US2 and US3 (the follower passes announces on, with every privacy rule): all P1.
3. Interop on GoToSocial, then Mastodon; R-70; docs; full verification.
4. Commit per completed phase, in the repo's style (`feat(006): …`, `test(006): …`, `docs(006): …`), ticking the tasks
   here, with the changelog note in the first commit an operator or app would notice.
