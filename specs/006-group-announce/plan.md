# Implementation Plan: Group actors announce the replies they receive

**Branch**: `006-group-announce` (CodeAlta worktree branch `alta/canny-mira-kkng`) | **Date**: 2026-11-04 |
**Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/006-group-announce/spec.md`

## Summary

A `Group` the app marks `announcesReplies` (R-61) sends an `Announce` of every public or unlisted reply recorded in
its threads, remote (002's reply path) or local (the app's publish), to its followers' servers and the remote author's
inbox, as a plain boost of the reply's id (R-62); an edit is followed by an `Announce` of a minimal `Update` and a
deletion by `Undo` of the announce (R-63); local replies are announced too (R-64). On the receiving side, an
`Announce` from a followed remote `Group` is handled before any fetch only when the signer is a Group with a follower
here, then the object is read from its origin and accepted only when attributed to its own host and public or
unlisted (R-65); it becomes `post.received`/`post.updated`/`post.deleted` with a new `announcedBy` field, on the same
`feed_posts` rows as 005 with a new `announced_by` column (R-66), idempotent by rows and by the Announce id, with one
`post.updated` per edit even when the author is also followed (R-67). The constitution's scope list is amended (R-69).
Interop on GoToSocial and Mastodon for the sending side (R-70); the receiving side is tested with the stub remote
playing a Group and with the sender's own builders (SC-002).

## Technical Context

**Language/Version**: F# on .NET 10 (unchanged)

**Primary Dependencies**: unchanged

**Storage**: Postgres; three columns (`local_actors.announces_replies`, `replies.announce_activity`,
`feed_posts.announced_by`) and `'Announce'` in the activities check; no new table

**Testing**: as 005: xUnit v3, real Postgres and Kestrel, the fake app and the stub remote (which learns to play a
`Group`), interop on GoToSocial (default) and Mastodon (`Suite=mastodon`)

**Target Platform**: Linux container, as before

**Project Type**: web service (federation port 8080, internal API port 8081)

**Performance Goals**: one announce activity and one delivery per follower server per reply; one signed fetch per
handled inbound `Announce` (plus the author's actor when not cached)

**Constraints**: 202 for every verified activity; no fetch before the signer is known to be a followed Group; no
event a user should not get; 002's and 005's events unchanged but for `announcedBy: null`; `dotnet build -c Release`
clean

**Scale/Scope**: as 005; `feed_posts` rows per announced post and recipient; one `activities` row per announce

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.* Passes both times, with the scope amendment.

| Gate | Answer |
| --- | --- |
| Protocol-free app | The app sets one boolean on its lookup answer and reads one field on events it already handles; it never sees an `Announce`. |
| Contract | Additive: `announcesReplies` on the lookup, `announcedBy` on the three feed events ([contracts/app-contract.md](contracts/app-contract.md)). |
| Privacy | Only public and unlisted replies are announced (never widening an audience); a follower accepts only public and unlisted announced objects; blocks of the Group and of the author, and instance blocks, filtered after the users' rows are locked; tests try a followers-only reply, a followers-only announced object, a blocked author, a blocked Group, an instance block, a deleted user, a thread's root author. No content kept. |
| Trust | The announced object is read from its origin, signed, and accepted only when its `id`, type, `attributedTo` host and audience pass; the announcer must be a `Group` as its actor document said when fetched; nothing in the `Announce` body is trusted beyond ids. Outbound announces carry ids, never copies. |
| Identity | Announce ids are new UUID activity ids; nothing reused. |
| Delivery | Announces are queued in the transaction of the reply's record or event; rows and markers make repeats no-ops; deliveries retry as every delivery. |
| Interop | GoToSocial and Mastodon: a Group's announce of a public reply is listed among the reply's boosters; the edit's announce and the retraction are accepted. The receiving side is proven against the sender's own shapes. |
| Operations | No new dependency, setting or operator step; one lookup field for the app. |
| Scope | "Boosts of remote posts" narrowed by constitution amendment 1.1.0 to users' boosts and timelines; a Group's announces of its threads' replies and a followed Group's announces are in scope (R-69). |

## Project Structure

### Documentation (this feature)

```text
specs/006-group-announce/
├── spec.md
├── plan.md              this file
├── research.md          R-61 to R-70
├── data-model.md        three columns, the activities check
├── quickstart.md        validation guide
├── contracts/
│   ├── app-contract.md  announcesReplies, announcedBy
│   └── federation.md    Announce, Announce{Update}, Undo{Announce}; inbound handling
├── checklists/requirements.md
└── tasks.md
```

### Source Code (repository root)

```text
src/Megafono/
├── Domain/ActivityStreams.fs        + announce / announceUpdate / undoAnnounce builders, announce addressing per audience
├── Domain/Events.fs                 + AnnouncedBy on postBody / postDeletedBody ("announcedBy", null when None)
├── Infrastructure/Database.fs       + the three columns, 'Announce' in the activities check (replaced once)
├── Infrastructure/AppClient.fs      + AnnouncesReplies (parse: Group only; store; LocalActor)
├── Infrastructure/Outbox.fs         + followerInboxes (moved from Internal/Objects.fs and Internal/Actors.fs)
├── Infrastructure/Threads.fs        + ReplyRow.AnnounceActivity, setAnnounce
├── Infrastructure/FeedPosts.fs      + announced_by on insertRows, announcedRows, qualifyingAnnounced (follows the group,
│                                      blocks neither, optionally not following the author), markDeletedAnnounced
├── Infrastructure/Announces.fs      NEW: groupAnnounces (FOR KEY SHARE read), queueAnnounce / queueAnnounceUpdate /
│                                      queueUndoAnnounce (activities row + deliveries to followers and the author inbox)
├── Federation/InboxReplies.fs       announce hooks in createReply / updateReply / handleDelete
├── Federation/InboxAnnounces.fs     NEW: handleAnnounce (new post / edit), handleUndo (retraction)
├── Federation/Inbox.fs              dispatch Announce; Undo{Announce} before Undo{Follow}
└── Internal/Objects.fs              announce hooks for local replies in publish / update / delete

tests/Megafono.Tests/
├── Unit/AnnounceBuildersTests.fs    NEW: the three builders and their addressing
├── Unit/EventsTests.fs              + announcedBy on the feed events, null otherwise
├── Integration/AppClientTests.fs    + announcesReplies parsing and storage
├── Integration/SchemaTests.fs       + the columns and the widened check
├── Integration/GroupAnnounceTests.fs NEW: US1 (outbound), with the stub as the followers' server and the author
├── Integration/InboundAnnouncesTests.fs NEW: US2 and US3 (inbound), the stub playing a Group, the sender's builders
└── Fixtures/{FakeApp,StubRemote,Publishing}.fs  AnnouncesReplies on FakeUser; actor type per stub user; addFollowing with a type

tests/Megafono.Interop.Tests/
├── Fixtures/FediClient.fs           + RebloggedBy (GET /api/v1/statuses/{id}/reblogged_by)
└── GroupAnnounceTests.fs            NEW: GoToSocial and Mastodon (Suite=mastodon)
```

`Megafono.fsproj` compile order: `Infrastructure/Announces.fs` after `FeedPosts.fs`; `Federation/InboxAnnounces.fs`
after `InboxPosts.fs`, before `Inbox.fs`.

**Structure Decision**: the existing single service and its two test projects; one new infrastructure module and one
new federation module, following the 005 split.

### Outbound (R-62 to R-64, R-68)

- `Announces.groupAnnounces tx groupId : Task<bool>`: `SELECT 1 FROM local_actors WHERE app_user_id = @g AND
  deleted_at IS NULL AND actor_type = 'Group' AND announces_replies FOR KEY SHARE`.
- `Announces.queueAnnounce tx time domain group replyIri audience (authorId, authorInbox option) : Task<Guid>`: insert
  `activities` (`'Announce'`, audience, object NULL, body = `ActivityStreams.announce …`), deliveries to
  `Outbox.followerInboxes tx group` plus the author inbox, returns the activity id. `queueAnnounceUpdate … updateId
  authorId replyIri`, `queueUndoAnnounce … announceId replyIri` likewise (`'Announce'`, `'Undo'`).
- `InboxReplies.createReply`: after `recordRemoteReply` returned true, when `Audience.isListable audience` and
  `groupAnnounces tx parent.RootAuthor` → queue, `Threads.setAnnounce tx replyId guid`. `updateReply`: when
  `row.AnnounceActivity` is Some → `queueAnnounceUpdate` with the inbound Update's id. `handleDelete`: when Some →
  `queueUndoAnnounce`.
- `Internal/Objects.fs` publish: after `recordLocalReply`, when `parent.RootAuthor <> actor.AppUserId` and
  `groupAnnounces tx parent.RootAuthor` → queue with the object's IRI, no author inbox; `setAnnounce`. Update and
  delete: `Threads.findReply tx objectIri` → `AnnounceActivity` → queue (the Update's id is `Ids.activityId domain
  activityGuid`; the delete embeds the announce id). The `activities.object` of these rows stays NULL (they are the
  Group's, not the reply's object): the object's delivery status endpoint lists only the author's activities.

### Inbound (R-65 to R-67)

- `Inbox.dispatch`: `Announce` → `InboxAnnounces.handleAnnounce`; `Undo` whose object is an embedded `Announce` →
  `InboxAnnounces.handleUndo`, else `InboxFollows.handleUndo` as today.
- `handleAnnounce`: preconditions (actor = signer, id on the signer's host, `signer.ActorType = "Group"`); target and
  kind from the object (IRI / object with id / `Create` → New; `Update` → Edit; else `Ignored`); target not on
  `env.Domain`; read-only: processed? → `Ignored`; `followerCandidates tx signer.Id` empty → `Ignored` (no fetch).
  Then `RemoteActors.fetchObject` → checks (id, Note/Article, `attributedTo` same host) → `getActor` author →
  audience listable. New: transaction as 005 `createPost` with the Group as the followed actor, `announced_by` set,
  the author as `remote_actor`, candidates minus R-53's root author, qualifying for the Group (mustFollow) and the
  author (blocks only), `markProcessed announceId`, rows, events with `AnnouncedBy`. Edit: `announcedRows tx target
  signer.Id` → lock → `markProcessed announceId` → users qualifying for the Group and the author and not following
  the author → `post.updated` with the re-read note (`updated` from the note or now; audience and `published` from
  the rows).
- `handleUndo`: embedded Announce with `actor` = signer and an `object` id → `announcedRows` → lock → `markDeleted`
  on rows announced by this Group → `post.deleted` to users qualifying for the Group and the author (blocks only).

## Complexity Tracking

No constitution violation beyond the scope amendment recorded in R-69 (constitution 1.1.0). No new dependency.
