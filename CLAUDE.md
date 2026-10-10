# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Status

Pre-implementation. Engineering rules and the plan-time Constitution Check live in `.specify/memory/constitution.md`; features go through spec-kit. The repo has no code yet, only `docs/vision.md` (the spec: goals, user stories US1–US6, FR-xxx requirements, SC-xxx success criteria, edge cases, open questions). There are no build, lint or test commands yet; add them here once the solution exists. When implementing, cite the FR/US/SC ids from `docs/vision.md` and check the "Open questions" section before making a call it lists as undecided (actor URL shape, per-actor vs instance keys, RFC 9421 support, inbox query API, activity-stream table).

## What this is

Megáfono (code, packages and images use `megafono`; the brand keeps the accent) is a self-hosted ActivityPub sidecar. A host app keeps its own users, data and UI; Megáfono owns everything protocol-level: WebFinger, NodeInfo, actor documents, keys, HTTP Signatures, followers, delivery and inbound verification. The app integrates through a small HTTP API plus webhooks and contains no ActivityPub code.

Planned stack: F# on .NET 10, Oxpecker for HTTP, raw Npgsql (no ORM), Postgres as the only dependency, System.Text.Json, .NET `RSA` for signatures. Integration tests run against GoToSocial and Mastodon in Testcontainers.

Frunchy (F#, Oxpecker, Postgres) is the first reference consumer, but nothing in Megáfono may depend on it. Frunchy changes are coordinated from its own sessions, not edited from here.

## Architecture (big picture)

- **Three deployables plus Postgres**: a gateway (YARP by default; Caddy config also shipped and tested) is the only public service. Megáfono is one process with two ports: 8080 federation (routed by the gateway) and 8081 internal API (never routed, bearer-token auth). The app sits behind the gateway.
- **Routing split lives in the gateway**: `/.well-known/webfinger`, `/.well-known/nodeinfo`, `/nodeinfo/*`, `/ap/*`, `/inbox`, and the actor URL path when `Accept` contains `application/activity+json` or `application/ld+json` go to Megáfono. Everything else goes to the app. The gateway must preserve the original `Host`, must not rewrite path or body on Megáfono routes (signatures cover them), and must forward `X-Forwarded-*`. `megafono doctor` self-checks this by signing a request to its own inbox through the gateway.
- **Data ownership**: the app owns users and content. Megáfono owns keys (RSA-2048 per actor, encrypted at rest, never leave Megáfono), follow relationships, copies of published objects (for dereferencing), the delivery queue and the webhook queue. Separate databases.
- **App contract**: the app implements `GET /megafono/actors/{user}` (200 profile or 404) and `POST /megafono/webhooks` (2xx = handled, otherwise retried). Everything else is calls into `/internal/*` on Megáfono. JSON is camelCase. Webhooks carry `Megafono-Signature: t=<unix>,v1=<hmac-sha256 of "t.body">` and an event id for dedup; remote HTML is sanitized to an allowlist before reaching the app.
- **Queues in Postgres**: deliveries and webhooks use `FOR UPDATE SKIP LOCKED`. Retries use exponential backoff (1 min doubling, capped at 6h) for 48h; a server is marked unreachable after 7 days of failures. Concurrency limits per host (default 2) and global (default 32). Delivery uses shared inboxes, one request per server. Restart mid-delivery must lose nothing and duplicate nothing to the same inbox (SC-005).
- **Audience enforcement happens at both publish and fetch time**: `public`, `unlisted`, `followers`, `mutuals`, `direct`. "Friends" means `mutuals`, computed at publish time with the recipient list frozen on the object. Non-public objects answer **404, never 403**, to unauthorized fetches. Blocks remove follows both ways, stop deliveries and make fetches from that actor or domain 404.
- **App extensions**: published objects can carry app data under a namespace and prefix. Remote objects with the same namespace are passed back to the app in webhooks under `extensions`; other servers ignore them.

## Protocol rules that are easy to get wrong

- Inbound: read the body as raw bytes and verify the digest **before** parsing JSON; body cap 1 MB. Verify draft-cavage signatures (`rsa-sha256` and `hs2019`) over `(request-target)`, host, date (±12h) and digest; 401 on failure. On a key mismatch, refetch the remote key once (rotation) before rejecting. Deduplicate activities by id for 7 days. Unknown activity types get 202 and are dropped.
- Only trust an object whose id host matches the signing key's host; otherwise refetch from the origin.
- Outbound: sign every request, including GETs of remote objects (authorized-fetch servers require it).
- Locked actors set `manuallyApprovesFollowers: true`; every Follow waits for the app's approve/reject.
- Actor ids must be stable and never reused, even after the user is deleted, so they must come from an immutable app id rather than a username that can be re-registered (constitution Principle IV). The actor URL is a configurable template (default `https://{domain}/u/{user}`); its shape is still an open question.
- Limitation to state in docs, not hide: followers-only on the fediverse is a request, not a guarantee once delivered.

## Scope guardrails (v1 non-goals)

No UI, no hosted/multi-tenant mode, no ActivityPub C2S or Mastodon client API, no media storage, no full JSON-LD processing, no relays/boosts/public timelines, moderation limited to per-user and per-instance blocks.
