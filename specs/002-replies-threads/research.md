# Research: Replies and Threads

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-10

From Mastodon `main` (4.7) and GoToSocial (0.22) sources and docs, read but not yet run against this feature.
Numbering continues 001's research (R-01 to R-15). **Unverified** items are confirmed by the interop tests.

### R-20 The `replies` collection and how threads load

- **Decision**: every published object carries `replies`: `type: Collection`, `id` =
  `{object}/replies`, `totalItems`, an embedded `first` `CollectionPage` (`partOf`, `items` as ids, at most 50)
  and `next` as a URL (`{object}/replies?page=2`). Items are the public and unlisted replies Megáfono recorded,
  **local replies first** (oldest first), then remote ones. Use `items`, never `orderedItems`, on a `Collection`.
- **Rationale**: Mastodon's per-Create path (`FetchRepliesService`) reads at most 5 ids from the first page and
  keeps only those on the parent author's host, so local replies must come first. Mastodon 4.5+
  (`FetchAllRepliesWorker`, run when a signed-in viewer opens the thread's context) crawls `replies` recursively
  with no host filter, up to 1000 replies, so remote replies we list are fetched from their origin. GoToSocial
  walks `replies` by following `next` IRIs and skips items on its own host. Mastodon ignores a collection's own
  `items` when `first` exists and requires page fetches on the same host.
- **Alternatives**: `OrderedCollection` (Mastodon reads it too but its own shape is `Collection`); listing only
  local replies (third-server readers would never see remote comments); relying on `context` (not consumed by
  Mastodon or GoToSocial for loading).

### R-21 Pasting an app page URL

- **Decision**: the app's page carries `<link rel="alternate" type="application/activity+json" href="<object id>">`
  and a matching `Link` header. Works on Mastodon; on GoToSocial users paste the object id or the handle.
- **Rationale**: Mastodon's `FetchResourceService` follows one alternate hop and resolves any Note type through
  `FetchRemoteStatusService`, checking only that the Note's `id` host matches its `attributedTo` host; `url` is not
  checked. GoToSocial requires an ActivityStreams response and does no alternate discovery. The gateway rule (no
  `Accept`-header routing) keeps the page HTML-only.

### R-22 Inbound replies: what arrives and when

- **Decision**: handle `Create{Note|Article}` whose `inReplyTo` is one of our objects or a recorded reply in one of
  our threads; `Update{Note}` and `Delete` of recorded replies, authorized against the reply's author.
- **Rationale**: Mastodon always delivers a public or unlisted reply to the replied-to account's inbox, plus
  mentioned accounts and the author's followers; its reply box prefills the replied-to author and the parent's
  mentions, so our thread owner usually stays mentioned down the chain (unverified). `Update` and `Delete` reach the
  same set as the original, so we only get edits and deletes of replies we received. Mastodon applies edits only
  with `updated`; `Delete` may carry a bare id or a Tombstone. GoToSocial puts mentions in `cc` (or `to` for
  direct) and deletes by URI with the original addressing.
- **Gotcha**: replies to a remote reply that mention neither our actors nor reach a follower are never seen; the
  viewer's own server finds them by crawling. Documented, not promised.

- **Verified in US1 (T011)**: GTS 0.22.2 and Mastodon 4.7.3 address a reply `to: [Public]`, `cc`: the author's
  followers plus mentioned actors, delivered to our shared `/inbox`. **GTS sent nothing to Megáfono when the reply
  did not mention the Group** (Mastodon untested without the mention): clients normally prefill the mention, but a
  user who deletes it is not heard. GTS sends `to` and `tag` as single values (not arrays); both send `contentMap`.
  GTS reuses one `Update` id for every edit (`…/activity#Update`), so edits are deduplicated by `id|updated`;
  Mastodon uses `…#updates/<unix>`. GTS deletes with a bare URI (with the Create's addressing), Mastodon with a
  Tombstone. Mastodon copies our `context`/`conversation` onto its replies.

### R-23 Outbound replies

- **Decision**: a reply's `to` is `[as:Public]` (public) or `[followers]` (unlisted), `cc` holds the other of the
  two plus the parent's author and every other mentioned actor; the object mirrors `to`/`cc`, carries
  `inReplyTo`, a `tag` Mention (`href`, `name: "@user@host"`) for the parent's author when remote, and
  `context` and `conversation` set to the thread id. Deliveries: the replier's followers' (shared) inboxes, plus
  the personal inbox of every remote mentioned actor.
- **Rationale**: a mentioned actor must be in `to`/`cc` to receive the post; Mastodon accepts a public status
  only if it is addressed to a local account, replies to a followed account, or a local account follows the
  author, so the Mention plus direct delivery is what guarantees the replied-to person sees it. Receiving servers
  fetch the parent if unknown (signed), which our public objects allow.

- **Verified in US2 (T014)**: our replies thread under the parent on GTS and Mastodon (`in_reply_to_id`,
  `spoiler_text`, `sensitive`), and the remote author of the parent reply gets a mention notification from the
  `Mention` tag alone (no `@handle` needed in the content). On GTS a reply in the Group's thread did not show in a
  follower's home timeline (it is in the thread's context); not tested on Mastodon.

### R-24 Thread identity and reply policy

- **Decision**: every object carries `context` and `conversation` =
  `https://{domain}/ap/contexts/{rootObjectId}` (resolvable: a `Collection` whose first page lists the root and the
  public replies' ids), and `interactionPolicy: {canReply: {automaticApproval: [as:Public]}}`.
- **Rationale**: Mastodon 4.5 emits `context`/`conversation` (FEP-7888) and creates a conversation from any URI;
  harmless elsewhere. GoToSocial's default for a post without a policy is already "anyone may reply"; stating it
  avoids depending on that default. Mastodon's `interactionPolicy` is quote-only and ignores `canReply`.
  Handling GoToSocial `ReplyRequest`/`replyAuthorization` is not needed while our posts allow everyone.

### R-25 Group actors

- **Decision**: no change to actor documents: the app's `type: Group` is served as is, follows follow the
  `locked` rule, posts are plain `Create`s from the Group. No `Announce`.
- **Rationale**: Mastodon and GoToSocial accept `Group` like `Person`; `Service` and `Application` get a bot badge,
  `Group` does not. Lemmy-style software expects a Group to `Announce` contributions (FEP-1b12); without it a
  series may look inactive there (unverified). That is acceptable: Lemmy semantics are out of scope.
- **Verified (T019)**: Mastodon 4.7.3 reports the Group with `group: true`, `bot: false`; GoToSocial 0.22.2
  shows no group flag (`group: false`) and `bot: false`. Both follow an unlocked Group at once and show its posts
  in followers' home timelines.

### R-26 Content warnings

- **Decision**: outbound `summary` (warning text) and `sensitive` exactly as the app gives them; inbound both
  passed through, `summary` sanitized to text. A spoiler from the app should set both.
- **Rationale**: Mastodon maps `summary` to the warning and `sensitive` to hidden media; GoToSocial reads
  `summary` (falling back to `name`) and `sensitive`. Either field may be missing on inbound posts.

### R-27 What Megáfono stores about replies

- **Decision**: a `replies` table with references only (ids, author, parent, root, audience, state, times); the
  content travels in the app event and is not kept after the event is delivered.
- **Rationale**: the app owns content (vision "Data ownership"); the collection only needs ids, and edits and
  deletes only need the author to authorize them.
