# Feature Specification: Replies and Threads

**Feature Branch**: `002-replies-threads`

**Created**: 2026-10-10

**Status**: Draft

**Input**: User description: "Megáfono's next feature, planned around Frunchy's per-episode comments
(`FrunchyBackend/docs/fediverse.md`): inbound replies (a Create of a Note replying to something Megáfono
published becomes `reply.received`, its Delete becomes `object.deleted`), outbound replies (publishing with
`inReplyTo`), a `replies` collection on published objects so Mastodon can load the whole thread, non-user
actors (Group, e.g. one per anime series) answered by the app's actor lookup, and content warnings kept both
ways. Following a feed is a later, separate feature."

## Overview

Feature 001 lets an app publish posts and be followed. This feature makes those posts **conversations**: people
on other servers reply to them and the app hears about it, the app's users reply too, and a remote reader opening
a post sees the whole thread.

The reference case is Frunchy. Each anime series is a Group account (`@frieren@frunchy.example`). For each episode
the series account publishes one public post whose `url` is Frunchy's episode page. Comments are replies to that
post: a Mastodon user's reply reaches Frunchy as a webhook, and a comment written on Frunchy is published as a
reply from that user's own account. Nothing in this feature is specific to Frunchy (constitution Principle I):
any app can use replies on any post it published.

Vision references: US4 (replies; likes and mentions stay later), FR-003 (Group and Service actors), FR-012
(Create and Delete of Notes), the webhook event list (`reply.received`, `object.deleted`), and the audience and
privacy rules.

## Decisions on the consumer's design (to confirm with the app side)

These answer Frunchy's questions and are the defaults this spec is written on.

- **Group actors fit, as plain accounts.** A Group posts and is followed like any account; there is no
  `Announce` (boost) of replies to the Group's followers, so it is not a Lemmy-style community (FEP-1b12) and
  Lemmy or Friendica users will not see it as one. Boosts and relays stay non-goals. A series' followers get each
  new episode thread; everyone opening a thread loads its replies through the `replies` collection.
- **The `replies` collection lists only replies Megáfono has seen**: the app's own replies and verified inbound
  replies, public and unlisted only. Megáfono cannot list replies that never reached it, and never lists
  followers-only or direct ones.
- **Edits are in.** An `Update` of a known inbound reply becomes `reply.updated`; the app's own replies are
  already editable with the existing update call. Without it, edited comments would go stale on the app.
- **Replies to replies are in.** A reply whose parent is a reply already recorded in one of the app's threads is
  treated like a direct reply, so nested comments are not lost.
- **Pasting an episode page URL works on Mastodon only.** The app's page carries
  `<link rel="alternate" type="application/activity+json" href="<thread post id>">` and the same `Link` header,
  as for profiles; Mastodon follows it to the thread post. GoToSocial does not follow alternate links, so its
  users paste the series handle or the post's own URL (research R-21).
- **Threads stay open to replies everywhere.** Posts declare that anyone may reply, so GoToSocial users can reply
  without an approval round trip; Mastodon ignores the declaration.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Replies from the fediverse reach the app (Priority: P1)

Someone on Mastodon or GoToSocial replies to a post the app published (for example an episode thread). The app
receives the reply as a webhook, with the verified author and safe content, and can show it. If the author edits
or deletes the reply, the app is told.

**Why this priority**: without it, comments from the fediverse go nowhere; it is the core of the feature.

**Independent Test**: a real Mastodon and a real GoToSocial account reply to a public post published through
Megáfono; the fake app receives `reply.received` with the author and sanitized content; an edit produces
`reply.updated`, a delete produces `object.deleted`.

**Acceptance Scenarios**:

1. **Given** a public post published through Megáfono, **When** a remote user replies to it, **Then** the app
   receives `reply.received` with the reply's id, the verified author, sanitized content, content warning,
   language, publication time, attachments as URLs, the post it replies to and the thread it belongs to.
2. **Given** a reply already received, **When** someone replies to that reply, **Then** the app receives
   `reply.received` for the new reply with its parent and the same thread.
3. **Given** a received reply, **When** its author edits it, **Then** the app receives `reply.updated` with the
   new content; **When** its author deletes it, **Then** the app receives `object.deleted` for its id.
4. **Given** a reply with a content warning ("Spoilers: S2 E5") marked sensitive, **When** it arrives, **Then**
   the webhook carries the warning text and the sensitive flag.
5. **Given** a Create of a Note that replies to something the app never published, and is not part of one of its
   threads, **When** it arrives, **Then** it is acknowledged and dropped, and the app hears nothing.
6. **Given** a reply whose signature is invalid, or whose author is not the signer, **When** it arrives,
   **Then** it is refused and the app hears nothing.
7. **Given** the same reply delivered twice, **When** it arrives again, **Then** the app receives one event.

---

### User Story 2 - The app's users reply into threads (Priority: P1)

A user of the app writes a comment on a thread. The app publishes it as a reply from that user's own account.
The thread's owner, the post being replied to, and the user's followers receive it, and it shows in the thread
on Mastodon.

**Why this priority**: comments must flow both ways for a shared discussion; it reuses publishing from 001.

**Independent Test**: publish a reply from a local user to a post published by a local Group; a Mastodon
follower of the user sees it as a reply in the thread; a reply to a remote user's reply reaches that remote user.

**Acceptance Scenarios**:

1. **Given** a thread post published by a Group, **When** the app publishes a reply from user alex with
   `inReplyTo` set to that post, **Then** alex's followers' servers receive it threaded under the post.
2. **Given** a remote user's reply in the thread, **When** alex replies to it, **Then** that remote user's server
   receives the reply, it mentions them, and it is threaded under their reply.
3. **Given** a reply with a content warning, **When** it is published, **Then** Mastodon shows the warning and
   hides the content until opened.
4. **Given** an `inReplyTo` that Megáfono does not know (not published by the app and not a recorded reply),
   **When** the app publishes, **Then** the call is refused.
5. **Given** a published reply, **When** the app edits or deletes it, **Then** the same recipients receive the
   Update or Delete (as in 001).

---

### User Story 3 - A reader sees the whole thread (Priority: P2)

Someone on Mastodon opens a thread post (or pastes the app's episode page URL into search). They see the replies
Megáfono knows about, not only the ones from people they follow.

**Why this priority**: it makes threads readable on the fediverse; the app's own page already shows everything.

**Independent Test**: with replies from two Mastodon accounts and one local user, a third Mastodon account that
follows none of them opens the thread and sees all three replies.

**Acceptance Scenarios**:

1. **Given** a post with public replies, **When** a server fetches the post, **Then** it carries a `replies`
   collection whose pages list the public and unlisted replies Megáfono has seen, the app's own first, paged.
2. **Given** a followers-only or direct reply, **When** the collection is fetched, **Then** that reply is never
   listed.
3. **Given** a deleted reply, **When** the collection is fetched, **Then** it is no longer listed.
4. **Given** an app page with the alternate link to a thread post, **When** a Mastodon user pastes the page URL
   into search, **Then** Mastodon shows the thread post (GoToSocial users paste the post's own URL instead).

---

### User Story 4 - Series and other non-user accounts (Priority: P2)

The app answers its actor lookup for accounts that are not people, such as one Group per anime series, with the
same permanent-id rule. They can be found, followed, and publish posts.

**Why this priority**: the Frunchy model depends on it, but 001 already serves Group and Service types; this story
proves it end to end and settles the follow rules for them.

**Independent Test**: GoToSocial and Mastodon find `@frieren@app` (a Group), follow it, and see its next post in
the home timeline.

**Acceptance Scenarios**:

1. **Given** the app returns `type: Group` for `frieren`, **When** Mastodon looks it up, **Then** it shows a group
   account that can be followed.
2. **Given** an unlocked Group, **When** someone follows it, **Then** the follow is accepted at once (as for any
   unlocked actor) and they receive its new posts.

### Edge Cases

- **Reply to a deleted post**: the parent answers 410; the reply is acknowledged and dropped (no event).
- **Reply arrives before its parent reply** (out-of-order delivery): if the parent is unknown, the reply is
  dropped; a later copy or a fetch of the thread does not resurrect it in this feature.
- **Reply addressed followers-only or direct to the thread owner**: delivered to the app with its audience, never
  listed in the `replies` collection.
- **Very long threads**: the collection is paged; a page holds at most 50 ids.
- **Reply edited after deletion, or deleted twice**: ignored.
- **Reply from an account later found on a forged host** (object id host differs from the signer): refetched from
  its origin, as in 001; if that fails, dropped.
- **The app is down**: events are kept and retried as in 001 (never dropped).
- **Spoilers in the parent**: a reply inherits nothing; each reply carries its own warning.

## Requirements *(mandatory)*

### Functional Requirements

**Inbound replies**

- **FR-001**: Megáfono MUST accept a `Create` of a Note or Article whose `inReplyTo` is an object Megáfono
  published, or a reply already recorded in one of its threads, and MUST emit a `reply.received` event to the
  author of the thread root, with the reply id, `inReplyTo`, thread root id, verified author, sanitized content,
  `summary`, `sensitive`, language, `published`, `url`, attachments (type, media type, URL, description) and its
  audience (public, unlisted, followers, direct).
- **FR-002**: Any other `Create` MUST be acknowledged and dropped (unchanged from 001).
- **FR-003**: Megáfono MUST record each accepted reply (id, author, parent, thread root, audience, state, times)
  but MUST NOT keep its content beyond what the event needs (the app owns content).
- **FR-004**: An `Update` of a recorded reply, signed by its author, MUST emit `reply.updated` with the new
  content fields; an `Update` of an unknown or deleted reply MUST be ignored.
- **FR-005**: A `Delete` of a recorded reply (by id or Tombstone), signed by its author, MUST mark it deleted
  and emit `object.deleted`; repeats MUST be ignored.
- **FR-006**: Inbound replies MUST pass every 001 inbox rule (signature, digest, rate limits, deduplication,
  object id on the signer's host or refetched from origin).

**Outbound replies**

- **FR-010**: The publish call MUST accept `inReplyTo` set to an object Megáfono published or a reply recorded
  in one of its threads; anything else MUST be refused with 400.
- **FR-011**: A reply MUST be addressed so the parent's author, the thread owner and the replying user's
  followers receive it, MUST mention the parent's author when that author is remote, and MUST carry the thread
  root so other servers thread it.
- **FR-012**: Replies MUST be `public` or `unlisted` in this feature; other audiences MUST be refused with 400.
- **FR-013**: `summary` and `sensitive` MUST be carried on every published object (already accepted by the
  publish call in 001) and MUST be shown as a content warning by Mastodon and GoToSocial.

**Threads**

- **FR-020**: Every published object MUST carry a `replies` collection listing, in pages of at most 50, the ids
  of the public and unlisted replies Megáfono has recorded whose parent is that object (the app's own replies
  first, then remote ones, each oldest first), excluding deleted ones. The first page is embedded and further
  pages are linked by URL, so both Mastodon and GoToSocial can walk it.
- **FR-023**: Every published object MUST declare that anyone may reply, and carry a stable thread identifier
  shared by the root and all its replies.
- **FR-021**: The collection and its pages MUST follow the object's own fetch rules (a followers-only object's
  replies are as private as the object).
- **FR-022**: Each reply Megáfono publishes or records MUST be resolvable: the app's own replies by fetching
  them, remote replies by their own server.

**Actors**

- **FR-030**: Actor lookups returning `Group` or `Service` MUST be served and followed exactly like `Person`,
  under the same permanent-id rule (constitution Principle IV).

**App contract**

- **FR-040**: The new events (`reply.received`, `reply.updated`, and `object.deleted` for replies) MUST follow
  the 001 envelope, ordering and retry rules; the contract stays at version 1 because the changes are additive.
- **FR-041**: The documentation MUST state that a reply hidden or removed by the app stays on other servers.

### Key Entities

- **Reply record**: a reply Megáfono has accepted or published: id, author (local or remote actor), parent id,
  thread root id, audience, state (live, deleted), first and last seen times.
- **Thread**: the tree of replies under a root object Megáfono published; identified by the root's id.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A reply from a real Mastodon and a real GoToSocial account to a post published through Megáfono
  reaches the app within 1 minute, with author, content and content warning, in automated tests.
- **SC-002**: A local user's reply appears threaded under the parent on Mastodon and GoToSocial, and reaches a
  remote user it replies to, in automated tests.
- **SC-003**: A Mastodon account following nobody in the thread sees every public reply Megáfono has recorded
  when it opens the thread, in an automated test.
- **SC-004**: No followers-only or direct reply is ever listed in a `replies` collection (tested).
- **SC-005**: Pasting an app page URL with the alternate link into Mastodon finds the thread post (tested; on
  GoToSocial, pasting the post's own URL finds it).
- **SC-006**: Edits and deletions of remote replies reach the app as events in automated tests on both servers.

## Assumptions

- Out of scope, as later features: likes and mentions (vision US4 rest), `Announce`/boosts and community
  semantics (FEP-1b12), receiving posts from followed accounts (the app's feed), blocks (a separate task that
  should land before public threads are announced widely), actor deletion (vision US6).
- Megáfono learns about replies only when they reach its inboxes; replies that never do (for example a reply to a
  remote reply that mentions neither the thread owner nor a local user) are not seen.
- The thread owner is the author of the root object; for Frunchy that is the series Group.
- Threads are created by the app with the existing publish call, using an idempotency key per thread.
