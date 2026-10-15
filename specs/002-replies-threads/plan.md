# Implementation Plan: Replies and Threads

**Branch**: `002-replies-threads` | **Date**: 2026-10-10 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/002-replies-threads/spec.md`

## Summary

Turn published posts into threads. Inbound `Create`/`Update`/`Delete` of replies to our objects (or to recorded
replies in our threads) become `reply.received`, `reply.updated` and `object.deleted` events for the thread
root's author. The publish call accepts `inReplyTo`, and Megáfono adds the addressing, Mention, `context` and reply
policy. Every object serves a paged `replies` collection (the app's own replies first) so Mastodon 4.5+ and
GoToSocial can load the whole thread. Group actors need no new code, only end-to-end tests. Design from
[research.md](research.md) R-20 to R-27; contracts in [contracts/](contracts/).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged from 001)

**Primary Dependencies**: unchanged (Oxpecker 2.1.1, Npgsql, HtmlSanitizer, System.Text.Json)

**Storage**: Postgres; new `replies` table and two `objects` columns ([data-model.md](data-model.md))

**Testing**: xUnit v3; integration with the stub remote server; interop on GoToSocial (every run) and Mastodon
(`Suite=mastodon`), including a third Mastodon account loading a thread it follows no one in

**Target Platform / Project Type**: unchanged (Linux containers; sidecar service)

**Performance Goals**: a reply reaches the app within 1 minute (SC-001); collection pages answered from one
indexed query

**Constraints**: no content stored beyond app events (R-27); public and unlisted replies only in collections

**Scale/Scope**: threads of up to thousands of replies (paged by 50); spec US1–US4

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Answer |
|---|---|
| Protocol-free app | ✅ the app sends `inReplyTo` and receives reply events; no ActivityPub in the app. Frunchy-specific choices (series Groups, episode keys) stay in the app |
| Contract | ✅ additive: new event types and an optional publish field; version stays 1 |
| Privacy | ✅ collections list public/unlisted only; collection and context endpoints follow the object's fetch rules; followers-only replies reach the app with their audience and never appear in collections |
| Trust | ✅ the 001 inbox pipeline unchanged; replies, edits and deletes authorized against the signer; content sanitized |
| Identity | ✅ reply ids are the 001 object ids; thread ids derive from the root's permanent id |
| Delivery | ✅ reply publishing reuses the 001 outbox (idempotency, durable queue) |
| Interop | ✅ every behavior tested on GoToSocial and Mastodon, including the thread-loading crawl |
| Operations | ✅ no new dependency |
| Scope | ✅ no boosts/Announce, no likes, no mentions outside threads, no feed (later features and tasks) |

## Project Structure

### Documentation (this feature)

```text
specs/002-replies-threads/
├── plan.md, research.md, data-model.md, quickstart.md
├── contracts/ (app-contract.md, internal-api.md, federation.md)
├── checklists/requirements.md
└── tasks.md   # /speckit-tasks, after the app side agrees on the contracts
```

### Source Code

```text
src/Megafono/
├── Domain/ActivityStreams.fs     # + replies collection/page, context, Mention, interactionPolicy builders
├── Domain/Audience.fs            # + audience of an inbound object from to/cc (public/unlisted/followers/direct)
├── Infrastructure/Database.fs    # + replies table, objects.in_reply_to / thread_root
├── Infrastructure/Threads.fs     # new: resolve a parent (ours or recorded), thread root, record a reply
├── Federation/InboxReplies.fs    # new: Create/Update/Delete of replies → records + events
├── Federation/Inbox.fs           # dispatch the three activity types
├── Federation/Objects.fs         # + /replies, /replies?page=N, /ap/contexts/{root}; replies on documents
├── Internal/Objects.fs           # + inReplyTo validation, reply addressing and recipients
└── Domain/Events.fs              # + reply.received, reply.updated, object.deleted payloads
tests/Megafono.Tests/Integration/ # RepliesInboundTests, RepliesOutboundTests, RepliesCollectionTests
tests/Megafono.Interop.Tests/     # RepliesTests (GTS + Mastodon variants), ThreadLoadingTests (Mastodon)
```

**Structure Decision**: one new infrastructure module (`Threads.fs`) shared by the inbox and the publish path, one
new inbox handler file, and additions to the existing object endpoints.

## Design notes

- **Thread resolution** (`Threads.resolveParent`): an `inReplyTo` that is one of our object IRIs → that object
  (root = its `thread_root` or itself); else a live row in `replies` → its root. Deleted parent → refused (outbound
  410, inbound dropped).
- **Inbound Create**: signer = `attributedTo`; parse audience from `to`/`cc` (R-22); record the reply and enqueue
  the event to the root's author in one transaction with the dedup marker (001 pattern). If the reply object is
  embedded and on the signer's host, use it; else fetch it signed (001 FR-015).
- **Outbound reply**: recipients = replier's accepted followers' (shared) inboxes ∪ personal inbox of the parent's
  remote author ∪ remote Mentions; addressing per R-23; `context` = `/ap/contexts/{root}`.
- **Collection**: one query ordered `(local first, published_at, id)`, keyset-paged by 50; `totalItems` from a
  count; `next` only when more remain.
- **Tests that matter most**: a third Mastodon account sees all replies of a thread (SC-003, crawl); a
  followers-only reply never in a collection (SC-004); an edit and a delete from both servers (SC-006); a reply to
  a remote reply mentions and reaches its author (US2-2).

## Complexity Tracking

No constitution violations.
