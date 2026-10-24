Megáfono: an ActivityPub sidecar for any app
8 Oct 2026 · @jjchiw
Summary
Megáfono is a self-hosted F# service that sits beside any web app and makes its users first-class ActivityPub actors. The app keeps its own users, data and UI; Megáfono owns everything the protocol needs: WebFinger, actor documents, keys, HTTP Signatures, followers, delivery and inbound verification. The app talks to it through a small HTTP API and receives webhooks. No hosted service is involved: one docker compose up with a reverse proxy (YARP), the app and Megáfono.
Problem. Federating an app today means implementing the server half of ActivityPub inside it, plus Mastodon's unwritten rules (signature variants, authorized fetch, content types, retries). Libraries such as Fedify (TypeScript) reduce the work but still live inside the app and its language. Bridgy Fed is a proxy, but hosted and tied to microformats and webmentions. Hatsu is self-hosted, but reads a site's RSS or Atom feed, mostly publishes, and keeps users on the Hatsu domain. Vocata is a generic AP server, but expects clients to speak ActivityPub C2S. Nothing offers "put this in front of your app, get webhooks and a publish endpoint".
First consumer. Frunchy (F#, Oxpecker, Postgres) will be the reference integration, but nothing in Megáfono may depend on it.
Goals and non-goals
Goals (v1)
• An app becomes federated by adding routes to its reverse proxy and implementing two internal endpoints. No ActivityPub code in the app.
• Users can follow each other across instances, including from Mastodon, GoToSocial and Misskey.
• "Friends only" posting that is actually private: locked accounts, follower-only addressing, signed fetch.
• Apps can attach their own structured data to posts, readable by other instances of the same app and ignored gracefully elsewhere.
• Self-hosted, single binary or container, Postgres as the only dependency.
Non-goals (v1)
• A hosted or multi-tenant service.
• A user interface: no timelines, no login, no admin screens. The app owns all UI.
• ActivityPub client-to-server (C2S) or the Mastodon client API.
• Media storage and upload: the app hosts its own files and sends URLs.
• Full JSON-LD processing.
• Moderation tooling beyond per-user and per-instance blocks.
• Relays, boosts of remote posts and public timelines (candidates for v2).
User stories
US1 — Discoverable actor (P1). As an app developer, I want my users to be found from any fediverse server without writing protocol code.
1. Given user alex exists in the app, when a Mastodon user searches @alex@app.example.com, then Mastodon shows the profile with the app's display name and summary.
2. Given bob does not exist in the app, when anyone resolves @bob@app.example.com, then WebFinger answers 404.
3. Given a browser opens https://app.example.com/u/alex, then it gets the app's HTML page, not JSON.
US2 — Friends by approval (P1). As an app user, I want to approve who follows me.
1. Given alex's actor is locked, when a remote user sends Follow, then the app receives a follow.requested webhook and the follow stays pending.
2. When the app calls approve, then Megáfono sends Accept and the follower is added. When it calls reject, Megáfono sends Reject.
3. Given alex and a remote user follow each other, then the follower list marks them as mutual.
US3 — Publish to friends (P1). As an app developer, I want one call to post something to a user's followers.
1. When the app calls publish with audience followers, then every follower's inbox (or shared inbox) receives a signed Create within 1 minute.
2. Given a follower's server is down, then delivery is retried with backoff for up to 48 hours, then dropped and reported.
3. Given the post is followers-only, when an unsigned or non-follower request fetches its URL, then Megáfono answers 404.
US4 — Interactions come back (P2). As an app developer, I want replies, likes and mentions as webhooks. Replies (reply.received, reply.updated, object.deleted, and publishing a reply) are built in specs/002-replies-threads; likes and mentions are still open.
1. When a remote user replies to or likes a post, then the app receives reply.received or like.received with the verified actor and object.
2. Given an activity with an invalid signature, then it is rejected with 401 and never reaches the app.
US5 — Self-host in minutes (P1). As a self-hoster, I want a compose file that just works.
1. Given a domain with TLS, when I run the example compose file, then a Mastodon account can follow a test user within 10 minutes of setup.
US6 — Leaving cleanly (P2). When the app deletes a user, then Megáfono sends Delete for the actor to every known follower server and removes the user's keys and follow records. Built in specs/003-actor-deletion: DELETE /internal/actors/{id} sends Delete to every server that follows or is followed by the actor, the id and its posts then answer 410 Gone and the id is never reused; keys are erased once the Delete deliveries end.
Architecture
Inbound traffic enters only through the gateway, which splits it by path. Outbound deliveries leave Megáfono directly. The app and Megáfono talk over a private port, and each keeps its own database: the app owns users and content, Megáfono owns keys, follow relationships, published object copies and the delivery queue.
Functional requirements
Identity and discovery
• FR-001 Serve /.well-known/webfinger for acct:{user}@{domain}, resolving the user through the app's actor lookup (FR-030). Cache positive answers for 5 minutes.
• FR-002 Serve /.well-known/nodeinfo and NodeInfo 2.1 with the app's configured software name and version.
• FR-003 Serve actor documents (Person by default, Service or Group if the app says so) with inbox, outbox, followers, following, shared inbox and publicKey.
• FR-004 The actor id is https://{domain}/ap/actors/{id}, where {id} is an identifier the app guarantees is immutable and never reused. The actor's url is the app's human profile URL (decided in specs/001-federation-core).
• FR-005 Generate one RSA-2048 keypair per actor on first lookup. Private keys never leave Megáfono and are encrypted at rest.
Inbound
• FR-010 Verify draft-cavage HTTP Signatures (rsa-sha256 and hs2019) on every inbox POST: (request-target), host, date (±12 hours), digest. Reject with 401 otherwise.
• FR-011 Fetch and cache remote actors and keys; on verification failure, refetch the key once (key rotation).
• FR-012 Handle Follow, Undo Follow, Accept, Reject, Create (Note, Article and app types), Like, Undo Like, Delete, Update (actor), Block. Unknown types are acknowledged with 202 and dropped. An inbound Block (built in specs/004-blocks) ends the follows between the blocker and the local user, with follow.removed webhooks. A Create, Update or Delete of a Note or Article by an account a local user follows (built in specs/005-followed-posts) becomes post.received, post.updated or post.deleted, one per following user; Announce (boosts) is acknowledged and dropped.
• FR-013 Deduplicate inbound activities by id for 7 days.
• FR-014 Accept the body as raw bytes and verify the digest before parsing JSON.
Outbound
• FR-020 Wrap app objects in Create, Update or Delete, assign ids under the actor, and store them for dereferencing.
• FR-021 Deliver to shared inboxes where available, one request per server.
• FR-022 Sign every outbound request, including GETs of remote objects (needed for servers in authorized-fetch mode).
• FR-023 Queue deliveries in Postgres; retry with exponential backoff (1 min doubling, capped at 6 hours) for 48 hours; mark a server unreachable after 7 days of failures and stop delivering until it answers again.
• FR-024 Limit concurrency per remote host (default 2) and globally (default 32).
App integration
• FR-030 Actor lookup: call the app's GET /megafono/actors?handle={handle} or GET /megafono/actors/{id} (immutable id); 404 means the user does not exist.
• FR-031 Internal API for publish, follow approval, follow and unfollow, block and actor deletion (see App contract). Blocks are built in specs/004-blocks: per user (an actor by URL or handle, or a domain) and per instance (a domain or an actor), domains covering their subdomains.
• FR-032 Webhooks for every inbound event, signed with HMAC-SHA256 over the body and a timestamp, retried like deliveries.
• FR-033 The internal API listens on a separate port that the reverse proxy never routes to, and requires a shared bearer token.
Operations
• FR-040 One process, Postgres as the only dependency; schema created idempotently at startup. Postgres is chosen on purpose: LISTEN/NOTIFY, triggers and pg_cron let scheduled jobs and other consumers observe the activity stream.
• FR-041 Health endpoint, structured logs, and a delivery status endpoint per object.
• FR-042 A CLI for operators: list actors, show delivery failures, retry, block an instance.
App contract
The app implements one endpoint and one webhook receiver; everything else is calls into Megáfono. All bodies are JSON with camelCase names.
The app implements
Endpoint
Purpose
GET /megafono/actors?handle={handle} and GET /megafono/actors/{id}
200 with { id, handle, name, summary?, iconUrl?, profileUrl, type?, locked }, or 404 (contract v1: specs/001-federation-core/contracts/app-contract.md)
POST /megafono/webhooks
Receives events; 2xx = handled, anything else = retried
Megáfono exposes (internal port only)
Call
Purpose
POST /internal/actors/{user}/objects
Publish an object to an audience; returns its id and delivery status URL
PUT /internal/objects/{id}
Update a published object (sends Update)
DELETE /internal/objects/{id}
Delete it (sends Delete, then answers 410 Gone on fetch)
POST /internal/actors/{user}/follow-requests/{id}/approve and /reject
Answer a pending follow
POST /internal/actors/{user}/following
Follow a remote actor by handle or URL
DELETE /internal/actors/{user}/following/{actor}
Unfollow
GET /internal/actors/{user}/followers and /following
Lists with mutual and status
POST /internal/actors/{user}/blocks
Block an actor or a whole domain (also GET and DELETE; POST/GET/DELETE /internal/blocks for the instance, specs/004-blocks)
PUT /internal/actors/{user}
Profile changed: send Update to followers
DELETE /internal/actors/{user}
Delete the actor everywhere (US6)
GET /internal/remote-actors?handle=
Resolve a remote handle (for "add friend" screens)
Publish example
POST /internal/actors/alex/objects
{
  "audience": "followers",
  "object": {
    "type": "Note",
    "content": "<p>Finished Frieren S1, 28 episodes</p>",
    "url": "https://app.example.com/u/alex/watch/123",
    "inReplyTo": null,
    "attachment": [{ "type": "Image", "url": "https://app.example.com/img/frieren.jpg" }]
  },
  "extensions": {
    "namespace": "https://app.example.com/ns#",
    "prefix": "app",
    "data": { "watch": { "malId": 52991, "episodes": 28 } }
  }
}
Megáfono adds the @context, ids, attributedTo, to/cc, published, and the extension under its prefix. Remote objects carrying the same namespace are passed back to the app in webhooks under extensions.
Webhook events
follow.requested, follow.accepted, follow.rejected, follow.removed, reply.received, reply.updated, post.received, post.updated, post.deleted, mention.received, like.received, like.removed, object.deleted, actor.updated, actor.deleted, delivery.failed.
{
  "id": "evt_01J…",
  "type": "reply.received",
  "occurredAt": "2026-10-08T18:00:00Z",
  "user": "alex",
  "actor": { "id": "https://mastodon.social/users/bob", "handle": "@bob@mastodon.social", "name": "Bob" },
  "object": { "id": "…", "type": "Note", "content": "<p>so good</p>", "inReplyTo": "…" },
  "extensions": null
}
Headers: Megafono-Signature: t=<unix>,v1=<hex hmac-sha256 of t.body>. Events carry an id so the app can deduplicate retries. Remote HTML in content is sanitized to a small allowlist before it reaches the app.
Audience and privacy
Every published object has one audience, and Megáfono enforces it both when delivering and when the object is fetched later.
Audience
to / cc
Who can fetch the object URL
Mastodon shows it as
public
Public / followers
Anyone
Public
unlisted
followers / Public
Anyone
Unlisted
followers
followers
Signed request from an approved follower
Followers-only (lock icon)
mutuals
each mutual, listed
Signed request from one of the listed actors
Mentioned people only
direct
the listed actors
Signed request from a listed actor
Private mention
Rules:
• "Friends" is the mutuals audience: both actors follow each other and both follows were approved. Megáfono computes it at publish time and freezes the recipient list on the object.
• Non-public objects answer 404, never 403, to unauthorized fetches, so their existence is not revealed.
• An actor's locked flag comes from the app. Locked actors set manuallyApprovesFollowers: true, and every Follow waits for the app's decision.
• A Block (by the user or the instance) removes the follow both ways, stops deliveries, and makes fetches from that actor or domain answer 404 (built in specs/004-blocks; a user block of an actor also sends Block to its server, instance blocks send nothing, and the blocked side's replies leave the reply collections).
• Known limit, stated in the docs: once delivered, a remote server can show a post to anyone. Followers-only on the fediverse is a request, not a guarantee.
Deployment
The reference setup is three containers and Postgres. YARP is the default front door, but any reverse proxy (Caddy, nginx, Traefik) works if it follows the rules below; the repo ships tested configs for YARP and Caddy.
services:
  gateway:   # YARP, terminates TLS, the only public service
    image: megafono/gateway
    ports: ["443:443"]
  app:
    image: my-app
    environment:
      MEGAFONO__URL: http://megafono:8081
      MEGAFONO__TOKEN: ${MEGAFONO_TOKEN}
      MEGAFONO__WEBHOOKSECRET: ${MEGAFONO_WEBHOOK_SECRET}
  megafono:
    image: megafono/megafono
    environment:
      Megafono__Domain: app.example.com
      Megafono__ProfileUrl: https://app.example.com/u/{user}
      Megafono__App__BaseUrl: http://app:8080
      Megafono__App__Token: ${MEGAFONO_TOKEN}
      Megafono__App__WebhookSecret: ${MEGAFONO_WEBHOOK_SECRET}
      ConnectionStrings__Database: Host=db;Database=megafono;Username=megafono
    # 8080 = federation (routed by the gateway), 8081 = internal API (never routed)
  db:
    image: postgres:17
Routing rules the gateway must follow
1. To Megáfono: /.well-known/webfinger, /.well-known/nodeinfo, /nodeinfo/*, /ap/* and /inbox. No routing by Accept header is needed.
2. Everything else to the app.
3. Preserve the original Host header (YARP: RequestHeaderOriginalHost: true). Signatures cover it.
4. Do not rewrite the path or the body on Megáfono routes. Signatures cover the path and the body digest.
5. Forward X-Forwarded-Proto and X-Forwarded-For.
6. Never route the internal port.
Megáfono runs a self-check at startup and from the CLI (megafono doctor): it signs a request to its own public inbox through the gateway and reports which rule is broken if verification fails.
Implementation stack. F# on .NET 10, Oxpecker for HTTP, raw Npgsql with Postgres as the delivery and webhook queue (FOR UPDATE SKIP LOCKED), System.Text.Json, .NET RSA for signatures. Tests run against GoToSocial and Mastodon in Testcontainers.
Success criteria
• SC-001 A developer federates a sample app (provided in the repo) by writing under 100 lines of integration code.
• SC-002 Follow, approve, publish followers-only, reply and like round-trip with Mastodon 4.x, GoToSocial and Misskey in automated tests.
• SC-003 A followers-only object is never returned to an unsigned or non-follower fetch (tested).
• SC-004 A post to 1,000 followers on 200 servers is fully delivered within 2 minutes on a 2-vCPU host.
• SC-005 Restarting Megáfono mid-delivery loses no deliveries and sends none twice to the same inbox.
Edge cases
• A username in the app is renamed: actor ids are built from the app's immutable id, so a rename never changes the actor id (decided in specs/001-federation-core). Announcing the new handle to remote servers is a later feature.
• The app is down: inbound activities are stored and webhooks retried; actor lookups fall back to the last cached profile for 24 hours.
• A remote actor moves (Move activity): v1 logs it and sends actor.moved; following the move is v2.
• A remote server sends the same activity with different ids, or forwards activities it did not author: only trust objects whose id host matches the signing key's host, otherwise refetch from origin.
• Very large inbound bodies: cap at 1 MB.
Open questions
[x] Name: the code lives at github.com/Cosmic-Chimps/megafono (MIT) and the images at ghcr.io/cosmic-chimps/megafono and ghcr.io/cosmic-chimps/megafono-gateway. "megafono" is free on NuGet, but the GitHub organization and the Docker Hub namespace "megafono" belong to others (checked 2026-10-16), so no image is ever named docker.io/megafono/…. The brand keeps the accent (Megáfono); code, packages and images use megafono.
[x] Actor URL: a separate /ap/actors/{immutableId} path whose url is the app's profile, no Accept-header routing (specs/001-federation-core, clarified 2026-10-10).
[x] Keys: one RSA-2048 key pair per actor, plus one instance actor key for fetches not made on behalf of a user (specs/001-federation-core, FR-006 and FR-022).
[x] Signatures: draft-cavage only in v1, behind a SignatureScheme seam; Mastodon signs draft-cavage first and only falls back to RFC 9421 after a 4xx (specs/001-federation-core, research R-01).
[x] Inbox query API: not in v1; apps keep what they need from webhooks, which are never dropped (specs/001-federation-core, FR-055). Revisit when replies and mentions arrive (US4 of this vision). Revisited for replies (specs/002-replies-threads) and for posts from followed accounts (specs/005-followed-posts): still events only; the app builds its feed from post.received and keeps what it shows.
[x] Activity stream table: not in v1; webhooks are the only public channel, so the database schema stays private (specs/001-federation-core).