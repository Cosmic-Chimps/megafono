---

description: "Task list for 001-federation-core"
---

# Tasks: Federation Core

**Input**: Design documents from `specs/001-federation-core/` ([plan.md](plan.md), [spec.md](spec.md),
[research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/),
[quickstart.md](quickstart.md))

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/

**Tests**: REQUIRED (constitution Principle VI). Every federation behavior has an integration test
against a real Postgres and a real Kestrel host, and an interop test against real GoToSocial on
every run. Mastodon runs as a scheduled suite (Phase 8). Every privacy rule has a test that tries
to break it. Write each story's tests first and see them fail.

**Organization**: by user story (spec US1–US5), after Setup and Foundational phases.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1–US5 from spec.md
- **F# compile order matters**: every new `.fs` file is added to its `.fsproj` in dependency order
  (Domain → Infrastructure → Federation/Internal → Workers → Program). A task that creates a file
  includes adding it there.
- Cite the spec's FR/SC ids in test names or comments, e.g. `FR-045 followers-only fetch by a
  non-follower answers 404`.

## Path Conventions

Single repository, layout from plan.md: `src/Megafono/`, `src/Megafono.Gateway/`,
`samples/SampleApp/`, `deploy/`, `tests/Megafono.Tests/`, `tests/Megafono.Interop.Tests/`,
`scripts/`, `docs/`.

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: solution, projects and pinned packages.

- [X] T001 Create `Megafono.slnx`, `global.json` (SDK 10.0.x, `"test": { "runner": "Microsoft.Testing.Platform" }`), `Directory.Build.props` (`net10.0`, `TreatWarningsAsErrors`, `LangVersion` latest) and `Directory.Packages.props` with central versions from research.md (Oxpecker 2.1.1, Npgsql 10.0.3, HtmlSanitizer 9.2.1039, Yarp.ReverseProxy 2.3.0, xunit.v3 4.0.1, Testcontainers 4.15.0, Testcontainers.PostgreSql 4.15.0) at the repository root
- [X] T002 Create `src/Megafono/Megafono.fsproj` (`Microsoft.NET.Sdk.Web`, references Oxpecker, Npgsql, HtmlSanitizer) with a minimal `src/Megafono/Program.fs` that builds a `WebApplication` through an exported `buildApp (args: string[]) (overrides: (string * string) list)` function (tests reuse it) and runs it
- [X] T003 [P] Create `tests/Megafono.Tests/Megafono.Tests.fsproj` (xUnit v3, `OutputType` Exe, Microsoft.Testing.Platform, Testcontainers.PostgreSql, project reference to `src/Megafono`) with one passing smoke test in `tests/Megafono.Tests/SmokeTests.fs`
- [X] T004 [P] Create `tests/Megafono.Interop.Tests/Megafono.Interop.Tests.fsproj` (xUnit v3, Testcontainers, project reference to `src/Megafono`) with an empty `tests/Megafono.Interop.Tests/Fixtures/` folder (harness code) and a `Payloads/` folder for captured payloads (not `fixtures/`: macOS file systems are case-insensitive)
- [X] T005 Add `Megafono.slnx` entries for all projects and confirm `dotnet build` and `dotnet test` pass from the root; add the commands to the "Status" section of `CLAUDE.md`

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: the test harness, storage, crypto, signatures, safe HTTP, port separation, queues and
app plumbing that every story uses. No story work starts before this phase is done.

### Harness spikes (research R-15; resolve the four unverified points first)

- [X] T006 Spike the GoToSocial harness in `tests/Megafono.Interop.Tests/Fixtures/TestCa.fs` and `tests/Megafono.Interop.Tests/Fixtures/GoToSocialFixture.fs`: generate a root CA and leaf certs (`megafono.test`, `gts.test`) with `CertificateRequest`; start a Testcontainers network, a Caddy container aliased `gts.test` terminating TLS in front of a pinned GoToSocial image (SQLite, `GTS_HOST=gts.test`, `SSL_CERT_FILE` = mounted CA, `http-client.allow-ips` = network subnet, rate limiting off); create an account with `gotosocial admin account create`; obtain a user token without a browser (scripted sign-in + authorize + `/oauth/token`); expose a host Kestrel port with `TestcontainersSettings.ExposeHostPortsAsync` and a second Caddy aliased `megafono.test` proxying to `host.testcontainers.internal:<port>` with Host preserved; prove GTS can GET a static JSON from it. Record what worked (exact env vars, image tag, token flow, macOS vs Linux notes) under R-15 in `specs/001-federation-core/research.md`
- [X] T007 Spike the Mastodon harness in `tests/Megafono.Interop.Tests/Fixtures/MastodonFixture.fs`: pinned `ghcr.io/mastodon/mastodon` 4.x web + sidekiq + Postgres + Redis on the same network with a fixed subnet, production mode, `LOCAL_DOMAIN=mastodon.test`, generated secrets and Active Record encryption keys, `ALLOWED_PRIVATE_ADDRESSES` = subnet, `AUTHORIZED_FETCH=true`, CA bundle via `SSL_CERT_FILE`; Caddy aliased `mastodon.test`; account via `tootctl accounts create --confirmed --approve`; token via a `rails runner` script. Prove Mastodon fetches the same static JSON from `megafono.test`. Record findings under R-15 in `specs/001-federation-core/research.md`; if the CA can't be trusted, stop and report the blocker
- [X] T008 Turn the spike code into reusable fixtures: an xUnit v3 assembly fixture `tests/Megafono.Interop.Tests/Fixtures/FederationNetwork.fs` (network, CA, Caddy fronts, host-port exposure) and a shared Mastodon-compatible client `tests/Megafono.Interop.Tests/Fixtures/FediClient.fs` (search with `resolve=true`, follow, unfollow, relationships, home timeline, get status, poll-until helper with 60 s timeout); Mastodon tests carry `[<Trait("Suite", "mastodon")>]`

### Core

- [X] T009 Implement configuration and startup validation in `src/Megafono/Infrastructure/Config.fs`: options from plan.md "Config" (`Megafono__…`), refusing to start when `MasterKey` is not base64 of 32 bytes, `Domain` is empty, `Software__Name` fails `^[a-z0-9-]+$`, or `Federation__AllowPrivateAddresses=true` under `ASPNETCORE_ENVIRONMENT=Production`; log the test switch loudly
- [X] T010 Implement `src/Megafono/Infrastructure/Database.fs`: `NpgsqlDataSource` registration and an idempotent `initializeSchema` creating every table, constraint and index in data-model.md (`instance_actor`, `local_actors`, `remote_actors`, `remote_hosts`, `follows`, `objects`, `activities`, `deliveries`, `app_events`, `processed_activities`), run at startup
- [X] T011 [P] Implement `src/Megafono/Domain/Ids.fs`: app user id validation `^[A-Za-z0-9_-]{1,64}$`, handle normalization `^[a-z0-9_]{1,30}$`, and IRI builders for actor, instance actor, inbox, shared inbox, collections, objects, activities and key ids (data-model.md, contracts/federation.md)
- [X] T012 [P] Implement `src/Megafono/Domain/Backoff.fs`: `least(6 h, 1 min × 2^(attempts−1))` with up to 10 % jitter from an injected random source, and the 48 h deadline check (FR-051)
- [X] T013 [P] Implement `src/Megafono/Infrastructure/KeyVault.fs`: RSA-2048 generation, SPKI/PKCS#8 PEM export and import, AES-256-GCM encryption `[keyVersion][nonce][tag][ciphertext]` with the actor id as associated data, decryption with current or previous master key (research R-11)
- [X] T014 [P] Implement `src/Megafono/Infrastructure/HttpSignatures.fs`: quote-aware `Signature` header parser, signing string builder over the declared headers (path + query for `(request-target)`), RSA-SHA256 sign and verify accepting `rsa-sha256` and `hs2019`, required-header check (`(request-target)`, `host`, `date`, plus `digest` on POST), ±12 h date check against an injected `TimeProvider`, `Digest: SHA-256=` compute and compare (case-insensitive prefix), and a `SignatureScheme` seam (research R-01)
- [X] T015 [P] Implement `src/Megafono/Infrastructure/SafeHttp.fs`: `SocketsHttpHandler` with a `ConnectCallback` that resolves DNS and rejects loopback, unspecified, private, link-local, CGNAT, ULA, multicast and IPv4-mapped forms; no auto-redirect (manual, max 3, re-validated and re-signed); `https` only unless the test switch; 10 s timeout; 1 MB capped body reads; helpers `signedGet` and `signedPost` that sign with a given key id and private key (research R-13, FR-020, FR-021)
- [X] T016 [P] Implement `src/Megafono/Infrastructure/Sanitizer.fs`: one shared `HtmlSanitizer` with cleared defaults and the allowlist from research R-14, links get `rel="nofollow noopener noreferrer ugc"`, plus a `toText` helper for names (FR-056)
- [X] T017 Implement `src/Megafono/Infrastructure/PortGuard.fs` and wire both Kestrel listeners in `src/Megafono/Program.fs`: middleware by `ctx.Connection.LocalPort` (`/internal/*` only on the internal port; only `/internal/*` and `/health` there; else 404), constant-time bearer check on `/internal/*`, `GET /health` checking the database (FR-061, FR-062, research R-10)
- [X] T018 Implement the instance actor bootstrap in `src/Megafono/Infrastructure/InstanceKey.fs`: create the single `instance_actor` row with a key pair at startup if missing, and expose its key id and decrypted signing key to `SafeHttp` (FR-022)
- [X] T019 Implement `src/Megafono/Infrastructure/AppClient.fs`: actor lookup by handle and by id (contracts/app-contract.md) with bearer token, 5 s timeout, answer validation (data-model.md rules), upsert into `local_actors` with key creation on first sight (FR-006), 5 min freshness and 24 h fallback to the stored profile when the app is unavailable
- [X] T020 Implement `src/Megafono/Infrastructure/RemoteActors.fs`: fetch a remote actor with a GET signed by the instance actor, require `id` = URL host and `publicKey.owner` = actor id, store in `remote_actors` (sanitized name), 24 h cache, key lookup by key id in both `#main-key` and `/main-key` forms, one refetch on verification failure (FR-011, FR-012), and WebFinger handle resolution `@user@host` → actor id
- [X] T021 Implement `src/Megafono/Workers/Dispatcher.fs`: a reusable claim loop over a queue table (`FOR UPDATE SKIP LOCKED`, lease of 60 s with `lease_token`, stale lease reclaim), wake-up by `LISTEN`/`NOTIFY` plus a 5 s poll, global and per-host `SemaphoreSlim` limits (defaults 32 and 2, FR-053), graceful drain on shutdown, an injected `TimeProvider`, and a test-only fault hook between the remote answer and the completion write
- [X] T022 Implement `src/Megafono/Workers/Deliveries.fs`: send a delivery's stored activity bytes with `signedPost` using the author's key, mark `delivered` on 2xx (`WHERE lease_token = @tok`), reschedule with Backoff on failure, `failed` after the deadline; plus a shared `enqueueDeliveries (tx) activityId inboxes` helper in `src/Megafono/Infrastructure/Outbox.fs` that inserts rows with `ON CONFLICT DO NOTHING` and `NOTIFY`s in the same transaction (FR-050)
- [X] T023 Implement `src/Megafono/Domain/Events.fs` and `src/Megafono/Workers/AppEvents.fs`: event envelope and types from contracts/app-contract.md, `enqueueEvent (tx)` that increments `local_actors.event_seq` in the same transaction, delivery to `POST /megafono/webhooks` with `Megafono-Signature: t=…,v1=<hex HMAC-SHA256 of "t.body">`, 10 s timeout, retried with Backoff capped at 6 h and never dropped (FR-055, FR-057)
- [X] T024 Implement `src/Megafono/Workers/Housekeeping.fs`: prune `processed_activities` older than 7 days hourly (FR-014)

### Foundational tests

- [X] T025 [P] Create integration fixtures in `tests/Megafono.Tests/Fixtures/`: `PostgresFixture.fs` (one container, a fresh database per test class), `MegafonoHost.fs` (real Kestrel on port 0 for both ports via `buildApp`, fake `TimeProvider`, access to the fault hook), `FakeApp.fs` (Kestrel host serving the actor lookup from an in-memory user list and recording webhook events, with switches to return 500 or time out), `StubRemote.fs` (a fake fediverse server with its own RSA key that signs requests to Megáfono, serves its actor document and records what its inbox receives)
- [X] T026 [P] Unit tests for `HttpSignatures` in `tests/Megafono.Tests/Unit/HttpSignaturesTests.fs`: header parsing with commas and quotes, signing string order, round trip, `hs2019`, missing required headers, date window edges, digest mismatch, and verification of real Mastodon and GoToSocial request samples saved in `tests/Megafono.Tests/Fixtures/payloads/` (real Mastodon/GoToSocial samples deferred to T085)
- [X] T027 [P] Unit tests for `KeyVault`, `Backoff`, `Ids` and `Sanitizer` in `tests/Megafono.Tests/Unit/` (AES-GCM associated data swap fails, previous-key decryption, schedule values and cap, id and handle validation, script/style/`javascript:` removed)
- [X] T028 [P] Unit tests for the SSRF address rules in `tests/Megafono.Tests/Unit/SafeHttpTests.fs`: every blocked range including IPv4-mapped IPv6, public addresses allowed, test switch behavior
- [X] T029 Integration tests in `tests/Megafono.Tests/Integration/HostTests.fs`: startup refuses a bad master key and the private-address switch in Production; `/internal/*` answers 404 on the federation port and 401 without the bearer; only `/internal/*` and `/health` answer on the internal port; schema bootstrap is idempotent across two starts (FR-061, FR-062)
- [X] T030 Integration tests in `tests/Megafono.Tests/Integration/QueueTests.fs`: app events are retried after the fake app returns 500 and are never dropped after 48 h; `seq` increases per user; a hard kill through the fault hook re-sends the same delivery bytes once; a clean stop drains in-flight deliveries without repeats; per-host limit 2 holds against a slow stub (FR-050, FR-053, FR-055, FR-057, SC-004)

**Checkpoint**: harness proven against GoToSocial and Mastodon; foundation ready.

---

## Phase 3: User Story 1 - Be found from any fediverse server (Priority: P1) 🎯 MVP

**Goal**: an app user is discoverable as `@handle@domain`, with the app's profile, from Mastodon and
GoToSocial; profile changes reach followers.

**Independent Test**: GTS (and Mastodon in Phase 8) search `@alex@megafono.test` and show the
profile the fake app returns.

### Tests for User Story 1 (REQUIRED) ⚠️

- [X] T031 [P] [US1] Integration tests in `tests/Megafono.Tests/Integration/WebFingerTests.fs`: known handle → JRD with `subject`, both aliases, `self` and `profile-page` links, `application/jrd+json`; unknown handle 404; other domain 404; uppercase handle and domain resolve; app unavailable uses the stored profile up to 24 h then 404 (FR-001, edge case "App is down")
- [X] T032 [P] [US1] Integration tests in `tests/Megafono.Tests/Integration/ActorTests.fs`: actor document fields from research R-02, `id` equals the request URL, `url` = profile URL, `publicKey.owner` = id, `manuallyApprovesFollowers` from `locked`, `type` Service/Group, key created once and reused, the private key never appears in any response; collections are stubs with `totalItems`; instance actor at `/ap/actor` is an Application; unknown id 404 (FR-003 to FR-006, FR-022)
- [X] T033 [P] [US1] Integration tests in `tests/Megafono.Tests/Integration/NodeInfoTests.fs`: discovery links 2.0 and 2.1; both documents carry the configured software name and version and the required keys (FR-002)
- [X] T034 [P] [US1] Integration test in `tests/Megafono.Tests/Integration/ProfileUpdateTests.fs`: with two seeded accepted inbound follows on `StubRemote` hosts, `PUT /internal/actors/{id}` after the fake app changes the name delivers one signed `Update{Person}` per host; no change → nothing sent (FR-007)
- [X] T035 [US1] Interop test in `tests/Megafono.Interop.Tests/DiscoveryTests.fs`: GTS search `@alex@megafono.test` with `resolve=true` returns Alex with the fake app's name, note and avatar; an unknown handle returns nothing (US1 scenarios 1–2)

### Implementation for User Story 1

- [X] T036 [P] [US1] Implement actor, instance actor and collection-stub builders in `src/Megafono/Domain/ActivityStreams.fs` with the `@context` and fields from research R-02, serialized with shared `JsonSerializerOptions`
- [X] T037 [US1] Implement `src/Megafono/Federation/WebFinger.fs`: `GET /.well-known/webfinger` per research R-03 using `AppClient` by handle
- [X] T038 [P] [US1] Implement `src/Megafono/Federation/NodeInfo.fs`: `/.well-known/nodeinfo`, `/nodeinfo/2.0`, `/nodeinfo/2.1` per research R-04 (user count = live local actors)
- [X] T039 [US1] Implement `src/Megafono/Federation/Actors.fs`: `GET /ap/actors/{id}` via `AppClient` by id, `GET /ap/actor`, and the `outbox`, `followers`, `following` stubs, all `application/activity+json` written with `WriteBytes` (contracts/federation.md)
- [X] T040 [US1] Implement `src/Megafono/Internal/Actors.fs`: `PUT /internal/actors/{id}` refreshes through `AppClient`, and if the profile changed creates an `Update{Person}` activity and enqueues it to the distinct inboxes of accepted followers via `Outbox` (FR-007, contracts/internal-api.md)
- [X] T041 [US1] Register the US1 endpoints in `src/Megafono/Program.fs` and make T031–T035 pass

**Checkpoint**: US1 works alone: GTS finds Alex.

---

## Phase 4: User Story 2 - Approve who follows me (Priority: P1)

**Goal**: remote follows are verified, held for approval on locked actors, accepted at once on
unlocked ones, and reported to the app; unfollows too.

**Independent Test**: GTS follows a locked user, the test approves through the internal API, and GTS
shows the follow accepted; repeat with reject and with an unlocked user.

### Tests for User Story 2 (REQUIRED) ⚠️

- [ ] T042 [P] [US2] Integration tests in `tests/Megafono.Tests/Integration/InboxSecurityTests.fs`, one per step of the inbox order in contracts/federation.md: 429 with `Retry-After` past the per-address limit; 413 for a declared and an actual body over 1 MB; 401 for no signature, stale `Date`, unsigned `digest`, digest mismatch, wrong key; key rotation accepted after one refetch; 429 past the per-verified-host limit; 400 for invalid JSON; a repeated activity id is 202 and ignored; an unknown type is 202 and dropped; none of the refused requests produces an app event (FR-010 to FR-016, SC-008)
- [ ] T043 [P] [US2] Integration tests in `tests/Megafono.Tests/Integration/InboundFollowTests.fs` with `StubRemote`: locked → `follow.requested` (`pending`), approve → Accept delivered embedding the full Follow and `follow.accepted`; reject → Reject delivered and `follow.rejected`; unlocked → Accept and `follow.accepted` without approval; Undo{Follow} (embedded and bare id) → `removed` and `follow.removed`; a repeated Follow keeps one row and answers with the latest id; a Follow whose `actor` is not the signer is ignored; a Follow for a user the app no longer knows is refused; approving twice is 200, approving after reject is 409 (FR-030 to FR-032, research R-05)
- [ ] T044 [P] [US2] Integration test in `tests/Megafono.Tests/Integration/RemoteActorUpdateTests.fs`: an `Update{Person}` signed by a followed remote actor refreshes `remote_actors` and emits `actor.updated`; an Update whose object is not the signer is ignored (FR-013)
- [ ] T045 [US2] Interop tests in `tests/Megafono.Interop.Tests/InboundFollowTests.fs`: GTS follows locked Alex → fake app receives `follow.requested` → approve → GTS relationship shows `following: true`; second GTS account → reject → `requested` cleared; unlocked user → accepted without approval; GTS unfollows → `follow.removed`; a tampered body sent to the inbox → 401 (US2 scenarios 1–6)

### Implementation for User Story 2

- [ ] T046 [P] [US2] Implement `src/Megafono/Infrastructure/RateLimits.fs`: global partitioned token-bucket limiter on inbox paths and signed GETs keyed by client address (trusting `X-Forwarded-For` only from `TrustedProxies`), and a per-verified-host limiter called after verification, both 600/min by default, 429 with `Retry-After` (FR-016, research R-12)
- [ ] T047 [P] [US2] Add Follow, Accept, Reject and Undo builders to `src/Megafono/Domain/ActivityStreams.fs` (Accept/Reject with their own id and the full embedded Follow, research R-05)
- [ ] T048 [US2] Implement the inbox pipeline in `src/Megafono/Federation/Inbox.fs`: `POST /ap/actors/{id}/inbox` and `POST /inbox`, capped raw read, steps 1–9 of contracts/federation.md, recording `processed_activities`, then dispatch; unknown types 202
- [ ] T049 [US2] Implement inbound follow handling in `src/Megafono/Federation/InboxFollows.fs`: Follow (target from `object`, not from the inbox), Undo{Follow}, follow state transitions from data-model.md, Accept enqueue for unlocked actors, events through `enqueueEvent`, all in one transaction per activity
- [ ] T050 [US2] Implement `Update{Person}` handling in `src/Megafono/Federation/InboxActors.fs` (refresh `remote_actors`, `actor.updated` for each local user related to that actor)
- [ ] T051 [US2] Implement approve and reject in `src/Megafono/Internal/Follows.fs`: `POST /internal/actors/{id}/follow-requests/{followId}/approve|reject` per contracts/internal-api.md, enqueuing Accept or Reject and the event in one transaction
- [ ] T052 [US2] Register the US2 endpoints and the rate limiter in `src/Megafono/Program.fs` and make T042–T045 pass

**Checkpoint**: US1 and US2 work: GTS can find and follow, with approval.

---

## Phase 5: User Story 3 - Publish to my followers, privately when I choose (Priority: P1)

**Goal**: one call publishes a Note or Article to public, unlisted or followers; followers' servers
receive it within a minute; followers-only objects stay private on fetch; edits and deletes follow.

**Independent Test**: with a GTS follower and a GTS non-follower, publish followers-only: the follower
sees it, the non-follower (and an unsigned request) gets 404 fetching it.

### Tests for User Story 3 (REQUIRED) ⚠️

- [X] T053 [P] [US3] Unit tests in `tests/Megafono.Tests/Unit/AudienceTests.fs`: `to`/`cc` per audience exactly as research R-06, identical on activity and object, followers URL equal to the actor's `followers`
- [X] T054 [P] [US3] Integration tests in `tests/Megafono.Tests/Integration/PublishTests.fs`: validation errors (audience, type, URLs, extension prefix, 256 KB); 201 shape; 10 retries with one `Idempotency-Key` → one object and one delivery per inbox (SC-005); same key with another body → 409; deliveries go to distinct shared inboxes (one per server) or personal inboxes; only accepted followers are recipients; extension data appears under its prefix with the namespace in `@context` (FR-040 to FR-043, FR-046)
- [X] T055 [P] [US3] Integration tests in `tests/Megafono.Tests/Integration/FetchPrivacyTests.fs` that try to break FR-045: followers-only object and its Create activity → 404 for unsigned, for a signed non-follower, for a pending follower, for a removed follower, for a remote instance actor, for a signature with a wrong key; 200 for an accepted follower; public and unlisted → 200 for anyone; never 403 (SC-002)
- [X] T056 [P] [US3] Integration tests in `tests/Megafono.Tests/Integration/UpdateDeleteTests.fs`: PUT sends `Update` with `updated` and the same audience; DELETE sends `Delete{Tombstone}` (never to Public for a followers-only object), repeat DELETE 202, then fetch → 410 with a Tombstone, PUT → 410 (FR-044)
- [X] T057 [P] [US3] Integration tests in `tests/Megafono.Tests/Integration/DeliveryTests.fs` with the fake clock: a down stub is retried on the backoff schedule, gives up after 48 h with `delivery.failed`; a host failing for 7 days becomes unreachable, new deliveries are `skipped` with `delivery.failed`, and one success clears it; `GET /internal/objects/{id}/deliveries` reports per-host status (FR-051, FR-052, FR-054)
- [ ] T058 [US3] Interop tests in `tests/Megafono.Interop.Tests/PublishTests.fs`: GTS follower sees a followers-only post in its home timeline within 60 s with private visibility; a second GTS account that does not follow gets 404 fetching the object URL with its own signed GET; public post appears as public and unlisted as unlisted; edit appears; delete removes it and the URL answers 410; a post with extension data displays normally (US3 scenarios 1–8)

### Implementation for User Story 3

- [X] T059 [P] [US3] Implement `src/Megafono/Domain/Audience.fs`: audience type and `to`/`cc` mapping (research R-06)
- [X] T060 [P] [US3] Add Note/Article, Create, Update and Delete{Tombstone} builders and extension embedding to `src/Megafono/Domain/ActivityStreams.fs` (research R-07)
- [X] T061 [US3] Implement `src/Megafono/Internal/Objects.fs`: publish (validation, idempotency with `request_hash`, one transaction: object, Create activity bytes, deliveries via `Outbox`, `NOTIFY`), update, delete and `GET /internal/objects/{id}/deliveries` per contracts/internal-api.md
- [X] T062 [US3] Implement `src/Megafono/Federation/Objects.fs`: `GET /ap/objects/{id}` and `GET /ap/activities/{id}` with the fetch rules of contracts/federation.md (verify the GET signature when present, rate-limited; accepted follower of the author now; else 404; deleted → 410)
- [X] T063 [US3] Add host reachability to `src/Megafono/Workers/Deliveries.fs`: maintain `remote_hosts`, mark unreachable after 7 days of failures, skip with `delivery.failed` while unreachable, clear on success, `delivery.failed` on deadline (FR-051, FR-052)
- [X] T064 [US3] Register the US3 endpoints in `src/Megafono/Program.fs` and make T053–T058 pass

**Checkpoint**: the three P1 stories work: find, follow with approval, publish privately.

---

## Phase 6: User Story 4 - Follow people on other servers (Priority: P2)

**Goal**: the app resolves a remote handle, makes a user follow or unfollow it, and lists followers
and following with a mutual flag.

**Independent Test**: resolve a GTS handle, follow it, the GTS account accepts and follows back; both
lists show `mutual: true`.

### Tests for User Story 4 (REQUIRED) ⚠️

- [ ] T065 [P] [US4] Integration tests in `tests/Megafono.Tests/Integration/RemoteActorLookupTests.fs`: handle and URL resolve to sanitized `{id, handle, name, iconUrl, profileUrl, type}`; unknown → 404; a handle whose WebFinger or actor points to a private address → 400 (FR-033, FR-021)
- [ ] T066 [P] [US4] Integration tests in `tests/Megafono.Tests/Integration/OutboundFollowTests.fs` with `StubRemote`: follow by handle and by URL → signed Follow delivered, `pending`; remote Accept → `accepted` and `follow.accepted` (`outbound`); remote Reject → `rejected` and `follow.rejected`; Reject after Accept → `rejected`; following again returns the current status; unfollow → `Undo{Follow}` delivered and `removed`; unfollowing someone not followed → 404 (FR-034)
- [ ] T067 [P] [US4] Integration tests in `tests/Megafono.Tests/Integration/FollowListTests.fs`: followers and following with status filter, `limit`, cursor paging, and `mutual` true only when both directions are accepted (FR-035)
- [ ] T068 [US4] Interop test in `tests/Megafono.Interop.Tests/OutboundFollowTests.fs`: Alex follows a locked GTS account by handle → GTS shows the request → GTS accepts → `follow.accepted`; GTS follows Alex back and the test approves → both lists show `mutual: true`; Alex unfollows → GTS no longer lists the follower (US4 scenarios 1–5)

### Implementation for User Story 4

- [ ] T069 [US4] Implement `src/Megafono/Internal/RemoteActors.fs`: `GET /internal/remote-actors?handle=|url=` through `RemoteActors` (contracts/internal-api.md)
- [ ] T070 [US4] Add follow, unfollow and the two lists to `src/Megafono/Internal/Follows.fs` (`POST /internal/actors/{id}/following`, `DELETE /internal/actors/{id}/following?actor=`, `GET …/followers`, `GET …/following`), enqueuing Follow and Undo activities through `Outbox`
- [ ] T071 [US4] Handle Accept{Follow} and Reject{Follow} for outbound follows in `src/Megafono/Federation/InboxFollows.fs` (match by follow id or by actor and target, research R-05), with events
- [ ] T072 [US4] Register the US4 endpoints in `src/Megafono/Program.fs` and make T065–T068 pass

**Checkpoint**: follows are two-way; the app can build a friends list.

---

## Phase 7: User Story 5 - Run it and integrate it in minutes (Priority: P2)

**Goal**: a self-hoster runs the example deployment; a developer sees a short integration.

**Independent Test**: start the compose deployment on a host with a domain and time until a Mastodon
account follows the sample app's test user (quickstart section 5).

### Tests for User Story 5 (REQUIRED) ⚠️

- [X] T073 [P] [US5] Integration tests in `tests/Megafono.Tests/Integration/GatewayTests.fs` running `Megafono.Gateway` in process against two stub upstreams: each Megáfono route goes to Megáfono and everything else (including `/.well-known/acme-challenge/x` and `/u/alex`) to the app; the original `Host` reaches Megáfono; path and body are unchanged byte for byte; a request signed through the gateway verifies at Megáfono; `X-Forwarded-For`/`-Proto` are set (FR-063)
- [X] T074 [P] [US5] Test in `tests/Megafono.Tests/Integration/SampleAppTests.fs`: the sample app answers the actor lookup by handle and id, verifies webhook signatures (rejects a bad or old one), and its profile page carries the `rel=alternate` link and `Link` header (FR-005, FR-064)
- [ ] T075 [US5] Interop test in `tests/Megafono.Interop.Tests/DeploymentShapeTests.fs`: build the Megáfono, gateway and sample app images, run them with Postgres on the federation network behind the `megafono.test` front, and check that GTS finds and follows the sample app's `test` user through the gateway and that the internal port is not reachable from the network (US5 scenarios 1, 3)

### Implementation for User Story 5

- [X] T076 [P] [US5] Create `src/Megafono.Gateway/` (`Megafono.Gateway.fsproj`, `Program.fs`, `appsettings.json`) on Yarp.ReverseProxy 2.3.0 with the routes, order, `RequestHeaderOriginalHost` and TLS settings from research R-14
- [X] T077 [P] [US5] Create `samples/SampleApp/` (Oxpecker): users in memory with a `test` user, the actor lookup, the webhook receiver with signature check, a profile page at `/u/{handle}` with the `rel=alternate` link and header, a page listing followers and pending requests with approve buttons, and a publish form; keep integration code in `samples/SampleApp/Megafono.fs` separate from the sample's UI
- [X] T078 [P] [US5] Create `deploy/Dockerfile.megafono`, `deploy/Dockerfile.gateway`, `deploy/Dockerfile.sample` (multi-stage, `linux/amd64` and `linux/arm64`)
- [X] T079 [US5] Create `deploy/compose.yaml` and `deploy/.env.example` (gateway on 443 with mounted cert, sample app, Megáfono with 8081 on an internal-only network, Postgres 17) following the vision's deployment section, with `/ap/*` routing
- [X] T080 [P] [US5] Create `scripts/count-integration-lines.sh` counting non-blank, non-comment lines of `samples/SampleApp/Megafono.fs` and failing above 99 (SC-007)
- [X] T081 [US5] Write `docs/operating.md`: setup from compose in under 10 minutes, every `Megafono__` setting, master key generation and backup apart from the database, gateway routing rules, and the plain statement that a delivered followers-only post can be shown to anyone by the receiving server (FR-065, Principle II)
- [ ] T082 [US5] Make T073–T075 pass and run quickstart section 6

**Checkpoint**: all five stories work and can be deployed.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [ ] T083 Mastodon suite in `tests/Megafono.Interop.Tests/Mastodon/` (Trait `Suite=mastodon`, `AUTHORIZED_FETCH=true`): the scenarios of T035, T045, T058 and T068 against Mastodon, plus the lock icon (visibility `private`) on the followers-only post and finding Alex by pasting the sample profile URL (SC-001)
- [ ] T084 [P] Load test in `tests/Megafono.Tests/Load/FanOutTests.fs` (Trait `Suite=load`): a stub fleet of 200 inbox hosts and 1,000 followers; with the process limited to 2 CPUs, every host receives the Create within 2 minutes (SC-003)
- [ ] T085 [P] Capture the real inbound payloads and headers seen in the interop runs into `tests/Megafono.Interop.Tests/Payloads/` and `tests/Megafono.Tests/Fixtures/payloads/` behind a test-mode logging switch, and add verification tests for them (Principle VI)
- [X] T086 [P] CI workflows in `.github/workflows/ci.yml` (build, `Megafono.Tests`, GoToSocial interop on every push) and `.github/workflows/nightly.yml` (nightly schedule and manual dispatch for `Suite=mastodon` and `Suite=load`)
- [X] T087 [P] Write `README.md`: what Megáfono is, the three-call integration, link to `docs/operating.md`, contracts and the privacy limit
- [ ] T088 Update `CLAUDE.md` (build and test commands, layout) and mark the decided open questions in `docs/vision.md` (per-actor keys, draft-cavage only, events only, no activity table) as decided by this feature
- [ ] T089 Run the whole of `specs/001-federation-core/quickstart.md` and record results; confirm every FR and SC of spec.md has a test that names it

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: after Setup; **blocks every story**. T006 and T007 come first: if the
  Mastodon spike (T007) fails, stop and report before building on the harness.
- **US1 (Phase 3)**: after Foundational. MVP.
- **US2 (Phase 4)**: after Foundational; its interop tests need US1 (GTS must resolve the actor
  before following).
- **US3 (Phase 5)**: after US2 for its end-to-end tests (recipients are accepted followers);
  integration tests can seed follows directly and start after Foundational.
- **US4 (Phase 6)**: after US2 (shares `Internal/Follows.fs` and `Federation/InboxFollows.fs`).
- **US5 (Phase 7)**: after US1–US3 for the sample app and deployment test; the gateway (T073, T076)
  can start after Foundational.
- **Polish (Phase 8)**: after the stories it covers.

### Within Each Story

Tests first and failing → domain builders → infrastructure → endpoints → registration in
`Program.fs` → tests green. Files shared within a story (`ActivityStreams.fs`, `Follows.fs`,
`InboxFollows.fs`, `Program.fs`) are edited by one task at a time.

### Parallel Opportunities

- Setup: T003, T004.
- Foundational: T011–T016 (separate files); T025–T028 once their modules exist.
- US1: T031–T034 together; T036 and T038 together.
- US2: T042–T044 together; T046 and T047 together.
- US3: T053–T057 together; T059 and T060 together.
- US4: T065–T067 together.
- US5: T073, T074, T076, T077, T078, T080 together.
- Polish: T084–T087 together.

---

## Parallel Example: User Story 3

```bash
# Tests first, in parallel (different files):
Task: "T054 Publish tests in tests/Megafono.Tests/Integration/PublishTests.fs"
Task: "T055 Fetch privacy tests in tests/Megafono.Tests/Integration/FetchPrivacyTests.fs"
Task: "T056 Update/delete tests in tests/Megafono.Tests/Integration/UpdateDeleteTests.fs"
Task: "T057 Delivery tests in tests/Megafono.Tests/Integration/DeliveryTests.fs"

# Domain pieces in parallel:
Task: "T059 Audience mapping in src/Megafono/Domain/Audience.fs"
Task: "T060 Note and activity builders in src/Megafono/Domain/ActivityStreams.fs"
```

---

## Implementation Strategy

### MVP first (User Story 1)

1. Phase 1 Setup.
2. Phase 2 Foundational, harness spikes first (T006, T007).
3. Phase 3 US1 → **stop and validate**: GTS finds Alex.

### Incremental delivery

1. + US2 → GTS follows with approval (federation is real from here).
2. + US3 → publishing with privacy: the three P1 stories make the first usable release for Frunchy.
3. + US4 → two-way follows and mutual flags.
4. + US5 → gateway, sample app, compose: ready for self-hosters.
5. Polish → Mastodon suite, load test, CI, docs.

### Delegation

Implementation tasks are delegated to Sonnet sub-agents one writing task group at a time; the
driving session reviews each diff against this file, the spec and the constitution, and runs the
tests before checking a task off.

---

## Notes

- [P] = different files and no dependency on an unfinished task.
- [Story] maps a task to a user story for traceability.
- Every test names the FR/SC it proves.
- Commit after each task or logical group, on `001-federation-core`.
- Stop at any checkpoint to validate a story on its own.
- If a spike or test contradicts research.md, update research.md first, then the code.
