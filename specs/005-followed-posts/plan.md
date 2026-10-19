# Implementation Plan: Posts from followed accounts (the app's feed)

**Branch**: `005-followed-posts` (CodeAlta worktree branch `alta/silent-olive-bf5b`) | **Date**: 2026-10-18 |
**Spec**: [spec.md](spec.md) (clarified 2026-10-18)

**Input**: Feature specification from `specs/005-followed-posts/spec.md`

## Summary

`Create`, `Update` and `Delete` of a Note or Article by an account local users follow (accepted outbound follow) become
`post.received`, `post.updated` and `post.deleted`, one per user (R-51). Recipients come from follows and, for `direct`
posts, from the addressing, never from the inbox (R-52); the root author of a thread the post replies into keeps 002's
reply events only (R-53). The inbox reads the object once and runs 002's reply path and the new feed path, each in its
own transaction (R-54). A `feed_posts` row per (post, user) makes `Create` and `Delete` idempotent by object id and
routes edits (still-following users) and deletions (every recipient) (R-55, R-56); edits use a `post|`-prefixed
processed marker. Recipients' rows are locked in `app_user_id` order before follows and blocks are read (R-57).
Extensions are matched against a new `extension_namespaces` table written by publish and update and backfilled once
(R-58). `Announce` stays unhandled (R-59). Interop on GoToSocial and Mastodon (R-60).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged)

**Primary Dependencies**: unchanged (Oxpecker, raw Npgsql, System.Text.Json, HtmlSanitizer)

**Storage**: Postgres; two new tables (`feed_posts`, `extension_namespaces`), nothing existing changed

**Testing**: xUnit v3 on Microsoft.Testing.Platform; integration against real Postgres and Kestrel with the fake app
and the stub remote; replays of captured GoToSocial and Mastodon payloads; interop on GoToSocial (default suite) and
Mastodon (`Suite=mastodon`)

**Target Platform**: Linux container, as before

**Project Type**: web service (federation port 8080, internal API port 8081)

**Performance Goals**: an inbound post to N local followers is one transaction with N events (R-51); no fetch beyond
the one 002 already makes for a non-embedded object

**Constraints**: 202 for every verified activity (unchanged); no event a user should not get (FR-002, FR-003, FR-009);
002's events byte-for-byte unchanged (SC-005); `dotnet build -c Release` clean

**Scale/Scope**: a few hundred local followers per remote account at most; `feed_posts` grows by one row per post and
recipient (references only)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.* Passes both times.

| Gate | Answer |
| --- | --- |
| Protocol-free app | The app follows by handle with the existing call and receives plain JSON events; nothing app-specific. |
| Contract | Additive: three new event types ([contracts/app-contract.md](contracts/app-contract.md)); 002's events unchanged. |
| Privacy | Recipients only from accepted follows (direct: addressed followers only), never from the inbox; blocks (user and instance) and deleted users filtered after the users' rows are locked; tests try a non-follower on the same inbox, a direct post to someone else, a pending follow, a blocked author, a deleted user, a thread reply. No content kept. |
| Trust | 002's rules: activity is the signer's own, object embedded only when on the signer's host, else fetched signed from its origin, attributed to the signer; HTML sanitized. |
| Identity | No ids minted; remote ids only stored. |
| Delivery | Rows and events commit together; repeats send nothing (rows for Create/Delete, marker for Update); restart-safe like every event. |
| Interop | GoToSocial and Mastodon: public and followers-only posts, an edit, a deletion, a non-follower and a blocked author; captured payloads replayed in the integration suite. |
| Operations | No new dependency, setting or operator step. |
| Scope | Not a timeline (no query API, no content stored); boosts stay out (R-59); no backfill. |

## Project Structure

### Documentation (this feature)

```text
specs/005-followed-posts/
├── spec.md              clarified 2026-10-18
├── plan.md              this file
├── research.md          R-51 to R-60
├── data-model.md        feed_posts, extension_namespaces
├── quickstart.md        validation guide
├── contracts/
│   ├── app-contract.md  post.received / post.updated / post.deleted
│   └── federation.md    inbound Create / Update / Delete / Announce
├── checklists/requirements.md
└── tasks.md             /speckit-tasks
```

### Source Code (repository root)

```text
src/Megafono/
├── Domain/Events.fs                 + PostReceived/PostUpdated/PostDeleted ("post.*"), postBody, postDeletedBody;
│                                      the note fields shared with replyBody (reply JSON unchanged)
├── Infrastructure/Database.fs       + feed_posts, extension_namespaces (+ guarded one-time backfill)
├── Infrastructure/FeedPosts.fs      NEW: feed_posts queries (candidates, lock in order, insert … RETURNING, live rows
│                                      of a post, mark updated / deleted) and extension_namespaces (record, known)
├── Federation/InboxNotes.fs         NEW: what the reply and feed paths share, moved out of InboxReplies: note types,
│                                      object resolution (noteOf), readers (time, flag, urlOf, attachments, language,
│                                      addressing / audienceOf) and extension data of a note for a set of namespaces
├── Federation/InboxReplies.fs       the reply path taking an already resolved note; behaviour unchanged
├── Federation/InboxPosts.fs         NEW: the feed path (create, update, delete)
├── Federation/Inbox.fs              dispatch of Create / Update{Note} / Delete: preconditions, one read, both paths,
│                                      outcomes combined
└── Internal/Objects.fs              records `extensions.namespace` on publish and update

tests/Megafono.Tests/
├── Unit/EventsTests.fs              + post.* envelopes
├── Integration/SchemaTests.fs       + the two tables, backfill, idempotent bootstrap (expected table list)
├── Integration/InboundPostsTests.fs NEW: US1–US4 against the stub remote and fake app
├── Integration/CapturedPostsTests.fs NEW: captured GoToSocial/Mastodon payloads replayed as feed posts
└── Integration/InboundRepliesTests.fs … unchanged (002 regression)

tests/Megafono.Interop.Tests/
└── FeedTests.fs                     NEW: GoToSocial and Mastodon (Suite=mastodon) end to end
```

`Megafono.fsproj` compile order: `Infrastructure/FeedPosts.fs` after `Threads.fs`; `Federation/InboxNotes.fs` after
`InboxCommon.fs`; `Federation/InboxPosts.fs` after `InboxReplies.fs`, before `Inbox.fs`.

**Structure Decision**: the existing single service (`src/Megafono`) and its two test projects; one new
infrastructure module and two new federation modules, following the 002 split (`Threads.fs` / `InboxReplies.fs`).

### Dispatch (R-54)

- `Create`: activity id, actor = signer, id on the signer's host, else `Ignored`. Read the object once
  (`InboxNotes.noteOf`); none → `Ignored`. Then the reply path (only when `inReplyTo` is set) and the feed path, each
  in its own transaction. Outcome: `Handled` when either handled, else `Ignored` with both reasons.
- `Update` of a Note/Article: same preconditions; without fetching, look up the recorded reply (002) and the live
  feed rows of the object by the signer; neither → `Ignored`. Otherwise read the object once (same id required) and
  run the path(s) that apply. Any other `Update` stays `InboxActors.handleUpdate`.
- `Delete`: no fetch; the reply path and the feed path, combined.
- `Announce`: unchanged (unhandled type).

### Feed path (R-52, R-53, R-55 to R-57)

- **create**: audience via `fromAddressing`. Transaction: candidates = live local users with an `accepted` outbound
  follow of the signer; for `direct`, only those whose actor id is in the addressing; minus the root author when
  `Threads.resolveParent` gives `Resolved`. None → rollback, `Ignored`. Lock them `ORDER BY app_user_id FOR NO KEY
  UPDATE`; re-select those still qualifying and not covered by `Blocks.coveredSql`; insert rows `ON CONFLICT DO
  NOTHING RETURNING local_actor`; read the extensions once (`extension_namespaces`); queue `post.received` per returned
  user in sorted order; commit. `Handled` when a row was inserted, else `Ignored "post already passed on"`.
- **update**: users with a live row by the signer; lock sorted; marker `post|{updateId}|{updated}` (repeat →
  `Ignored`); rows' `updated_at` set; `post.updated` (stored audience and published, `updated` from the note or now) to
  users who still follow (accepted) and are not covered by a block.
- **delete**: users with a live row by the signer; lock sorted; every live row → `deleted`; `post.deleted` to users
  not covered by a block. Nothing live → `Ignored`.

## Complexity Tracking

No constitution violation. Two tables and three modules (one infrastructure, two federation), no new dependency.
