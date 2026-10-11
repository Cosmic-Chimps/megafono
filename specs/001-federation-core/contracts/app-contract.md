# Contract: What the app implements

**Contract version**: 1 (constitution Principle I: within a major version, changes are additive)

The app implements two things: an **actor lookup** and an **event receiver**. Megáfono calls them on
the app's private address (`Megafono__App__BaseUrl`), never through the gateway. JSON is camelCase.
Every call from Megáfono carries `Authorization: Bearer <Megafono__App__Token>`; the app should
reject other callers.

## Actor lookup

Two forms, same answer (FR-060). Megáfono uses the handle form for WebFinger and the id form for
actor fetches and profile refreshes.

```http
GET /megafono/actors?handle={handle}
GET /megafono/actors/{id}
```

**200**:

```json
{
  "id": "3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10",
  "handle": "alex",
  "name": "Alex",
  "summary": "<p>Anime and coffee</p>",
  "iconUrl": "https://app.example.com/img/alex.png",
  "profileUrl": "https://app.example.com/u/alex",
  "type": "Person",
  "locked": true
}
```

| Field | Required | Rules |
|---|---|---|
| `id` | yes | immutable, never reassigned, `^[A-Za-z0-9_-]{1,64}$` (FR-004) |
| `handle` | yes | `^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$` (case-insensitive): 1 to 30 characters, dashes only inside, no dots; the `@handle@domain` people type |
| `name` | yes | display name |
| `summary` | no | HTML the app trusts |
| `iconUrl` | no | absolute URL hosted by the app |
| `profileUrl` | yes | the human profile page; becomes the actor's `url` |
| `type` | no | `Person` (default), `Service`, `Group` |
| `locked` | yes | `true` = every follow waits for approval |

**404**: the user does not exist. Any other status or a timeout (5 s) counts as "app unavailable":
Megáfono uses the last known profile for up to 24 hours, then answers 404 itself.

Megáfono caches a positive answer for 5 minutes (FR-001).

**Recommended on the profile page** (FR-005): so a pasted profile link works on Mastodon,

```html
<link rel="alternate" type="application/activity+json" href="https://app.example.com/ap/actors/{id}">
```

and the same as a header: `Link: <https://app.example.com/ap/actors/{id}>; rel="alternate"; type="application/activity+json"`.

## Event receiver

```http
POST /megafono/webhooks
Content-Type: application/json
Megafono-Signature: t=1791662400,v1=5d41402abc4b2a76b9719d911017c592...
```

`v1` is the lowercase hex HMAC-SHA256 of `"{t}.{raw body}"` with `Megafono__App__WebhookSecret`.
The app should reject a signature older than 5 minutes and compare in constant time.

**Any 2xx** = handled. Anything else, or no answer within 10 s, is retried with backoff (1 min,
doubling, capped at 6 h) **until it succeeds; events are never dropped** (FR-055). Events are **not
ordered** (FR-057): use `seq` and the status fields to ignore stale events, and `id` to ignore
repeats.

### Envelope

```json
{
  "id": "evt_01J9ZQ7F3K8M2N4P6R8T0V2X4Z",
  "version": 1,
  "type": "follow.requested",
  "occurredAt": "2026-10-10T18:00:00Z",
  "seq": 42,
  "user": { "id": "3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10", "handle": "alex" },
  "actor": {
    "id": "https://mastodon.social/users/bob",
    "handle": "@bob@mastodon.social",
    "name": "Bob",
    "iconUrl": "https://files.mastodon.social/bob.png",
    "profileUrl": "https://mastodon.social/@bob"
  },
  "follow": { "id": "6a1e…", "direction": "inbound", "status": "pending" },
  "extensions": null
}
```

| Field | Rules |
|---|---|
| `id` | unique per event; the same on every retry |
| `version` | contract version, `1` |
| `seq` | increases per `user.id`; an event with a lower `seq` than one already applied for the same relationship is stale |
| `user` | the local user concerned |
| `actor` | the remote actor involved; `name` sanitized to text |
| `follow` | on `follow.*` events: the relationship's id, `direction` (`inbound` = they follow the user, `outbound` = the user follows them) and its status **at send time** |
| `extensions` | always `null` in this feature |

### Event types in this feature

| Type | When | Extra fields |
|---|---|---|
| `follow.requested` | a remote actor asks to follow a locked user | `follow` (`inbound`, `pending`) |
| `follow.accepted` | an inbound follow is accepted (auto or by the app), or a remote actor accepts the user's follow | `follow` |
| `follow.rejected` | the app rejects an inbound follow, or a remote actor rejects or revokes the user's follow | `follow` |
| `follow.removed` | a remote follower unfollows | `follow` (`inbound`, `removed`) |
| `actor.updated` | a remote actor the user follows or is followed by changes its profile | `actor` only |
| `delivery.failed` | a delivery gave up after 48 h, or its server is unreachable | `object: {id}`, `delivery: {host, attempts, lastError}` |

Later features add types (`reply.received`, `like.received`, …); receivers must accept and
acknowledge unknown types.
