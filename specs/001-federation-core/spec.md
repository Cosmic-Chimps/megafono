# Feature Specification: Federation Core

**Feature Branch**: `001-federation-core`

**Created**: 2026-10-10

**Status**: Draft

**Input**: User description: "Federation core: discoverable actors, approved follows and followers-only publishing (first Megáfono feature; source: docs/vision.md US1, US2, US3, plus the parts of US5 needed to run it)." The full description, with its in-scope and out-of-scope lists, is the `/speckit-specify` prompt of 2026-10-10.

## Overview

An app that contains no ActivityPub code becomes a fediverse server by running Megáfono beside it.
Its users can be found and followed from Mastodon and GoToSocial, the app approves or rejects
follow requests, its users can follow remote people, and the app publishes a post to a user's
followers with one call. Followers-only posts stay private when someone tries to fetch them.

Three kinds of people use this feature:

- **App developers** integrate their app: they answer actor lookups, receive events and call
  Megáfono's internal API.
- **App users** are the people who get a fediverse identity, such as `@alex@app.example.com`.
- **Self-hosters** deploy the app, Megáfono and a gateway on their own server.

Remote fediverse users on Mastodon or GoToSocial are the other side of every interaction.

Vision references: US1, US2, US3, US5 (subset); FR-001 to FR-005, FR-010 to FR-014, FR-020 to
FR-024, FR-030 to FR-033, FR-040, FR-041; SC-001, SC-002 (partial), SC-003, SC-004, SC-005.

## Clarifications

### Session 2026-10-10

- Q: Actor URL shape: content negotiation on the profile URL, or a separate path keyed by the
  immutable id? → A: A separate Megáfono path, `https://{domain}/ap/actors/{immutableId}`, whose
  `url` points to the app's human profile; the gateway routes only `/ap/*` (no `Accept`-header
  routing).
- Q: What happens to app events the app never acknowledges? → A: They are kept and retried until
  the app acknowledges them, with the interval capped at 6 hours; app events are never dropped.
- Q: Should inbound traffic be rate limited? → A: Yes: a configurable per-source-host limit on
  inbox requests and remote key fetches (default 600 a minute per host), answered with 429 and
  `Retry-After` when exceeded.
- Q: Are app events delivered in order? → A: No ordering guarantee; each event carries a per-user
  sequence number and the relationship's current status at send time, so the app can ignore stale
  events.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Be found from any fediverse server (Priority: P1)

An app user exists in the app. A Mastodon user searches for their handle and sees their profile,
with the name, summary and avatar the app provides. When the app changes the profile, followers
see the change. People who do not exist in the app cannot be found. A person opening the user's
profile link in a browser sees the app's own page.

**Why this priority**: nothing else works until remote servers can find and verify an actor.
It is the smallest slice that proves the sidecar model.

**Independent Test**: with only discovery built, search for `@alex@app.example.com` from a real
Mastodon and a real GoToSocial server and check that both show the profile the app returned.

**Acceptance Scenarios**:

1. **Given** user alex exists in the app, **When** a Mastodon user searches
   `@alex@app.example.com`, **Then** Mastodon shows the profile with the app's display name,
   summary and avatar.
2. **Given** bob does not exist in the app, **When** anyone resolves `@bob@app.example.com`,
   **Then** the lookup answers "not found".
3. **Given** alex exists, **When** a browser opens alex's human profile URL, **Then** it gets the
   app's HTML page, never machine-readable actor data.
4. **Given** the app marks alex as a group or a service, **When** a remote server fetches alex,
   **Then** alex is presented with that actor type instead of a person.
5. **Given** remote users follow alex, **When** the app reports that alex's profile changed,
   **Then** each follower's server receives the update and shows the new name.
6. **Given** any server, **When** it asks what software the domain runs, **Then** it receives
   the app's configured software name and version.

---

### User Story 2 - Approve who follows me (Priority: P1)

A remote user follows an app user. If the app user's account is locked, the follow waits until
the app approves or rejects it; otherwise it is accepted at once. Either way the app is told.
When the remote user unfollows, the app is told too.

**Why this priority**: following is the base of every audience, and locked accounts are the
first half of "friends only".

**Independent Test**: from a real Mastodon account, follow a locked test user, approve it through
the internal API, and check that Mastodon shows the follow as accepted; repeat with reject and
with an unlocked user.

**Acceptance Scenarios**:

1. **Given** alex is locked, **When** a remote user sends a follow, **Then** the app receives a
   `follow.requested` event and the follow stays pending.
2. **Given** a pending follow, **When** the app approves it, **Then** the remote server is told
   the follow is accepted and the follower appears in alex's followers.
3. **Given** a pending follow, **When** the app rejects it, **Then** the remote server is told
   it is rejected and the follower never appears.
4. **Given** alex is not locked, **When** a remote user follows, **Then** the follow is accepted
   at once and the app receives `follow.accepted`.
5. **Given** a remote user follows alex, **When** they unfollow, **Then** they leave alex's
   followers and the app receives `follow.removed`.
6. **Given** a follow request with a missing or invalid signature, **When** it reaches the
   inbox, **Then** it is refused and the app never hears of it.

---

### User Story 3 - Publish to my followers, privately when I choose (Priority: P1)

The app publishes a post for an app user with one call and picks its audience: public, unlisted
or followers only. Every follower's server receives it within a minute. If a server is down,
delivery keeps trying. A followers-only post cannot be fetched by anyone who is not an approved
follower. The app can later edit or delete the post.

**Why this priority**: this is what the app gets out of federating. Followers-only delivery that
holds under fetch is the core privacy promise (constitution Principle II).

**Independent Test**: with a real Mastodon follower and a real non-follower, publish a
followers-only post; the follower sees it with the followers-only marker, and fetching the post
URL unsigned or as the non-follower answers "not found".

**Acceptance Scenarios**:

1. **Given** alex has followers on several servers, **When** the app publishes a post with
   audience followers, **Then** every follower's server receives it within 1 minute, with one
   delivery per server, not one per follower.
2. **Given** a followers-only post, **When** an unsigned request or a signed request from a
   non-follower fetches its URL, **Then** the answer is "not found", never "forbidden".
3. **Given** a followers-only post, **When** an approved follower's server fetches it with a
   valid signature, **Then** it receives the post.
4. **Given** a public or unlisted post, **When** anyone fetches its URL, **Then** they receive
   it, and Mastodon shows it as public or unlisted respectively.
5. **Given** the app's publish call times out and the app retries it with the same idempotency
   key, **When** Megáfono processes both calls, **Then** exactly one post exists and each server
   receives it once.
6. **Given** a follower's server is down, **When** delivery fails, **Then** it is retried with
   growing delays for up to 48 hours, then dropped, and the app receives `delivery.failed`.
7. **Given** a published post, **When** the app edits it, **Then** followers' servers receive the
   update; **When** the app deletes it, **Then** they receive the deletion and its URL answers
   "gone".
8. **Given** a post with app extension data, **When** it is delivered to Mastodon, **Then**
   Mastodon shows the post normally and ignores the extension.
9. **Given** a published post, **When** the app asks for its delivery status, **Then** it sees,
   per destination server, whether delivery is pending, delivered or failed.

---

### User Story 4 - Follow people on other servers (Priority: P2)

An app user wants to follow a friend who is on Mastodon. The app's "add friend" screen resolves
the friend's handle to a name and avatar, then asks Megáfono to follow them. When the friend
follows back, both sides show as mutual.

**Why this priority**: it makes follows two-way and gives the app the mutual flag it needs for a
friends list. Publishing works without it, so it comes after the P1 stories.

**Independent Test**: resolve a real Mastodon handle, follow it, accept the follow on the
Mastodon side, follow back from Mastodon, and check that both lists mark the relationship as
mutual.

**Acceptance Scenarios**:

1. **Given** a handle such as `@bob@mastodon.social`, **When** the app resolves it, **Then** it
   receives bob's name, avatar and actor id, or "not found" if bob does not exist.
2. **Given** alex does not follow bob, **When** the app asks Megáfono to follow bob by handle or
   URL, **Then** bob's server receives the follow and alex's following list shows it as pending.
3. **Given** a pending outgoing follow, **When** bob's server accepts or rejects it, **Then** the
   app receives `follow.accepted` or `follow.rejected` and the status updates.
4. **Given** alex follows bob and bob follows alex, both accepted, **When** the app lists alex's
   followers or following, **Then** bob is marked as mutual.
5. **Given** alex follows bob, **When** the app asks to unfollow, **Then** bob's server is told
   and bob leaves alex's following list.

---

### User Story 5 - Run it and integrate it in minutes (Priority: P2)

A self-hoster with a domain and TLS runs the example deployment and has a working federated test
user. A developer reads the sample app and sees that the whole integration is short.

**Why this priority**: the product is only useful if people can run it. Delivering it before the
P1 stories are proven would be premature, so it comes second.

**Independent Test**: on a fresh host with a domain, start the example deployment and time how
long it takes until a Mastodon account follows the sample app's test user.

**Acceptance Scenarios**:

1. **Given** a domain with TLS, **When** the self-hoster runs the example deployment, **Then** a
   Mastodon account can follow the test user within 10 minutes of starting.
2. **Given** the sample app in the repository, **When** a developer counts its integration code,
   **Then** it is under 100 lines and contains no fediverse protocol code.
3. **Given** the example gateway configuration, **When** someone sends a request to the internal
   API port through the public address, **Then** it is not reachable.

### Edge Cases

- **App is down**: remote follows and other inbound activities are still verified and stored, and
  the app's events are kept and retried, at least every 6 hours, until it answers, however long
  that takes. Actor lookups fall back to the last known profile
  for up to 24 hours (vision edge case).
- **Remote key rotated**: a signature fails against the cached key; the remote actor's key is
  fetched once more and the request is accepted if it now verifies, refused otherwise.
- **Same activity sent twice**: an inbound activity whose id was already processed in the last
  7 days is acknowledged and ignored.
- **Activity forwarded by a third server**: an object whose id is on a different host than the
  signer is not trusted as sent; it is fetched again from its origin.
- **Unknown activity types**: acknowledged and dropped, never an error.
- **Oversized body**: requests over 1 MB are refused before they are read in full.
- **Flood from one host**: past the per-host limit, requests get 429 with `Retry-After` and cause
  no key fetch; well-behaved servers back off and retry.
- **Clock skew**: requests dated more than 12 hours from now, in either direction, are refused.
- **Remote handle points to a private network address**: the fetch is refused, so the internal
  network cannot be probed through the "add friend" screen.
- **Follower removed after a followers-only post**: they can no longer fetch it; posts already
  delivered stay on their server (a protocol limit stated in the docs, Principle II).
- **Server unreachable for 7 days**: it is skipped for new deliveries until it answers again.
- **Restart mid-delivery**: no queued delivery or event is lost; a clean restart repeats nothing,
  and a hard crash repeats at most the in-flight deliveries, with the same activity id (FR-050).
- **Follow request for a user the app has since removed**: the lookup answers "not found" and the
  follow is refused; actor deletion itself is a later feature.
- **Duplicate follow from the same remote actor**: kept as one follow relationship.

## Requirements *(mandatory)*

### Functional Requirements

**Identity and discovery**

- **FR-001**: Megáfono MUST resolve handles of the form `acct:{user}@{domain}` by asking the app's
  actor lookup, and MUST answer "not found" when the app says the user does not exist. Positive
  answers MAY be cached for up to 5 minutes. (vision FR-001)
- **FR-002**: Megáfono MUST publish NodeInfo 2.1 with the app's configured software name and
  version. (vision FR-002)
- **FR-003**: Megáfono MUST serve an actor document for each app user, of type Person unless the
  app says Service or Group, with inbox, outbox, followers, following, shared inbox, public key,
  display name, summary, avatar, the human profile URL and whether follows need approval.
  (vision FR-003)
- **FR-004**: Every actor id MUST be permanent and MUST be built from an identifier that the app
  guarantees is immutable and never reassigned, never from a username alone. An actor id MUST
  never be issued to a second user. (constitution Principle IV)
- **FR-005**: The actor id MUST be `https://{domain}/ap/actors/{immutableId}`, and the actor's
  `url` MUST be the app's human profile URL. Handle resolution MUST point to that actor id. The
  human profile URL stays entirely the app's, so a browser opening it always receives the app's
  HTML, and the gateway MUST NOT need to route by the `Accept` header. For lookups by pasted
  profile link, the app MAY add to its profile page a link of type
  `application/activity+json` pointing to the actor id; the sample app MUST show it. (vision open
  question, resolved)
- **FR-006**: Megáfono MUST create one key pair per actor on first lookup, keep it for the life of
  the actor, store the private key encrypted, and never expose it in any response, event or log.
  (vision FR-005)
- **FR-007**: When the app reports a profile change, Megáfono MUST send an update to every
  follower's server. (vision PUT actor)

**Inbound**

- **FR-010**: Every request to an inbox MUST be read as raw bytes, refused above 1 MB, and have its
  body digest checked before the content is parsed. (vision FR-014)
- **FR-011**: Every request to an inbox MUST carry a valid signature covering the request target,
  host, date (within ±12 hours) and digest, using the `rsa-sha256` or `hs2019` algorithms of the
  draft-cavage scheme. Otherwise it MUST be refused as unauthorized and MUST NOT reach the app.
  (vision FR-010)
- **FR-012**: Megáfono MUST fetch and cache remote actors and their keys, and on a verification
  failure MUST refetch the key once before refusing. (vision FR-011)
- **FR-013**: Megáfono MUST handle Follow, Undo of a Follow, Accept and Reject of its own follows,
  and Update of a remote actor. Any other activity type MUST be acknowledged and dropped. (vision
  FR-012, subset; Create, Like, Delete and Block handling come with later features)
- **FR-014**: Megáfono MUST ignore an inbound activity whose id it already processed in the last
  7 days. (vision FR-013)
- **FR-015**: Megáfono MUST trust an object only when its id is on the same host as the signing
  key; otherwise it MUST fetch the object from its origin. (vision edge case)
- **FR-016**: Inbox requests and remote key fetches MUST be rate limited in two layers, each
  configurable and 600 a minute by default: per client address before any key fetch or signature
  work, and per verified source host after verification. The host named in an unverified signature
  MUST NOT be the only key, because a sender can claim any host. Requests over a limit MUST be
  answered with 429 and a `Retry-After` header. (constitution Principle III; research R-12)

**Outbound**

- **FR-020**: Every outbound request, including fetches of remote actors and objects, MUST be
  signed. (vision FR-022)
- **FR-021**: Outbound fetches MUST refuse private, loopback and link-local addresses, and MUST
  have a timeout and a size limit. (constitution Principle III)
- **FR-022**: Megáfono MUST have one instance actor, of type Application, used to sign outbound
  fetches not made on behalf of a user (handle resolution, key refetch). It never accepts follows
  and never authorizes a fetch of a followers-only object. (research R-02)

**Follows**

- **FR-030**: For a locked actor, a remote follow MUST stay pending and produce a
  `follow.requested` event until the app approves (an Accept is sent and the follower added) or
  rejects (a Reject is sent). (vision US2)
- **FR-031**: For an unlocked actor, a remote follow MUST be accepted at once and produce a
  `follow.accepted` event.
- **FR-032**: An Undo of a follow MUST remove the follower and produce a `follow.removed` event.
- **FR-033**: The app MUST be able to resolve a remote handle or URL to the remote actor's id,
  name and avatar. (vision remote-actors lookup)
- **FR-034**: The app MUST be able to make a user follow or unfollow a remote actor by handle or
  URL, and MUST receive `follow.accepted` or `follow.rejected` when the remote side answers.
- **FR-035**: The app MUST be able to list a user's followers and following, each with its status
  (pending, accepted) and a mutual flag that is true only when both follows are accepted.

**Publishing**

- **FR-040**: The app MUST be able to publish a Note or Article for a user, with content, a link,
  an optional reply-to reference, attachments given as URLs, optional app extension data under a
  namespace, and one audience: public, unlisted or followers. (vision FR-020)
- **FR-041**: Megáfono MUST assign the object and its activity permanent ids under the actor, add
  the addressing for the chosen audience, store a copy for later fetches, and return the object's
  id and a delivery status reference.
- **FR-042**: A publish call MUST accept an idempotency key. A repeated call with the same key MUST
  return the original result and MUST NOT create a second object or a second delivery.
  (constitution Principle V)
- **FR-043**: Megáfono MUST deliver to the shared inbox of each server where one exists, one
  request per server, and to individual inboxes otherwise. (vision FR-021)
- **FR-044**: The app MUST be able to update and delete a published object. An update MUST be
  delivered to the same audience; a deletion MUST be delivered and the object's URL MUST then
  answer "gone".
- **FR-045**: A followers-only object MUST be returned only to a signed request from an actor who
  is an approved follower at the time of the fetch. Every other request MUST receive "not found",
  never "forbidden". Public and unlisted objects are returned to anyone. (vision audience table,
  SC-003)
- **FR-046**: App extension data MUST be carried under its namespace and prefix, so other servers
  can ignore it. Matching on inbound objects is by full namespace, never by prefix.

**Delivery and events**

- **FR-050**: Deliveries and app events MUST be stored durably before Megáfono acknowledges the
  call that caused them. A restart MUST lose none. A clean shutdown or deploy MUST send nothing
  twice. After a hard crash, a delivery whose remote answer arrived but was not yet recorded MAY be
  sent once more, with the same activity id and identical content, which receiving servers
  deduplicate; two different activities for one change, or concurrent sends of one delivery, MUST
  never happen. (vision FR-023, SC-005; research R-14: exactly-once over HTTP is impossible)
- **FR-051**: Failed deliveries MUST be retried with exponential backoff starting at 1 minute,
  doubling and capped at 6 hours, for up to 48 hours. Then they MUST be dropped and reported to
  the app as `delivery.failed`. (vision FR-023)
- **FR-052**: A server that has failed every delivery for 7 days MUST be marked unreachable and
  skipped until it answers again. (vision FR-023)
- **FR-053**: Delivery MUST be limited to 2 concurrent requests per remote server and 32 overall
  by default, both configurable. (vision FR-024)
- **FR-054**: The app MUST be able to see the delivery status of each published object per
  destination server. (vision FR-041)
- **FR-055**: Every app event MUST carry a unique event id, a type, a timestamp, the user it
  concerns and the remote actor involved, and MUST be signed with an HMAC over a timestamp and the
  body. Events MUST be retried with the same backoff as deliveries (1 minute, doubling, capped at
  6 hours) until the app answers with success. Unlike remote deliveries, app events MUST never be
  dropped, however long the app stays down. (vision FR-032)
- **FR-057**: App events MUST NOT be assumed to arrive in order. Each event MUST carry a sequence
  number that increases per local user, and follow events MUST carry the relationship's current
  status at the time the event is sent, so the app can discard an event older than one it already
  applied. One failing event MUST NOT hold back other events.
- **FR-056**: Remote HTML that reaches the app MUST be sanitized to a small allowlist first.

**App contract and operation**

- **FR-060**: The app MUST only need to implement an actor lookup and an event receiver, and to
  call Megáfono's internal API. It MUST need no fediverse protocol knowledge. The actor lookup
  MUST return the user's immutable id, handle and profile, and MUST be answerable by handle (for
  handle resolution) and by immutable id (for actor fetches). (vision FR-030, FR-031,
  constitution Principle I)
- **FR-061**: The internal API MUST listen separately from the public federation endpoints, MUST
  never be reachable through the gateway, and MUST require the shared bearer token. (vision FR-033)
- **FR-062**: Megáfono MUST run as one process with one database, creating its schema itself at
  startup, and MUST expose a health check and structured logs. (vision FR-040, FR-041)
- **FR-063**: The repository MUST ship an example deployment (gateway, sample app, Megáfono,
  database) and a tested gateway configuration that follows the vision's routing rules, with
  `/ap/*` in place of `Accept`-header routing (FR-005).
- **FR-064**: The repository MUST ship a sample app whose integration code is under 100 lines.
  (vision SC-001)
- **FR-065**: The documentation MUST state that once delivered, a followers-only post can be shown
  to anyone by the receiving server. (constitution Principle II)

### Key Entities

- **Local actor**: the fediverse identity of an app user. Has a permanent id, the app's immutable
  user identifier, the current handle, actor type, locked flag, last known profile and a key pair.
- **Remote actor**: a person or service on another server. Has an id, handle, inbox, optional
  shared inbox, public key and when it was last fetched.
- **Follow relationship**: a follow between a local and a remote actor, in either direction, with
  status pending, accepted or rejected. Mutual when both directions are accepted.
- **Published object**: a post owned by a local actor. Has a permanent id, audience, content,
  extension data, idempotency key, state (live, updated, deleted) and the activities sent for it.
- **Delivery**: one attempt plan to send one activity to one destination inbox. Has status,
  attempt count, next attempt time and last error.
- **Remote server**: a destination host, with its reachability state and failure history.
- **App event**: a notification to the app, with event id, type, per-user sequence number,
  payload (including the relationship's current status for follow events), delivery attempts and
  status.
- **Processed activity**: the id of an inbound activity, kept for 7 days to drop duplicates.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A Mastodon and a GoToSocial user can each find, follow (with approval), receive a
  followers-only post from, and be refused a fetch of that post as a non-follower, in automated
  end-to-end tests against real instances of both. (vision SC-002, partial)
- **SC-002**: Across the whole test suite, a followers-only object is never returned to an
  unsigned or non-follower request. (vision SC-003)
- **SC-003**: A post to 1,000 followers on 200 servers reaches every server within 2 minutes on a
  2-vCPU host. (vision SC-004)
- **SC-004**: Stopping Megáfono at any point during delivery and starting it again loses no
  delivery. A clean stop sends no activity twice to the same inbox; a hard kill sends at most one
  repeat of an in-flight delivery, with the same activity id. (vision SC-005, as refined in FR-050)
- **SC-005**: Retrying a publish call with the same idempotency key 10 times produces one post and
  one delivery per destination.
- **SC-006**: A self-hoster with a domain and TLS gets from the example deployment to a Mastodon
  account following the test user within 10 minutes. (vision US5)
- **SC-007**: The sample app federates with under 100 lines of integration code. (vision SC-001)
- **SC-008**: Every request with a missing, invalid or expired signature is refused and never
  produces an app event.

## Assumptions

- The app can provide an immutable, never-reused identifier for each user, separate from the
  handle. Frunchy has one (a permanent internal id); apps without one cannot use Megáfono safely.
- Handles do not change in this feature. A handle change does not break the actor id, but
  announcing it to remote servers is a later feature.
- One Megáfono instance serves one app on one domain.
- The deployment has a public domain with valid TLS. Megáfono does not manage certificates; the
  gateway does.
- The reference gateway is YARP (constitution, Scope and Technology Constraints). Caddy support,
  the `megafono doctor` command and the operator CLI are later features.
- Media is hosted by the app; Megáfono only carries URLs.
- Followers-only access is checked against the follower list at the time of each fetch.
- Webhook events go to one receiver URL with one shared secret.
- Out of scope, as later features: the mutuals and direct audiences, replies, likes and mentions as
  events (vision US4), actor deletion (US6), blocks, Misskey testing and RFC 9421 signatures.
- The remaining open questions in the vision (per-actor or instance keys, RFC 9421, inbox query
  API, activity-stream table) are settled by this feature's defaults: per-actor keys, draft-cavage
  only, events only, no shared activity table.
