# Implementation Plan: Federation Core

**Branch**: `001-federation-core` | **Date**: 2026-10-10 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/001-federation-core/spec.md`

## Summary

Build the first Megáfono release: an F# service, one process with a federation port and an internal
port, that makes an app's users discoverable fediverse actors, handles approved follows in both
directions, and publishes Notes and Articles to public, unlisted or followers-only audiences with
durable, idempotent delivery. The app talks to it through an actor lookup, an event receiver and the
internal API ([contracts/](contracts/)). Actor ids live under `/ap/actors/{appUserId}`, so the
gateway routes by path only. Every behavior is proven against real GoToSocial (every run) and
Mastodon (scheduled) servers behind TLS in containers, with Megáfono running in the test process
([research.md](research.md) R-15). The feature also ships a small YARP gateway, a sample app and an
example compose deployment.

## Technical Context

**Language/Version**: F# on .NET 10 (`net10.0`)

**Primary Dependencies**: Oxpecker 2.1.1 (HTTP), Npgsql 10.0.3 (raw SQL), System.Text.Json,
.NET `RSA`/`AesGcm`/`SHA256`, `Microsoft.AspNetCore.RateLimiting` (shared framework),
HtmlSanitizer 9.2.1039; gateway: Yarp.ReverseProxy 2.3.0

**Storage**: PostgreSQL 17, Megáfono's own database (schema created idempotently at startup; also
the delivery and event queue) — [data-model.md](data-model.md)

**Testing**: xUnit v3 4.0.1 on Microsoft.Testing.Platform; Testcontainers (Postgres, Caddy,
GoToSocial, Mastodon); real Kestrel hosts in the test process; a fake app and a stub inbox fleet

**Target Platform**: Linux containers (amd64, arm64); development on macOS with Docker Desktop

**Project Type**: web service (sidecar) plus a gateway, a sample app and test projects

**Performance Goals**: a post to 1,000 followers on 200 servers delivered within 2 min on 2 vCPU
(SC-003); delivery starts within 1 min of publish (US3)

**Constraints**: inbound body ≤ 1 MB; per-host 2 and global 32 delivery concurrency; ±12 h date
window; secrets only from configuration; no runtime dependency besides Postgres

**Scale/Scope**: one app, one domain, up to thousands of local actors and tens of thousands of
follow relationships; this feature covers spec US1–US5 (vision US1, US2, US3, part of US5)

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

| Gate | Answer before research | After design |
|---|---|---|
| Protocol-free app | The app implements an actor lookup and an event receiver and calls the internal API; no ActivityPub in the app. Nothing references Frunchy or Umbral. | ✅ [app-contract.md](contracts/app-contract.md) has no protocol fields; the sample app shows it in < 100 lines |
| Contract | New contract, version 1, carried in every event (`version`) | ✅ additive-only rule stated in both contract files; vision's single `GET /megafono/actors/{user}` becomes the by-handle and by-id pair, recorded in the vision |
| Privacy | Audience enforced on delivery (addressing, recipients = accepted followers) and on fetch (signed accepted follower or 404), failing closed | ✅ fetch table in [federation.md](contracts/federation.md); tests for unsigned, non-follower, removed follower and instance-actor fetches |
| Trust | Raw bytes, size cap, digest, signature before parsing; SSRF-safe client; sanitizing; keys encrypted | ✅ inbox order in federation.md; R-11, R-12, R-13, R-14 |
| Identity | Actor ids from the app's immutable id; object and activity ids are UUIDs; rows never deleted | ✅ data-model.md; deleted objects answer 410 |
| Delivery | Queued in the publish transaction; idempotency key; unique (activity, inbox); leases | ✅ data-model.md queues; the crash-window limit is stated in spec FR-050 and SC-004 |
| Interop | GoToSocial every run, Mastodon scheduled, real payloads kept as fixtures | ✅ R-15; quickstart sections 2 and 3; four harness spikes come first |
| Operations | One process, Postgres only; compose example; health endpoint | ✅ no new runtime dependency; `megafono doctor` is a later feature (spec scope) |
| Scope | Mutuals, direct, replies, likes, deletion of actors, blocks, Caddy config, CLI are out | ✅ none of them in the design |

No violations; Complexity Tracking stays empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-federation-core/
├── plan.md              # This file
├── research.md          # Phase 0: decisions R-01 to R-15
├── data-model.md        # Phase 1: tables, states, validation
├── quickstart.md        # Phase 1: how to validate
├── contracts/
│   ├── app-contract.md  # what the app implements (actor lookup, events)
│   ├── internal-api.md  # Megáfono's internal API
│   └── federation.md    # public endpoints, fetch and inbox rules
├── checklists/requirements.md
└── tasks.md             # Phase 2 (/speckit-tasks)
```

### Source Code (repository root)

```text
Megafono.slnx
global.json                       # SDK 10, Microsoft.Testing.Platform runner
Directory.Build.props             # net10.0, warnings as errors, nullable off (F#)
Directory.Packages.props          # central package versions (table in research.md)

src/Megafono/                     # the service
├── Domain/                       # pure, no I/O
│   ├── Ids.fs                    # actor/object/activity IRIs, id validation
│   ├── Audience.fs               # audience → to/cc (R-06)
│   ├── ActivityStreams.fs        # builders for actor, collections, Note/Article, activities
│   ├── Events.fs                 # app event envelope and types
│   └── Backoff.fs                # retry schedule
├── Infrastructure/
│   ├── Config.fs                 # options, startup validation (master key, software name)
│   ├── Database.fs               # connection, idempotent schema bootstrap
│   ├── KeyVault.fs               # RSA generation, AES-GCM at rest (R-11)
│   ├── HttpSignatures.fs         # draft-cavage sign/verify, digest, SignatureScheme seam (R-01)
│   ├── SafeHttp.fs               # SSRF-safe signed client (R-13)
│   ├── RemoteActors.fs           # fetch, cache, key refetch, handle resolution
│   ├── AppClient.fs              # actor lookup client with 5 min cache and 24 h fallback
│   ├── Sanitizer.fs              # HtmlSanitizer allowlist (R-14)
│   ├── RateLimits.fs             # two-layer limits (R-12)
│   └── PortGuard.fs              # port separation and bearer check (R-10)
├── Federation/                   # public endpoints, one file per endpoint group
│   ├── WebFinger.fs
│   ├── NodeInfo.fs
│   ├── Actors.fs                 # local actors, instance actor, collections
│   ├── Objects.fs                # objects and activities with fetch rules
│   └── Inbox.fs                  # verification pipeline and dispatch
├── Internal/                     # internal API, one file per resource
│   ├── Objects.fs                # publish, update, delete, delivery status
│   ├── Actors.fs                 # profile refresh
│   ├── Follows.fs                # approve/reject, follow/unfollow, lists
│   └── RemoteActors.fs
├── Workers/
│   ├── Dispatcher.fs             # shared claim/lease loop, LISTEN/NOTIFY wake-up, drain
│   ├── Deliveries.fs             # remote delivery, host reachability
│   ├── AppEvents.fs              # signed webhook delivery
│   └── Housekeeping.fs           # prune processed activities
└── Program.fs                    # composition root, two Kestrel ports

src/Megafono.Gateway/             # YARP reference gateway (R-14)
├── Program.fs
└── appsettings.json              # routes, RequestHeaderOriginalHost

samples/SampleApp/                # Oxpecker app, < 100 lines of integration (SC-007)

deploy/
├── compose.yaml                  # gateway, sample app, Megáfono, Postgres
├── .env.example
└── Dockerfile.megafono, Dockerfile.gateway, Dockerfile.sample

tests/Megafono.Tests/             # unit + integration: Postgres container, real Kestrel, fake app,
                                  # stub inbox fleet, fake TimeProvider, crash fault hook
tests/Megafono.Interop.Tests/     # test CA, Caddy fronts, GoToSocial (every run),
                                  # Mastodon (Trait Suite=mastodon), fixtures/ of real payloads

scripts/count-integration-lines.sh
docs/                             # vision.md; operator docs (master key backup, privacy limit FR-065)
```

**Structure Decision**: one service project with a pure `Domain/` layer (unit-tested without I/O),
`Infrastructure/` for I/O building blocks, and endpoint files grouped by surface (`Federation/`
public, `Internal/` private) so the port boundary is visible in the tree. Workers share one
dispatcher. The gateway, sample app and two test projects are separate projects; the interop
suite is its own project because its fixtures are heavy and its Mastodon part runs on a schedule.

## Design notes

- **Request flow, publish**: internal API → app lookup (cached) → one transaction: object,
  Create activity (exact bytes), delivery rows for the distinct inboxes of accepted followers
  (`ON CONFLICT DO NOTHING`), `NOTIFY` → 201. The dispatcher picks rows up within seconds.
- **Request flow, inbound Follow**: inbox pipeline (federation.md) → upsert remote actor →
  `follows` row → if unlocked, queue an Accept delivery and a `follow.accepted` event; if locked, a
  `follow.requested` event — all in one transaction → 202.
- **Followers-only fetch**: verify the GET signature (rate limited like the inbox), resolve the key
  owner, check an accepted inbound follow to the author, else 404.
- **Clocks and faults**: workers and expiry checks use an injected `TimeProvider`; the delivery
  worker exposes a test-only fault hook between the remote answer and the database write (SC-004).
- **Config** (`Megafono__…`): `Domain`, `App__BaseUrl`, `App__Token`, `App__WebhookSecret`,
  `MasterKey`, `Software__Name`, `Software__Version`, `Ports__Federation`, `Ports__Internal`,
  `Delivery__PerHost`, `Delivery__Global`, `RateLimit__PerAddress`, `RateLimit__PerHost`,
  `TrustedProxies`, `Federation__AllowPrivateAddresses` (tests only, refused in Production).
- **Order of work** (for `/speckit-tasks`): harness spikes (R-15) → schema, keys, signatures,
  SafeHttp → discovery (US1) → inbox and inbound follows (US2) → publishing and delivery (US3) →
  outbound follows (US4) → gateway, sample app, compose, docs (US5) → load and crash tests.

## Complexity Tracking

No constitution violations to justify.
