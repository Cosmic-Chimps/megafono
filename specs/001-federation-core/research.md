# Research: Federation Core

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-10

Three research passes fed this file: the interop test harness, the Mastodon and GoToSocial wire
details, and the .NET building blocks. Nothing here was run against live servers yet; items marked
**unverified** are confirmed by the spikes in R-15 before the code depends on them.

Sources are listed per decision. Mastodon links point at `main` on GitHub, GoToSocial (GTS) links at
`main` on Codeberg or docs.gotosocial.org.

## Protocol

### R-01 HTTP Signatures (draft-cavage)

- **Decision**: hand-written module. Outbound: `algorithm="rsa-sha256"`, signed headers
  `(request-target) host date digest` on POST and `(request-target) host date` on GET,
  `Digest: SHA-256=<base64>`, RFC 7231 `Date`, `Content-Type: application/activity+json`.
  Inbound: accept `rsa-sha256` and `hs2019` (RSA-SHA256 when the key is RSA); verify the header
  list the sender declares, but require `(request-target)`, `host`, `date` and, on POST, `digest`
  to be in it; `Date` within ±12 h; Digest prefix matched case-insensitively, SHA-256 only, checked
  against the raw bytes before parsing. Parse the `Signature` header with a quote-aware tokenizer.
  Verification sits behind a `SignatureScheme` seam so RFC 9421 can be added later.
- **Rationale**: matches what both servers send. Mastodon signs draft-cavage first and only falls
  back to RFC 9421 after a 400/401 (4.7 behavior on `main`), so a cavage-only receiver keeps
  working. GTS always signs `hs2019` with RSA-SHA256 and requires signed GETs on every
  ActivityPub endpoint.
- **Gotchas**: keyIds differ: Mastodon `…/users/x#main-key`, GTS `…/users/x/main-key` (a path that
  returns a partial actor stub; fetch it signed). Strip the fragment, fetch, use the `publicKey`
  whose `id` equals the keyId, take the actor from `publicKey.owner`. GTS signs with the query
  string first; use path plus query in `(request-target)`.
- **Alternatives**: a NuGet draft-cavage library (none found that is maintained; not surveyed in
  depth); RFC 9421 now (deferred by the spec).
- **Sources**: [Mastodon signed_request.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/signed_request.rb),
  [request.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/request.rb),
  [docs: security](https://docs.joinmastodon.org/spec/security/),
  [GTS http signatures](https://docs.gotosocial.org/en/latest/federation/http_signatures/).

### R-02 Actor documents and the instance actor

- **Decision**: actor at `https://{domain}/ap/actors/{appUserId}` with `@context`
  `[activitystreams, security/v1, {toot, schema, manuallyApprovesFollowers, discoverable,
  indexable}]`, `id`, `type`, `preferredUsername`, `name`, `summary`, `url` (app profile), `inbox`,
  `outbox`, `followers`, `following`, `endpoints.sharedInbox`, `manuallyApprovesFollowers`,
  `discoverable`, `indexable`, `published`, `icon`, `webfinger` (FEP-2c59), and one
  `publicKey {id: <actor>#main-key, owner, publicKeyPem}`. `outbox`, `followers` and `following`
  are served as `OrderedCollection` stubs with `totalItems` and no items. A separate **instance
  actor** (`Application`, `preferredUsername` = domain) at `https://{domain}/ap/actor` signs every
  outbound GET not made on behalf of a user (handle resolution, key refetch).
- **Rationale**: Mastodon requires `@context`, a known `type`, `preferredUsername`, `inbox`,
  `publicKey`, and that the fetched `id` equals the URL. GTS requires exactly one `publicKey` whose
  `owner` equals the actor id, and reads `followers` to classify followers-only posts. Both servers
  (GTS always, Mastodon under authorized fetch) reject unsigned GETs, so a key is needed even when
  no user is involved; living under `/ap/*` keeps the gateway rule unchanged.
- **Gotcha**: the `id` in the body must equal the request URL byte for byte (no trailing slash, no
  case drift).
- **Sources**: [actor_serializer.rb](https://github.com/mastodon/mastodon/blob/main/app/serializers/activitypub/actor_serializer.rb),
  [fetch_remote_actor_service.rb](https://github.com/mastodon/mastodon/blob/main/app/services/activitypub/fetch_remote_actor_service.rb),
  [instance actor](https://github.com/mastodon/mastodon/blob/main/app/controllers/instance_actors_controller.rb),
  [GTS astointernal.go](https://codeberg.org/superseriousbusiness/gotosocial/src/branch/main/internal/typeutils/astointernal.go),
  [GTS actors](https://docs.gotosocial.org/en/latest/federation/actors/).

### R-03 WebFinger and profile-URL lookups

- **Decision**: answer `acct:{handle}@{domain}` (domain matched case-insensitively, other domains
  404) with `application/jrd+json`: `subject`, `aliases: [profileUrl, actorId]`, links
  `rel=self type=application/activity+json href=actorId` and
  `rel=http://webfinger.net/rel/profile-page type=text/html href=profileUrl`. The sample app emits
  both `<link rel="alternate" type="application/activity+json" href=actorId>` and a matching
  `Link:` header on profile pages.
- **Rationale**: Mastodon re-resolves `preferredUsername@host` and requires the same actor, so
  `preferredUsername` must equal the handle. Mastodon's `FetchResourceService` follows one
  `Link`/`<link rel=alternate>` hop for pasted profile URLs. GTS does not appear to follow alternates
  (inferred from source), so on GTS people paste the handle.
- **Sources**: [webfinger.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/webfinger.rb),
  [webfinger_serializer.rb](https://github.com/mastodon/mastodon/blob/main/app/serializers/webfinger_serializer.rb),
  [fetch_resource_service.rb](https://github.com/mastodon/mastodon/blob/main/app/services/fetch_resource_service.rb).

### R-04 NodeInfo

- **Decision**: `/.well-known/nodeinfo` links both schema 2.0 and 2.1; `/nodeinfo/2.0` and
  `/nodeinfo/2.1` serve `version`, `software{name, version}`, `protocols: ["activitypub"]`,
  `services{inbound: [], outbound: []}`, `openRegistrations: false`, `usage{users{total}}`,
  `metadata: {}`. The configured software name is validated against `^[a-z0-9-]+$` at startup.
- **Rationale**: required keys of the 2.1 schema; Mastodon advertises only 2.0, so some consumers
  look for it. Neither server needs NodeInfo to federate (unverified), so it is not on any critical
  path.
- **Source**: [NodeInfo 2.1 schema](https://github.com/jhass/nodeinfo/blob/main/schemas/2.1/schema.json).

### R-05 Follow, Accept, Reject, Undo

- **Decision**: outbound Follow `{id, type: Follow, actor, object: <target id>}`. Accept and Reject
  carry their own unique `id`, `actor` = our actor, and embed the **full** Follow
  (`{id, type, actor, object}`) as `object`; after a repeated Follow, answer with the latest follow
  id. Inbound Undo accepts an embedded Follow or a bare id. Inbound Follow is taken only if
  `follow.actor` equals the signer and `follow.object` is one of our actors, regardless of which
  inbox it arrived at. A Reject for an already accepted outgoing follow ends it.
- **Rationale**: GTS answers 400 to an Accept or Reject without `id` and handles a bare IRI only for
  its own follow paths, so embedding is the form both accept. Mastodon matches by uri or by
  actor/target and treats Reject-after-Accept as an unfollow.
- **Sources**: [follow serializers](https://github.com/mastodon/mastodon/blob/main/app/serializers/activitypub/accept_follow_serializer.rb),
  [accept.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/activity/accept.rb),
  [reject.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/activity/reject.rb),
  [GTS accept.go](https://codeberg.org/superseriousbusiness/gotosocial/src/branch/main/internal/federation/federatingdb/accept.go).

### R-06 Addressing, visibility and private fetches

- **Decision**: one addressing table, set identically on the activity and the object:

  | Audience | `to` | `cc` |
  |---|---|---|
  | public | `[as:Public]` | `[followers]` |
  | unlisted | `[followers]` | `[as:Public]` |
  | followers | `[followers]` | `[]` |

  The `followers` value is exactly the actor's `followers` URL. A followers-only object (and its
  activity) is served only to a signed request whose key owner is an **accepted follower of the
  object's author at fetch time**; everything else, including requests signed by a remote
  instance actor, gets 404.
- **Rationale**: Mastodon derives visibility as public if Public is in `to`, unlisted if in `cc`,
  private if `to` contains the followers collection, else direct; GTS does the same and treats
  followers in `cc` as direct. Mastodon signs fetches of a status with a local account that follows
  the author (first local follower), and its own status controller answers 404 when not permitted.
- **Sources**: [status_parser.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/parser/status_parser.rb),
  [tag_manager.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/tag_manager.rb),
  [statuses_controller.rb](https://github.com/mastodon/mastodon/blob/main/app/controllers/statuses_controller.rb),
  GTS `internal/ap/extract.go` (`ExtractVisibility`).

### R-07 Notes, Update, Delete and extensions

- **Decision**: a Note or Article carries `id`, `type`, `attributedTo`, `published`, `content`,
  `url`, `to`, `cc`, optional `summary`, `sensitive`, `inReplyTo`, `tag`, and
  `attachment[{type: Document, mediaType, url, name}]`. Edits send `Update{object}` with `updated`
  set and the same addressing. Deletes send `Delete{object: {id, type: Tombstone}}` to the same
  audience (never Public for a followers-only object), then the id answers 410. Extensions: the
  namespace IRI is added to `@context` as `{prefix: namespace}` and the data under
  `prefix:key` properties; no `@graph`, `@included` or `@reverse`.
- **Rationale**: Mastodon applies edits only with `updated`, accepts Delete with an id or a
  Tombstone, and reads specific keys without full JSON-LD processing, so unknown properties are
  ignored. GTS handling of unknown properties is **unverified** (covered by an interop test).
- **Sources**: [update.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/activity/update.rb),
  [delete.rb](https://github.com/mastodon/mastodon/blob/main/app/lib/activitypub/activity/delete.rb),
  [GTS posts](https://docs.gotosocial.org/en/latest/federation/posts/).

### R-08 Shared inboxes

- **Decision**: at publish, recipients are the accepted followers of the author; group them by
  `endpoints.sharedInbox` when advertised, else by personal inbox, one delivery row per distinct
  URL. GTS actors advertise no shared inbox, so they fall back to personal inboxes automatically.
- **Rationale**: Mastodon accepts a followers-only Create on its shared inbox only if it has a local
  follower of the author, and fans it out to those followers only. Since deliveries only go to
  servers with an accepted follower, this always holds.
- **Source**: Mastodon `app/lib/activitypub/activity/create.rb`.

## .NET building blocks

### R-09 HTTP layer on Oxpecker 2.1.1

- **Decision**: Oxpecker 2.1.1 endpoints with explicit verbs. Inbox handlers read the raw body
  through a helper that rejects a declared `Content-Length` over 1 MB, sets
  `IHttpMaxRequestBodySizeFeature`, and copies into a buffer, mapping Kestrel's 413
  `BadHttpRequestException`. Responses use `ctx.SetContentType` + `ctx.WriteBytes` with our own
  `JsonSerializerOptions` (`application/activity+json`, `application/jrd+json`), never `WriteJson`.
- **Rationale**: 2.1.1 has no raw-body reader and no per-endpoint metadata (`configureEndpoint`), so
  per-endpoint limits and policies live in handlers or middleware.
- **Source**: Frunchy's `oxpecker` skill and the 2.1.1 package XML docs.

### R-10 One process, two ports

- **Decision**: one `WebApplication`, Kestrel listening on the federation and internal ports
  (8080, 8081 by default). A first middleware compares `ctx.Connection.LocalPort`: `/internal/*`
  only on the internal port, and nothing but `/internal/*` and `/health` on it; anything else is
  404. The internal API also checks the bearer token in constant time
  (`CryptographicOperations.FixedTimeEquals`).
- **Rationale**: `RequireHost("*:8081")` matches the Host header, which a caller controls, and needs
  endpoint metadata. The local port of the socket can't be spoofed.
- **Alternative**: two `WebApplication`s in one process (rejected: double DI and config wiring).

### R-11 Keys at rest

- **Decision**: RSA-2048 per actor (`RSA.Create 2048`, PKCS#8 / SPKI PEM). Private keys are
  encrypted with AES-256-GCM under a master key from configuration (`Megafono__MasterKey`, base64 of
  32 bytes): stored as `[keyVersion:1][nonce:12][tag:16][ciphertext]`, with the actor id as
  associated data so a row can't be moved to another actor. Megáfono refuses to start without a
  valid master key. A previous key can be configured for rotation (re-encryption command is a later
  feature).
- **Rationale**: ASP.NET Data Protection would need its own key ring storage (EF-based or a custom
  repository) and an opaque format tied to application names, which breaks silently on migration.
- **Risk**: losing the master key loses every actor. The docs say to back it up apart from the
  database.

### R-12 Rate limiting (refines spec FR-016)

- **Decision**: two layers. (1) Before verification, the ASP.NET Core `GlobalLimiter` (a
  `PartitionedRateLimiter` token bucket) on inbox paths, keyed by client address taken from
  `X-Forwarded-For` only when the request comes from a configured trusted proxy. (2) After
  verification, a per-host limiter keyed by the verified signer's host, before any further work.
  Both default to 600 a minute, both configurable, both answer 429 with `Retry-After`. Remote key
  fetches are counted against the claimed host.
- **Rationale**: the keyId host is attacker-controlled until verified, so keying only on it lets an
  attacker exhaust a legitimate server's budget. The per-address layer bounds the number of
  partitions.
- **Source**: `Microsoft.AspNetCore.RateLimiting` (shared framework).

### R-13 SSRF-safe outbound client

- **Decision**: one `SocketsHttpHandler` whose `ConnectCallback` resolves DNS itself, rejects
  loopback, unspecified, private (10/8, 172.16/12, 192.168/16), link-local (169.254/16, fe80::/10),
  CGNAT (100.64/10), ULA (fc00::/7), multicast and IPv4-mapped forms of these, and connects to a
  validated address. No automatic redirects (manual, at most 3, each re-validated and re-signed);
  `https` only; 10 s timeout; responses read with `ResponseHeadersRead` into a 1 MB capped buffer.
  Tests inject an allow rule (`Megafono__Federation__AllowPrivateAddresses=true`), logged loudly and
  refused when `ASPNETCORE_ENVIRONMENT=Production`. Plain `http` is allowed only by the same test
  switch.
- **Rationale**: connecting to the validated IP closes DNS rebinding; TLS still validates the
  original host name.

### R-14 Sanitizing, queues, gateway

- **HTML**: `HtmlSanitizer` 9.2.1039 (AngleSharp 1.7.2), one shared instance with cleared defaults
  and an allowlist of `p br a span strong em b i ul ol li blockquote code pre`, attributes `href rel
  class` (class on `a` and `span` only), schemes `http https mailto`, and
  `rel="nofollow noopener noreferrer ugc"` added to links. Applied to `content`, `summary` and
  `name` of anything sent to the app. A hand-rolled stripper fails the security bar.
- **Queues**: Postgres tables `deliveries` and `app_events`, one row per (activity, inbox) with
  `UNIQUE (activity_id, inbox_url)`. One in-process dispatcher claims due rows with
  `FOR UPDATE SKIP LOCKED` and a lease (`lease_until`, `lease_token`), hands them to a pool bounded
  globally (32) and per host (2) by semaphores, completes with `WHERE lease_token = @tok`. Rows are
  written in the same transaction as the object, before the app gets its answer. Wake-up through
  `LISTEN`/`NOTIFY` sent in that transaction, plus a poll every few seconds. Backoff
  `least(6 h, 1 min × 2^(attempts−1))` with jitter. Shutdown drains: stop claiming, let in-flight
  requests finish and record them.
- **What "no duplicate" can mean (refines spec SC-004/FR-050)**: exactly-once over HTTP is
  impossible: a POST can succeed and the process die before the row is marked delivered. So: a
  clean restart or deploy never loses and never duplicates; after a hard crash, a delivery in that
  window may be sent once more with the **same activity id and identical bytes**, which receivers
  deduplicate; two different activities, or concurrent sends of one, never happen. The test kills
  the process between the response and the write through a fault hook.
- **Gateway**: a small `Megafono.Gateway` project on `Yarp.ReverseProxy` 2.3.0 (stable Microsoft
  YARP images are unverified, and owning the project keeps config path and TLS under our control).
  Routes in order: exact `/.well-known/webfinger`, exact `/.well-known/nodeinfo`, `/nodeinfo/{**}`,
  `/ap/{**}`, `/inbox` to Megáfono; catch-all to the app. `RequestHeaderOriginalHost: true` on every
  route, no path or body transforms, default `X-Forwarded-*`, TLS from a configured certificate.
  Megáfono trusts forwarded headers only from configured proxies.
- **Prior art**: Frunchy's `Workers/ImportProcessor.fs` (`SKIP LOCKED` with stale-claim reset);
  River, graphile-worker, Que, Oban use the same claim-and-lease shape (from memory).

## Test harness

### R-15 Real-server interop harness

- **Decision**:
  - TLS everywhere. The test process generates a root CA and one leaf certificate per domain
    (`megafono.test`, `gts.test`, `mastodon.test`; reserved `.test` TLD).
  - One Docker network (Testcontainers). Each domain is a Caddy container with that network alias,
    terminating TLS with its leaf and reverse-proxying with the Host preserved.
  - Megáfono runs **in the test process on real Kestrel** (port 0, both ports), exposed to the
    containers with `TestcontainersSettings.ExposeHostPortsAsync`; the `megafono.test` Caddy proxies
    to `host.testcontainers.internal:<port>`. The test also hosts the fake app (actor lookup and
    event receiver) on Kestrel.
  - GoToSocial (pinned tag, SQLite, `GTS_HOST=gts.test`, CA via `SSL_CERT_FILE`,
    `http-client.allow-ips` for the Docker subnet, rate limiting off) runs on **every** test run.
  - Mastodon (pinned 4.x tag; web, sidekiq, Postgres, Redis; production mode,
    `ALLOWED_PRIVATE_ADDRESSES` = the three private ranges (the subnet is not fixed),
    `AUTHORIZED_FETCH=true`, CA bundle via `SSL_CERT_FILE`) runs in a separate suite marked `Trait("Suite", "mastodon")`, scheduled and
    before releases.
  - Both servers are driven through their Mastodon-compatible client API (search with `resolve`,
    follow, home timeline, status) with one shared client. Provisioning: GTS
    `gotosocial admin account create` plus a scripted sign-in for a token; Mastodon
    `tootctl accounts create --confirmed --approve` plus a `rails runner` script for a token.
  - Assertions poll with a timeout (30–60 s). Real inbound payloads are captured as fixtures.
- **Rationale**: plain HTTP would test neither https ids nor Mastodon at all (it forces HTTPS in
  production). TestServer has no socket, so containers can't reach it. Fedify's Mastodon lane
  skipped signatures to get going, which would defeat Principle VI.
- **Alternatives**: Megáfono as a container on the network (kept as the fallback if host-port
  exposure is flaky; costs an image build per run); HTTP mode (rejected above).
- **Spikes before the code depends on them** (first tasks of the feature):
  1. Mastodon in production mode trusting the test CA through `SSL_CERT_FILE`: **verified (T007,
     2026-10-10)**, `ghcr.io/mastodon/mastodon:v4.7.3` (native arm64, Ruby 4.0.6, user `mastodon`,
     entrypoint `tini`, no default command), the latest stable at the time (4.6.9, 4.5.19 and 4.4.26 are
     the older maintained lines). `SSL_CERT_FILE=/certs/ca.pem`, a bundle holding **only the test CA**,
     is enough: Mastodon (Ruby/OpenSSL, http.rb) then accepts the `megafono.test` leaf, and none of the
     harness traffic needs the system roots. Without it Mastodon never reaches the host (the request
     dies in the TLS handshake and nothing is logged). Setup that worked, in `MastodonFixture`:
     - **Topology**: Postgres (`postgres:17-alpine`, alias `db`) and Redis (`redis:7-alpine`, alias
       `redis`), web and sidekiq from the same image, a Caddy front aliased `mastodon.test` proxying to
       `web:3000` (Caddy sets `X-Forwarded-Proto: https` itself; Mastodon's `force_ssl` needs it), and
       the same `megafono.test` Caddy and host-port exposure as for GTS (both fixtures run in one
       process without trouble).
     - **Env** (web and sidekiq): `RAILS_ENV=production`, `LOCAL_DOMAIN=mastodon.test`,
       `SECRET_KEY_BASE` (64 random bytes, hex), `ACTIVE_RECORD_ENCRYPTION_DETERMINISTIC_KEY`,
       `ACTIVE_RECORD_ENCRYPTION_KEY_DERIVATION_SALT`, `ACTIVE_RECORD_ENCRYPTION_PRIMARY_KEY` (random
       32-char strings, no need for `rails db:encryption:init`), `DB_HOST/PORT/USER/NAME/PASS`,
       `REDIS_HOST/PORT`, `ES_ENABLED=false`, `SINGLE_USER_MODE=false`, `AUTHORIZED_FETCH=true`,
       `ALLOWED_PRIVATE_ADDRESSES=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16`,
       **`EMAIL_DOMAIN_ALLOWLIST=mastodon.test`**, `SSL_CERT_FILE`. `OTP_SECRET` is not needed, and
       **VAPID keys are not needed** (the instance boots and serves without them).
     - **Gotcha**: `tootctl accounts create` fails with `email unreachable` for a `.test` address,
       because production validates the e-mail domain's MX record; `EMAIL_DOMAIN_ALLOWLIST` skips it.
     - **Migration step**: the web container's command is `sh -c "bundle exec rails db:setup &&
       exec bundle exec puma -C config/puma.rb"` (`POSTGRES_DB` already creates the database; `db:setup`
       loads the schema, 615 migrations, in about 12 s). Sidekiq starts after web is healthy.
     - **Readiness**: web is ready when `GET https://mastodon.test/health` answers 200 through the front
       (`/health` is exempt from `force_ssl`); sidekiq is ready when
       `/opt/mastodon/tmp/sidekiq_process_has_started_and_will_begin_processing_jobs` exists
       (`MASTODON_SIDEKIQ_READY_FILENAME`), checked with `test -f`; Postgres with
       `pg_isready -h 127.0.0.1` (the init server is socket-only), Redis with `redis-cli ping`.
     - **Account and token**: `docker exec <web> bin/tootctl accounts create alice --email
       alice@mastodon.test --confirmed --approve` (prints a generated password, which the harness does not
       need), then `bin/rails runner /tmp/token.rb` (script copied in with `WithResourceMapping`) that
       does `Doorkeeper::Application.create!(name:, redirect_uri: 'urn:ietf:wg:oauth:2.0:oob', scopes:
       'read write follow')` and `Doorkeeper::AccessToken.create!(application:, resource_owner_id:,
       scopes: 'read write follow', expires_in: nil)` and prints `TOKEN=<token>` (parsed from stdout, which
       also carries Sidekiq log lines). `GET /api/v1/accounts/verify_credentials` with it returns
       `username: alice`, `url: https://mastodon.test/@alice` (the actor id is
       `https://mastodon.test/ap/users/<numeric id>` in 4.7).
     - **Dialing**: a search `GET /api/v2/search?q=@nobody@megafono.test&resolve=true&type=accounts` makes
       Mastodon send, unsigned, `GET https://megafono.test/.well-known/webfinger?resource=acct:nobody@megafono.test`
       and `GET /.well-known/host-meta`, both with `Host: megafono.test` and `User-Agent: Mastodon/4.7.3
       (http.rb/5.3.1; +https://mastodon.test/)`, so the host must answer host-meta too (404 is fine).
       Without `ALLOWED_PRIVATE_ADDRESSES` the search fails at once with
       `{"error":"Mastodon::PrivateNetworkAddressError"}` and nothing arrives.
  2. A non-interactive GTS user token: **verified (T006, 2026-10-10)**. Flow, all over the Caddy
     front with one cookie jar and no redirect following (`HarnessTests`, `GoToSocialFixture.ObtainToken`):
     1. `gotosocial admin account create --username alice --email alice@gts.test --password ...` via
        `docker exec` (`/gotosocial/gotosocial admin account create`). The server must be fully up
        first (see timings); the account is usable at once, no `account confirm` needed.
     2. `POST /api/v1/apps` (form: `client_name`, `redirect_uris=urn:ietf:wg:oauth:2.0:oob`,
        `scopes=read write follow`) returns `client_id` and `client_secret`.
     3. `GET /oauth/authorize?client_id&redirect_uri&response_type=code&scope` answers `303
        /auth/sign_in` and sets the `gotosocial-gts.test` cookie (`Secure`, `Domain=gts.test`, 2 min),
        so the cookie jar only works over https.
     4. `POST /auth/sign_in` with form `username` (the **email**) and `password` answers `302
        /oauth/authorize`.
     5. `GET /oauth/authorize` (same query) is a page with one field-less form (`POST
        /oauth/authorize`, button "Allow"); the pending request lives in the session cookie.
     6. `POST /oauth/authorize` with an empty form answers `302 /oauth/oob?code=<code>`.
     7. `POST /oauth/token` (`grant_type=authorization_code`, `client_id`, `client_secret`,
        `redirect_uri`, `code`) returns `access_token`; `GET /api/v1/accounts/verify_credentials` with
        it returns the user. Steps 2 to 7 take about 0.1 s.
     Gotchas: GTS answers **418 to any request without a `User-Agent`** (`HttpClient` sends none, so
     set one); it answers **503 while starting** and logs "listening on" *before* it serves, so
     readiness is a 200 from `GET /api/v1/instance`, not the log line.
  3. Host-port exposure on Docker Desktop for macOS: **verified on macOS Docker Desktop 29.8
     (arm64)**; Linux CI **still unverified** (same mechanism, expected to work).
     `TestcontainersSettings.ExposeHostPortsAsync(port)` (before any container is created) starts a
     `testcontainers/sshd:1.4.0` sidecar (2 s warm) and containers created afterwards resolve
     `host.testcontainers.internal` to it, including containers on a custom network. A real Kestrel
     host listening on `0.0.0.0:<free port>` in the test process was reached from the
     `megafono.test` Caddy (`reverse_proxy host.testcontainers.internal:<port>`, Host preserved by
     default) and from a curl container (`curlimages/curl:8.22.0`, `--cacert`) as
     `https://megafono.test/probe.json`; the host saw `Host: megafono.test`. The same request without
     the CA fails the TLS handshake. The sidecar and the containers are removed by Ryuk at the end.
     Certificates, Caddyfiles and the CA are copied in with `WithResourceMapping` (no bind mounts, so no
     host-path sharing or file-permission issues on macOS).
  4. GTS dialing private addresses only with `allow-ips`: **verified**. Without
     `GTS_HTTP_CLIENT_ALLOW_IPS`, a search for `@nobody@megafono.test` fails in GTS with `dial tcp
     172.22.0.4:443: dial within blocked / reserved IP range` and nothing reaches the host; with
     `GTS_HTTP_CLIENT_ALLOW_IPS=10.0.0.0/8,172.16.0.0/12,192.168.0.0/16` (comma-separated CIDRs, env
     form of `http-client.allow-ips`) GTS makes a signed `GET
     https://megafono.test/.well-known/webfinger?resource=acct:nobody@megafono.test` (keyId
     `https://gts.test/users/alice/main-key`, `User-Agent` `gotosocial/...`), trusting the test CA
     through `SSL_CERT_FILE=/certs/ca.pem`. The Docker network's subnet is not fixed (it was
     `172.22.0.0/16` here), so the harness allows the three private ranges instead of the exact subnet.
- **Pinned images (T006)**: `docker.io/superseriousbusiness/gotosocial:0.22.2` (reports
  `0.22.2+git-fb2bf13`; entrypoint `/gotosocial/gotosocial server start`, user 1000), `docker.io/library/caddy:2.11.7-alpine`
  (`auto_https off`, `admin off`, explicit `tls <leaf> <key>`, ready on "serving initial configuration"),
  `docker.io/curlimages/curl:8.22.0` (helper). GTS env: `GTS_HOST=gts.test`, `GTS_ACCOUNT_DOMAIN=gts.test`,
  `GTS_PROTOCOL=https`, `GTS_DB_TYPE=sqlite`, `GTS_DB_ADDRESS=/gotosocial/storage/sqlite.db`,
  `GTS_LETSENCRYPT_ENABLED=false`, `GTS_ADVANCED_RATE_LIMIT_REQUESTS=0` (0 disables rate limiting),
  `GTS_TRUSTED_PROXIES`, `GTS_HTTP_CLIENT_ALLOW_IPS`, `SSL_CERT_FILE`. The test CA and leaves are ECDSA P-256
  made with `CertificateRequest`; the test process trusts only that CA (custom chain trust, name checked),
  and reaches the fronts through the mapped port 443 with a `ConnectCallback`, keeping the URL host for SNI.
- **Timings (T006, macOS Docker Desktop, images cached)**: the GTS harness smoke suite takes about 18 s:
  expose host port 2.1 s, start GTS container 1.6 s, both Caddy fronts 0.9 s (parallel), GTS ready
  (migrations, WASM compile) 5.6 s, token 0.1 s. A cold run adds the image pulls.
- **Mastodon timings and memory (T007, macOS Docker Desktop, images cached)**: the Mastodon smoke suite
  takes about 35 s end to end: Postgres, Redis and both Caddys 3.1 s, web healthy (`db:setup` plus boot)
  10.2 s, sidekiq ready 6.3 s, account plus token 8.2 s (two Rails boots), host-port exposure 2.1 s. The
  first run (cold) was about 60 s. Idle memory: web about 425 MiB, sidekiq about 327 MiB, Postgres about 57
  MiB, Redis about 11 MiB, around 0.8 GiB in total; the image is 868 MB. Far below the 6–12 min first
  estimated, so the Mastodon suite can run on every CI build if desired (the `Suite=mastodon` trait still
  lets it be filtered out).
- **Sources**: [GTS config](https://docs.gotosocial.org/en/latest/configuration/general/),
  [GTS CLI](https://docs.gotosocial.org/en/latest/admin/cli/),
  [Mastodon config](https://docs.joinmastodon.org/admin/config/),
  [Fedify PR #598](https://github.com/fedify-dev/fedify/pull/598),
  [Testcontainers networking](https://dotnet.testcontainers.org/api/create_docker_network).

## Package versions (NuGet, 2026-10-10)

| Package | Version | Use |
|---|---|---|
| Oxpecker | 2.1.1 | HTTP (pinned, same as Frunchy) |
| Npgsql | 10.0.3 | Postgres |
| HtmlSanitizer | 9.2.1039 | Sanitizing remote HTML |
| Yarp.ReverseProxy | 2.3.0 | Gateway project |
| xunit.v3 | 4.0.1 | Tests (Microsoft.Testing.Platform) |
| Testcontainers.PostgreSql | 4.15.0 | Tests (and base Testcontainers for other containers) |
