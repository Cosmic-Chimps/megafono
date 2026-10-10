# Data Model: Federation Core

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-10

Megáfono's own Postgres database. The schema is created idempotently at startup (constitution
Principle VII); there is no migration framework. Times are `timestamptz` in UTC. Ids that leave
Megáfono (actor, object, activity, event) are permanent (Principle IV): rows that carry them are
never deleted, only marked.

## Identity

### `instance_actor` (exactly one row)

| Column | Type | Rules |
|---|---|---|
| `id` | `smallint` PK | always `1` (`CHECK (id = 1)`) |
| `public_key_pem` | `text` | SPKI PEM |
| `enc_private_key` | `bytea` | AES-GCM blob `[keyVersion][nonce][tag][ciphertext]`, AD = actor id |
| `created_at` | `timestamptz` | |

Actor id `https://{domain}/ap/actor`, key id `…/ap/actor#main-key` (FR-022). Created at first start.

### `local_actors`

| Column | Type | Rules |
|---|---|---|
| `app_user_id` | `text` PK | the app's immutable id; `^[A-Za-z0-9_-]{1,64}$`; never reused (FR-004) |
| `handle` | `text` | lowercase; `UNIQUE` among rows with `deleted_at IS NULL` |
| `actor_type` | `text` | `Person`, `Service`, `Group` |
| `locked` | `boolean` | from the app; drives `manuallyApprovesFollowers` |
| `name`, `summary` | `text` | from the app (summary is HTML the app owns) |
| `icon_url`, `profile_url` | `text` | from the app; `profile_url` becomes the actor's `url` |
| `profile_checked_at` | `timestamptz` | last successful app lookup; refreshed after 5 min, used as fallback for 24 h when the app is down |
| `public_key_pem` | `text` | created on first lookup (FR-006) |
| `enc_private_key` | `bytea` | as for the instance actor, AD = actor id |
| `event_seq` | `bigint` | last event sequence number issued for this user (FR-057) |
| `created_at` | `timestamptz` | becomes the actor's `published` |
| `deleted_at` | `timestamptz` null | reserved for actor deletion (later feature); the row is kept |

Actor id = `https://{domain}/ap/actors/{app_user_id}` (FR-005). Handle changes are out of scope; a
lookup returning a different handle for a known id updates `handle` only.

## Remote side

### `remote_actors`

| Column | Type | Rules |
|---|---|---|
| `id` | `text` PK | the actor's `id` IRI |
| `host` | `text` | lowercase host of `id` |
| `handle` | `text` null | `preferredUsername@host` once known |
| `actor_type` | `text` | |
| `inbox` | `text` | required |
| `shared_inbox` | `text` null | only trusted if on the actor's host or a subdomain (research R-02) |
| `followers_url` | `text` null | |
| `name`, `icon_url`, `profile_url` | `text` null | sanitized before reaching the app |
| `key_id` | `text` | the `publicKey.id` |
| `public_key_pem` | `text` | |
| `fetched_at` | `timestamptz` | cached for 24 h; refetched once on verification failure (FR-012) |

Index: `UNIQUE (key_id)`.

### `remote_hosts`

| Column | Type | Rules |
|---|---|---|
| `host` | `text` PK | |
| `first_failure_at` | `timestamptz` null | set on the first failure after a success |
| `last_success_at` | `timestamptz` null | |
| `unreachable_since` | `timestamptz` null | set when every delivery failed for 7 days; cleared on any success (FR-052) |

## Relationships

### `follows`

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | |
| `direction` | `text` | `inbound` (remote follows local) or `outbound` (local follows remote) |
| `local_actor` | `text` FK → `local_actors` | |
| `remote_actor` | `text` FK → `remote_actors` | |
| `follow_activity_id` | `text` | the Follow's `id`; for inbound, the latest one received |
| `status` | `text` | `pending`, `accepted`, `rejected`, `removed` |
| `created_at`, `updated_at` | `timestamptz` | |

Constraint: `UNIQUE (direction, local_actor, remote_actor)`; a repeated Follow updates the row
(edge case "duplicate follow").

**Mutual** (FR-035): the inbound and outbound rows for the same pair are both `accepted`.

**Inbound transitions** (FR-030 to FR-032):

```text
(none) --Follow, locked--> pending --approve--> accepted
                                   --reject---> rejected
(none) --Follow, unlocked--> accepted
accepted --Undo Follow--> removed
rejected/removed --Follow--> pending or accepted (a new request)
```

**Outbound transitions** (FR-034):

```text
(none) --app follows--> pending --remote Accept--> accepted
                                --remote Reject--> rejected
accepted --remote Reject--> rejected
pending/accepted --app unfollows (Undo sent)--> removed
```

Every transition that the app should know about writes an `app_events` row in the same
transaction.

## Content

### `objects`

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | object IRI = `https://{domain}/ap/objects/{id}` |
| `local_actor` | `text` FK | the author |
| `audience` | `text` | `public`, `unlisted`, `followers` |
| `object_type` | `text` | `Note`, `Article` |
| `document` | `jsonb` | the full ActivityPub object as served (FR-041) |
| `idempotency_key` | `text` null | `UNIQUE (local_actor, idempotency_key)` (FR-042) |
| `request_hash` | `bytea` null | SHA-256 of the publish request, to reject a reused key with a different body (409) |
| `state` | `text` | `live`, `deleted` |
| `created_at`, `updated_at`, `deleted_at` | `timestamptz` | |

A deleted object answers 410 (FR-044); the row and its id are kept.

### `activities`

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | IRI = `https://{domain}/ap/activities/{id}` |
| `local_actor` | `text` FK | |
| `object` | `uuid` FK null | for Create, Update, Delete |
| `activity_type` | `text` | `Create`, `Update`, `Delete`, `Accept`, `Reject`, `Follow`, `Undo` |
| `audience` | `text` | copied from the object, or `direct` for follow traffic (not fetchable) |
| `body` | `bytea` | the exact bytes delivered, so a retry is byte-identical (FR-050) |
| `created_at` | `timestamptz` | |

`Create`/`Update` activities are fetchable with the object's audience rules; others are not.

## Queues

### `deliveries`

| Column | Type | Rules |
|---|---|---|
| `id` | `uuid` PK | |
| `activity` | `uuid` FK | |
| `inbox_url` | `text` | shared or personal inbox |
| `host` | `text` | lowercase host of `inbox_url` |
| `status` | `text` | `pending`, `sending`, `delivered`, `failed`, `skipped` |
| `attempts` | `int` | |
| `next_attempt_at` | `timestamptz` | |
| `lease_until`, `lease_token` | `timestamptz`, `uuid` null | claim lease; completion checks the token |
| `deadline_at` | `timestamptz` | `created_at + 48 h` (FR-051) |
| `last_error` | `text` null | status code or exception summary |
| `delivered_at` | `timestamptz` null | |
| `created_at` | `timestamptz` | |

Constraint: `UNIQUE (activity, inbox_url)`. Index on `next_attempt_at` where status is `pending`
or `sending`.

```text
pending --claimed--> sending --2xx--> delivered
                             --error, before deadline--> pending (next_attempt_at = backoff)
                             --error, after deadline---> failed  (+ delivery.failed event)
pending --host unreachable--> skipped (+ delivery.failed event)
sending --lease expired (crash)--> claimable again (same body)
```

Backoff: `least(6 h, 1 min × 2^(attempts − 1))` plus up to 10 % jitter.

### `app_events`

| Column | Type | Rules |
|---|---|---|
| `id` | `text` PK | `evt_` + ULID |
| `local_actor` | `text` FK | |
| `seq` | `bigint` | from `local_actors.event_seq`, incremented in the same transaction (FR-057) |
| `event_type` | `text` | see [contracts/app-contract.md](contracts/app-contract.md) |
| `payload` | `jsonb` | the event body sent to the app |
| `status` | `text` | `pending`, `sending`, `delivered` (never dropped, FR-055) |
| `attempts`, `next_attempt_at`, `lease_until`, `lease_token`, `last_error` | | as for deliveries, backoff capped at 6 h, no deadline |
| `created_at`, `delivered_at` | `timestamptz` | |

Constraint: `UNIQUE (local_actor, seq)`.

### `processed_activities`

| Column | Type | Rules |
|---|---|---|
| `activity_id` | `text` PK | inbound activity `id` |
| `received_at` | `timestamptz` | rows older than 7 days are pruned (FR-014) |

## Validation rules (summary)

- Publish: `audience` in `public | unlisted | followers`; `type` in `Note | Article`; `content`
  non-empty; attachment and link URLs absolute `https`; extensions need a namespace IRI and a
  prefix matching `^[a-z][a-z0-9]{0,15}$`; request body at most 256 KB.
- Actor lookup answers from the app: `id` matches the id pattern; `handle` matches
  `^[a-z0-9_]{1,30}$` after lowercasing; `profileUrl` absolute `https` (or `http` under the test
  switch); `type` in `Person | Service | Group` (default `Person`).
- Remote documents: `id` host must match the URL fetched; `publicKey.owner` must equal the actor id.
