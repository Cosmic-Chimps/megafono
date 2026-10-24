# Feature Specification: Posts from followed accounts (the app's feed)

**Feature Branch**: `005-followed-posts` (worked on the CodeAlta worktree branch `alta/silent-olive-bf5b`)

**Created**: 2026-10-18

**Status**: Implemented

**Input**: User description: "Inbound posts from followed accounts as webhooks (the app's feed), Megáfono feature 005.
Source: .alta/tasks/2026-10-10-megafono-inbound-posts-from-followed-accounts-as-webhooks-fe-m6zz.md; builds on 001
(outbound follows), 002 (reply payloads, sanitizing, content warnings, extensions), 003 (deleted actors) and 004
(blocks), all on main. When a remote actor that a local user follows (an accepted outbound follow) sends
Create{Note|Article}, the app receives post.received for that local user; Update of such a post gives post.updated and
Delete gives post.deleted. The payload reuses 002's reply.received object shape: id, url, sanitized content,
summary/sensitive, language, published, updated, audience, inReplyTo, attachment (URLs only, no media storage), and
extensions passed back only when the namespace matches. Megáfono keeps no copy of the content (as for replies); the
app stores what it shows. Privacy (Principle II): a followers-only or direct post goes only to local users who follow
its author or are addressed, never to everyone who happens to share the inbox; a public post that several local users
follow arrives once on the shared inbox. Nothing is delivered for authors or domains a user or the instance blocks
(004), for deleted local users (003), or for actors nobody follows any more; a post that is both a reply to our
object and from a followed account must not produce two conflicting events. Deduplicate by object id, and handle
GoToSocial's reused Update activity ids as 002 does. Decide in clarify: one event per following user (fits the
envelope's single `user`) or one event listing the users; whether replies by followed accounts to third parties are
included; whether Announce (boosts) is ignored in this feature. Tested end to end: a GoToSocial account and a
Mastodon account that a local user follows publish public and followers-only posts, edit and delete them; the app
receives the right events and nothing for a non-follower or a blocked author. Out of scope: fetching an account's
past posts (outbox backfill), boosts/relays/public timelines (vision non-goals), likes and mentions (separate task),
media storage."

## Overview

Since 001 a local user can follow a remote account, and once that account accepts, its server delivers the account's
posts to Megáfono. Today Megáfono drops them: only replies into the app's own threads (002) reach the app. An app that
wants a feed, "what the people I follow posted", gets nothing. Frunchy is waiting for exactly this (its
`docs/fediverse.md`, item 6 and step 3: "following remote accounts and a feed").

With this feature, when an account that local users follow publishes, edits or deletes a post, the app is told for
those users with `post.received`, `post.updated` and `post.deleted`. The post arrives verified and sanitized, shaped
like a reply in `reply.received`. Megáfono still keeps no copy of the content and offers no timeline query: the app
stores what it shows. This keeps the 001 decision on the vision's open question "Inbox query API … Revisit when replies
and mentions arrive": still events only.

Vision references: FR-012 (handle `Create` of Note and Article, `Update`, `Delete`), FR-032 (a webhook for every
inbound event), the app-extension rule ("Remote objects carrying the same namespace are passed back to the app in
webhooks under `extensions`"), the "Audience and privacy" rules and the v1 non-goals (no boosts, relays or public
timelines). Constitution Principles I (protocol-free app, additive contract), II (privacy, blocks), III (verify before
trust) and V (durable, idempotent).

## Clarifications

### Session 2026-10-18

- Q: When a post by an account that several local users follow arrives, how is the app told? → A: One event per
  following user: one `post.received` per following local user, each with its own `user` and `seq`, the same object
  in each.
- Q: When a followed account replies to a post by someone else (not one of the app's posts), is that reply passed on
  as `post.received`? → A: Yes: every Note or Article by a followed account is a feed post, with `inReplyTo` set to
  its parent; the app filters by `inReplyTo` if it wants.
- Q: When a followed account boosts (`Announce`) a post, what does the app get in this feature? → A: Nothing: the
  boost is acknowledged with 202 and dropped, as an unhandled activity type; documented as a limitation.
- Q: A user was told about a post and later unfollowed its author; the author then edits or deletes it. What is sent
  for that user? → A: Deletions yes, edits no: `post.deleted` still goes to every user who was told about the post
  (unless they block the author or were deleted); `post.updated` only to users who still follow.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - A followed account's new post reaches the app (Priority: P1)

Alex follows bob@gts.example from the app, and bob accepted. Bob writes a post. The app is told, for alex, with bob as
the verified author and the post's sanitized content, content warning, language, attachments and audience, and shows
it in alex's feed.

**Why this priority**: it is the feed itself, the one thing the reference app is waiting for.

**Independent Test**: a GoToSocial account (and a Mastodon account) that alex follows publishes a public post and a
followers-only post; the app receives `post.received` for alex for each, and nothing for a local user who does not
follow that account.

**Acceptance Scenarios**:

1. **Given** alex has an accepted follow of bob, **When** bob publishes a public, unlisted or followers-only post,
   **Then** the app receives `post.received` for alex with the post's id, url, sanitized content, content warning and
   sensitive flag, language, published time, audience, `inReplyTo` and attachment URLs.
2. **Given** alex and carol both follow bob and bob's server delivers his public post once to the shared inbox,
   **Then** the app receives two `post.received`, one for alex and one for carol, each in that user's own event
   stream, with the same post in both.
3. **Given** dave's follow of bob is pending, rejected or removed, or dave never followed bob, **When** bob posts,
   **Then** nothing is sent for dave.
4. **Given** bob posts followers-only and the post reaches a server-wide inbox that also serves erin, who does not
   follow bob, **Then** nothing about the post is sent for erin.
5. **Given** alex follows bob and bob sends a direct post addressed to alex (not to the public or to his followers),
   **Then** the app receives `post.received` for alex with audience `direct`, and nothing for carol who follows bob but
   is not addressed.
6. **Given** bob's server delivers the same post twice (the same activity again, or under another activity id, for
   example once to the shared inbox and once to a personal inbox), **Then** each following user is told about the post
   once.
7. **Given** bob's post carries data under the namespace the app uses for its own objects (another instance of the
   same app), **Then** the event passes that data back under `extensions`; data under any other namespace is not
   passed on.
8. **Given** alex follows bob and bob's post is a reply to a post of someone other than the app's users, **Then** the
   app receives `post.received` for alex with `inReplyTo` set to that post's id.

---

### User Story 2 - Edits and deletions follow the post (Priority: P1)

Bob edits the post, then deletes it. The app is told for alex with `post.updated` carrying the new content, then with
`post.deleted`, so alex's feed never shows a stale or deleted post.

**Why this priority**: a feed that keeps edited or deleted posts breaks what the author expects; deletion is the
privacy half of the feed.

**Independent Test**: the GoToSocial (and the Mastodon) account edits then deletes a post alex received; the app
receives `post.updated` then `post.deleted` for alex.

**Acceptance Scenarios**:

1. **Given** alex received bob's post and still follows bob, **When** bob edits it, **Then** the app receives
   `post.updated` for alex with the new content and the edit time; the audience stays the one the post arrived with.
2. **Given** bob edits the same post twice and his server reuses one activity id for every edit of it (GoToSocial),
   **Then** both edits are passed on; the same edit delivered twice is passed on once.
3. **Given** alex received bob's post, **When** bob deletes it (whether his server names the post or sends a tombstone
   of it), **Then** the app receives one `post.deleted` for alex with the post's id, and a later edit or a repeated
   deletion of it produces nothing.
4. **Given** alex received bob's post and then unfollowed bob, **When** bob edits it, **Then** nothing is sent for
   alex; **When** bob deletes it, **Then** the app still receives `post.deleted` for alex, so the app can remove a post
   it holds.
5. **Given** an edit or a deletion of a post the app was never told about, or sent by someone other than the post's
   author, **Then** nothing is sent.

---

### User Story 3 - Blocks, deleted users and threads are respected (Priority: P1)

The rules of 003 and 004 hold on this new path: nothing reaches the app about an author a user or the instance
blocks, or for a user the app deleted. A post that is also a reply into one of the app's own threads does not reach
one user twice in two different shapes.

**Why this priority**: 004's promise that the app hears nothing from a blocked actor, and 003's that a deleted user
hears nothing, must not leak through the feed.

**Independent Test**: on GoToSocial, alex follows an account and then blocks it; the account's next post produces no
event. Integration tests cover the instance block, the deleted user and the reply overlap.

**Acceptance Scenarios**:

1. **Given** alex and carol follow bob and alex blocks bob or bob's domain, **When** bob posts, edits or deletes,
   **Then** nothing is sent for alex, and carol is told as usual.
2. **Given** the instance blocks bob's domain or bob, **When** bob's server sends a post, **Then** it is refused as in
   004 and nothing is sent for anyone.
3. **Given** alex followed bob and the app then deleted alex, **When** bob posts, edits or deletes, **Then** nothing
   is sent for alex.
4. **Given** alex follows bob and bob replies to a post alex published through the app, **Then** alex is told once,
   with 002's `reply.received` (as the thread's author), and not also with `post.received`; carol, who follows bob but
   did not publish that thread, receives `post.received`. Edits and deletions of that reply follow the same split
   (`reply.updated`/`object.deleted` for alex, `post.updated`/`post.deleted` for carol).

---

### User Story 4 - Boosts are not passed on (Priority: P3)

Bob boosts (announces) someone else's post. The boost is acknowledged and dropped: the app receives nothing, and the
limitation is documented (boosts of remote posts are a v1 non-goal).

**Why this priority**: it bounds the feature; the vision lists boosts as a v1 non-goal.

**Independent Test**: bob boosts a post; the app receives no feed event.

**Acceptance Scenarios**:

1. **Given** alex follows bob, **When** bob boosts a post (his own or anyone's), **Then** Megáfono answers 202 and the
   app receives no event, as for any activity type it does not handle.

### Edge Cases

- A post whose content is not a Note or Article (a poll, a page, an event) is not passed on.
- A post that claims to come from bob but is embedded by another server is not trusted as is: it is read from bob's own
  server, and only a post attributed to bob counts (001's trust rule).
- A post arriving at the same moment as a follow is accepted or ended may be decided either way for that one user;
  it is never sent to a user whose follow was never accepted.
- A followed account's own deletion (its whole account) is not handled by this feature.
- A post arriving before the follow was accepted is not passed on later: there is no backfill.
- An account followed by many local users produces one event per user in one go, without delaying other inbound
  traffic noticeably.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001** When an account sends a new Note or Article post, verified as that account's own (001's trust rules),
  Megáfono MUST tell the app about it with one `post.received` per live local user who has an accepted follow of the
  author and does not block it (user or instance block), subject to FR-002 and FR-003. Each event belongs to that one
  user, like every event of the contract; `post.updated` and `post.deleted` are per user in the same way.
- **FR-002** A public, unlisted or followers-only post MUST go to every local user who follows the author (FR-001). A
  direct post MUST go only to those of them who are addressed in it. No post may be passed on for a local user only
  because the post reached an inbox that also serves that user.
- **FR-003** A post that is a reply into one of the app's own threads MUST reach the thread's author only as 002's
  reply events, never also as feed events; the author's other local followers receive the feed events. No local user
  may receive two events of different types about the same post change.
- **FR-004** Each local user MUST be told about a post at most once, recognized by the post's id whatever the activity
  id or the inbox it arrived on.
- **FR-005** An edit of a post by its author MUST produce `post.updated` for the users who were told about the post and
  still follow the author, and do not block it. Distinct edits MUST each be passed on, also when the author's server
  reuses one activity id for all edits of a post; a repeat of the same edit MUST NOT.
- **FR-006** A deletion of a post by its author MUST produce one `post.deleted` for each user who was told about the
  post, also after an unfollow, except users who block the author or were deleted. Edits and deletions after the
  deletion MUST produce nothing.
- **FR-007** The post in `post.received` and `post.updated` MUST have the fields and the sanitizing of a reply in 002's
  `reply.received`: id, url, type, sanitized HTML content, content warning as plain text, sensitive flag, language,
  published, updated, audience, `inReplyTo` (null when the post answers nothing), attachments as URLs only. It has no
  thread or parent of the app's. `post.deleted` carries the post's id.
- **FR-008** Data in a post under the namespace the app uses for its own published objects MUST be passed back under
  `extensions`, matched by the full namespace whatever prefix the post uses; data under other namespaces MUST NOT be
  passed on.
- **FR-009** Nothing MUST be sent for posts of authors that no live local user follows with an accepted follow, for
  deleted local users, or for authors and domains blocked by the user or the instance; such activities are
  acknowledged and dropped as today.
- **FR-010** Megáfono MUST NOT keep the post's content. It MAY keep, per post, which users were told about it, to route
  edits and deletions.
- **FR-011** The change to the app contract MUST be additive: new event types only; existing events, including 002's
  reply events, keep their meaning and shape.
- **FR-012** Replies by followed accounts to posts that are not the app's MUST be passed on as feed posts like any
  other post, with `inReplyTo` set to their parent. Boosts (`Announce`) MUST be acknowledged and dropped with no event
  (User Story 4), and the limitation MUST be stated in the operator and app documentation.

### Key Entities

- **Feed post**: a post by a followed account that the app was told about, per local user: the post's id, its author,
  how it was addressed when it arrived, and whether it is still live or deleted. No content.
- **The app's namespace**: the namespace(s) the app has used for data on its own published objects; a feed post's
  data under it is passed back.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001** Against a real GoToSocial server and a real Mastodon server, a public post, a followers-only post, an edit
  and a deletion by an account a local user follows each reach the app as the matching event within 30 seconds.
- **SC-002** In the same runs, a local user who does not follow the author, and a user who blocks the author, receive
  no event about any of those posts.
- **SC-003** In every tested repeat (the same delivery twice, the same post on two inboxes, the same edit twice), each
  following user receives exactly one event per post change.
- **SC-004** Every privacy rule of FR-002, FR-003 and FR-009 has a test that tries to break it (a non-follower sharing
  the inbox, a direct post to someone else, a pending follow, a blocked author, a deleted user, a reply in one of the
  app's threads), and none produces an event it should not.
- **SC-005** No existing behavior changes: replies keep their events exactly as in 002, and the existing test suites
  stay green.

## Assumptions

- The app follows remote accounts with the existing follow call of 001; this feature adds no call to the internal API.
- Remote servers deliver a followed account's posts either once to Megáfono's shared inbox or to the followers'
  personal inboxes; in both cases the post is matched against the local users who follow its author, not against the
  inbox it arrived on.
- "Actors nobody follows any more" (input) means: no new posts and no edits after the follow ended. Deletions are
  still passed on to users who were told about the post (FR-006, clarified 2026-10-18), because a deletion only
  removes content the app already holds and keeping a deleted post would break the author's expectation.
- A direct post is passed on only to addressed users who also follow the author; a direct post or a mention from an
  account the user does not follow belongs to the mentions feature (separate task).
- The audience of a post never changes after it arrives; servers refuse such edits.
- No backfill: posts published before the follow was accepted, or while Megáfono was refusing the author, are not
  fetched.
- Media is passed as URLs only; Megáfono stores no media.
