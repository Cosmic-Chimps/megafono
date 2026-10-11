# Quickstart: validating Replies and Threads

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

Same prerequisites and commands as [001's quickstart](../001-federation-core/quickstart.md).

1. `dotnet test --project tests/Megafono.Tests` — inbound reply records and events, nested replies, edits,
   deletes and their authorization; outbound reply validation, addressing, Mention and recipients; collection
   paging, ordering (local first) and privacy (no followers-only/direct, deleted removed, followers-only object's
   collection is 404 to the unauthorized).
2. `dotnet test --project tests/Megafono.Interop.Tests -- --filter-not-trait "Suite=mastodon"` — GoToSocial:
   a GTS user replies to a Group's thread post → `reply.received` with content and content warning; edits →
   `reply.updated`; delete → `object.deleted`; a local user's reply appears threaded for a GTS follower and reaches
   the GTS author it replies to; pasting the post's own URL finds it.
3. `dotnet test --project tests/Megafono.Interop.Tests -- --filter-trait "Suite=mastodon"` — the same on Mastodon,
   plus: a third Mastodon account that follows nobody in the thread opens the thread's context and sees every
   public reply (SC-003); pasting the app page URL (with the alternate link) finds the thread post (SC-005); the
   Group is shown as a group account and its posts reach followers' home timelines.

## Results (T022, 2026-10-10, macOS arm64, Docker Desktop 29.8)

| Step | Result |
|---|---|
| `dotnet build`, `dotnet build -c Release` | 0 warnings, 0 errors |
| 1. `Megafono.Tests` | 514 passed (one ordering test was flaky because two seeded local replies shared a timestamp; fixed in the test, 6 of 6 runs green) |
| 2–3. Interop, GoToSocial 0.22.2 + Mastodon 4.7.3 | 28 passed, 2 skipped (the deployment-shape test and the slow crawl, both explicit) |
| Slow crawl, `-- --explicit only --filter-trait "Suite=mastodon-slow"` | passed: a GTS reply appears in a non-following Mastodon reader's thread after ~5 minutes (research R-20) |

Every FR and SC of [spec.md](spec.md) is named by at least one test.
