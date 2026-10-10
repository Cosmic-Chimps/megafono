<!--
Sync Impact Report
==================
Version change: TEMPLATE (unversioned) → 1.0.0
Bump rationale: initial ratification. First concrete constitution, derived from docs/vision.md
and the 2026-10-10 review of it (permanent actor ids, idempotent publish, real-server interop
testing, Frunchy as first consumer).

Principles defined (all new):
  I.   The App Stays Protocol-Free
  II.  Privacy Is Enforced at Every Door
  III. Verify Before Trust
  IV.  Identity Is Permanent
  V.   Durable, Idempotent Delivery
  VI.  Interop Is Proven Against Real Servers
  VII. One Process, Postgres, Boring Stack

Sections added:
  - Scope and Technology Constraints (replaces [SECTION_2_NAME])
  - Development Workflow & Quality Gates (replaces [SECTION_3_NAME])
  - Governance

Sections removed: none (template placeholders replaced in place)

Templates:
  ✅ .specify/templates/plan-template.md: its Constitution Check is a placeholder filled per
     feature by /speckit-plan; the gate table below is the source. No edit needed.
  ✅ .specify/templates/spec-template.md: no mandatory section added or removed. No edit needed.
  ✅ .specify/templates/tasks-template.md: "Tests are OPTIONAL" replaced; tests are required
     (Principle VI).
  ✅ CLAUDE.md: points to this file; actor ids "stable and never reused".
  ⚠ .claude/skills/speckit-tasks/SKILL.md line 145 still says "Tests are OPTIONAL" (managed by
     spec-kit, not edited); Principle VI overrides it.
  ⚠ README.md does not exist yet; Principle I and VII require one before v1 ships.

Deferred TODOs: none. The vision's "Open questions" stay open on purpose; this file only
constrains how they are answered.
-->

# Megáfono Constitution

Megáfono is a self-hosted ActivityPub sidecar: it sits beside any web app and makes the app's
users first-class fediverse actors, while the app keeps its own users, data and UI. It is one of
three sibling projects that each work alone: **Umbral** signs people in, an **app** such as
**Frunchy** owns the product, and **Megáfono** speaks to the fediverse. This constitution defines
the non-negotiable rules for building Megáfono. The product description lives in
`docs/vision.md`.

## Core Principles

### I. The App Stays Protocol-Free

- An app MUST be able to federate by routing paths in its reverse proxy, implementing the actor
  lookup and the webhook receiver, and calling the internal API. It MUST NOT need to know
  ActivityPub vocabulary, JSON-LD, HTTP Signatures or remote-server quirks.
- Megáfono MUST NOT depend on any particular app, identity provider or language. Frunchy is
  the reference consumer and Umbral a sibling, but no code, configuration default, schema or
  test may require either. The sample app in the repository is the proof (SC-001: under 100
  lines of integration code).
- The app contract (actor lookup, internal API, webhook payloads and signatures) is a public,
  versioned contract. Within a major version, changes MUST be additive. A breaking change
  requires a new contract version and a documented migration.
- Megáfono MUST NOT ship user-facing UI, login or timelines. The app owns all UI and all
  authentication of its users.

**Rationale**: the product is the boundary. Every protocol detail that leaks into the app is
work the app was promised it would not do.

### II. Privacy Is Enforced at Every Door

- Every object has exactly one audience (`public`, `unlisted`, `followers`, `mutuals`,
  `direct`), and Megáfono MUST enforce it both when delivering and when the object is fetched
  later.
- Non-public objects MUST answer 404, never 403, to an unauthorized or unsigned fetch, so
  their existence is not revealed. Recipient lists for `mutuals` and `direct` are frozen at
  publish time.
- When in doubt, Megáfono MUST fail closed: an unknown audience, an unverifiable requester or
  a missing follow relationship means no delivery and no content.
- Blocks (per user or per instance) MUST remove follows both ways, stop deliveries and make
  fetches from the blocked actor or domain answer 404.
- Nothing is published unless the app asks for it. Megáfono MUST NOT infer, backfill or
  re-send posts on its own.
- Documentation MUST state the protocol's limit plainly: once delivered, a remote server can
  show a post to anyone. Followers-only is a request, not a guarantee.

**Rationale**: the reason to federate with a sidecar instead of posting publicly is "friends
only" that actually holds. A leak here is worse than any outage.

### III. Verify Before Trust

- Inbound bodies MUST be read as raw bytes, capped at 1 MB, and their digest verified before
  any JSON parsing. Every inbox POST MUST carry a valid HTTP Signature or be rejected with 401
  before it reaches any handler or webhook.
- An object is trusted only when its id host matches the signing key's host; otherwise it MUST
  be refetched from its origin. Inbound activities are deduplicated by id.
- Outbound fetches of remote actors and objects MUST refuse private, loopback and link-local
  addresses (SSRF), MUST be signed, and MUST have timeouts and size limits.
- Remote HTML MUST be sanitized to an allowlist before it reaches the app.
- Private keys MUST be encrypted at rest and MUST never leave Megáfono, in logs, in API
  responses or in webhooks. The internal API MUST listen on its own port, never routed by the
  gateway, and MUST require the shared bearer token. Webhooks MUST be HMAC-signed with a
  timestamp.
- The secure behavior MUST be the default. No security property may depend on an operator
  reading a hardening guide.

**Rationale**: an inbox is a public endpoint that anyone on the internet can POST to. Megáfono
is the only thing between that traffic and the app.

### IV. Identity Is Permanent

- An actor id, once published, MUST never change and MUST never be reused for another user,
  including after the user is deleted. It MUST be derived from an identifier the app guarantees
  is immutable and never reassigned. A username that can change or be re-registered does not
  qualify.
- Object ids are permanent too. A deleted object MUST answer 410 Gone, not 404 and not a new
  object.
- An actor's key pair is created once and kept for the life of the actor. Rotation is an
  explicit operation that announces the change, never a side effect.

**Rationale**: remote servers cache actor and object ids forever. A reused id hands one
person's followers, replies and reputation to someone else, and it cannot be recalled.

### V. Durable, Idempotent Delivery

- Deliveries and webhooks MUST be queued in Postgres before Megáfono acknowledges the call
  that caused them. A restart, crash or deploy MUST lose none and MUST send none twice to the
  same inbox (SC-005).
- Every write call from the app (publish, update, delete, follow, approve) MUST be safe to
  retry: it accepts an idempotency key or is naturally idempotent, and a retry MUST NOT create
  a second object or a second delivery.
- Webhooks are at-least-once and MUST carry a stable event id so the app can deduplicate.
- Failures MUST be visible, never silent: per-object delivery status, a `delivery.failed`
  webhook, and the operator CLI. Retries follow the backoff and give-up rules in the vision.
- Remote servers MUST be treated politely: shared inboxes, per-host and global concurrency
  limits, and no delivery to a server marked unreachable until it answers again.

**Rationale**: apps retry, processes restart and remote servers go down. Duplicate posts and
lost posts are the two failures users notice, and both are prevented by design or not at all.

### VI. Interop Is Proven Against Real Servers

- Every federation behavior MUST be tested end to end against a real fediverse server in
  containers, not a mocked peer. GoToSocial runs on every test run. Mastodon and Misskey run in
  a scheduled or pre-release suite, and a release MUST NOT ship with that suite failing.
- Signature, digest, audience and fetch rules MUST be tested through the real HTTP endpoints.
  Pure functions (signing strings, addressing, sanitizing) MUST also have unit tests.
- Every interop bug fix MUST add the real payload that triggered it as a fixture, and the test
  that would have caught it.
- The privacy rules of Principle II MUST each have a test that tries to break them (SC-003).
- Tests are a required part of every feature's task list, not optional extras.

**Rationale**: ActivityPub interop is defined by what Mastodon and others actually do, not by
the spec. Only real servers reveal it.

### VII. One Process, Postgres, Boring Stack

- Megáfono ships as one process and one container image, with Postgres as its only runtime
  dependency. Message brokers, caches, ORMs, job frameworks and extra services MUST NOT be
  added without measured pain recorded in the plan's Complexity Tracking.
- The schema is created idempotently at startup. Postgres is also the queue (`FOR UPDATE SKIP
  LOCKED`).
- A self-hoster MUST get from the example compose file to a Mastodon account following a test
  user within 10 minutes (US5). `megafono doctor` MUST diagnose a misconfigured gateway, and
  every operator-facing error MUST say what to do next.
- Scale reference for design decisions: a post to 1,000 followers on 200 servers delivered
  within 2 minutes on a 2-vCPU host (SC-004). Designs MUST NOT optimize far beyond it.

**Rationale**: the audience runs a few apps on a small server. Operational weight is what a
sidecar is supposed to remove, not add.

## Scope and Technology Constraints

- **Stack**: F# on .NET 10 (`net10.0`), Oxpecker for HTTP, raw Npgsql with hand-written SQL,
  System.Text.Json, .NET `RSA` for signatures, Testcontainers for integration tests. Code MUST
  use only APIs present in the pinned package versions; upgrading is a stated decision.
- **Gateway**: YARP is the reference front door, and a Caddy config is shipped and tested. Any
  proxy that follows the routing rules in the vision MUST work. Megáfono MUST NOT rely on
  behavior specific to one proxy.
- **Protocol**: draft-cavage HTTP Signatures in v1, with verification behind a seam so other
  schemes can be added. No full JSON-LD processing: app extensions are matched by their full
  namespace IRI, never by prefix.
- **Permanently out of scope for v1**, and declined by default: a hosted or multi-tenant
  service, any user interface, ActivityPub C2S and the Mastodon client API, media storage,
  full JSON-LD processing, moderation beyond per-user and per-instance blocks, relays, boosts
  of remote posts and public timelines. Moving an item into scope requires a MINOR amendment
  with written rationale.
- **Open questions** listed in `docs/vision.md` MUST be answered in a spec or plan, with the
  decision recorded in the vision, before code depends on them.

## Development Workflow & Quality Gates

- Features go through spec-kit (specify → clarify → plan → tasks → implement) on a numbered
  feature branch (`###-feature-name`). Specs cite the US, FR and SC ids of `docs/vision.md`
  they implement.
- Specs describe what apps, users and operators get, and why; plans describe how.
- Changes to Frunchy, Umbral or any other consumer are coordinated with that project's own
  sessions, never edited from this repository.

Every plan MUST pass this Constitution Check before Phase 0 research and again after Phase 1
design:

| Gate | Question the plan must answer |
| --- | --- |
| Protocol-free app | Does the app need any ActivityPub knowledge, or does Megáfono depend on a specific app? |
| Contract | Does this change the app contract, and is the change additive? |
| Privacy | Is the audience enforced on both delivery and fetch, failing closed, with a test that tries to break it? |
| Trust | Is every inbound input verified before use, and every outbound fetch SSRF-safe? |
| Identity | Can any actor or object id change or be reused? |
| Delivery | Is every write retry-safe, and does a restart lose or duplicate nothing? |
| Interop | Is the behavior tested against a real server, with fixtures for remote quirks? |
| Operations | Does this add a runtime dependency beyond Postgres, or an operator step without a doctor check? |
| Scope | Is this on the out-of-scope list? |

## Governance

- This constitution supersedes other project documents when they conflict. `docs/vision.md`
  holds the product requirements; `CLAUDE.md` and the README hold runtime guidance, and both
  MUST stay consistent with this file.
- Amendments are made through a commit or PR that updates this file, its version and its Sync
  Impact Report, and any template or document the change invalidates.
- Versioning follows semver: MAJOR when a principle is removed or redefined so that it permits
  what it forbade, MINOR for a new principle or section or materially expanded guidance, PATCH
  for clarifications and wording.
- Compliance: the Constitution Check is evaluated at plan time and again after design. A
  violation is removed from the feature or recorded in the plan's Complexity Tracking with the
  simpler alternative that was rejected. An unjustified violation blocks the plan. Reviews
  verify the tests of Principle VI and the privacy tests of Principle II.

**Version**: 1.0.0 | **Ratified**: 2026-10-10 | **Last Amended**: 2026-10-10
