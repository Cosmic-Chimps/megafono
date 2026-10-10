# Contract: Megáfono internal API

**Contract version**: 1

Served only on the internal port (default 8081), which the gateway never routes (FR-061). Every
request needs `Authorization: Bearer <Megafono__App__Token>`; a missing or wrong token gets 401.
Any path outside `/internal/*` and `/health` on this port gets 404, and `/internal/*` on the
federation port gets 404 (research R-10).

`{id}` is the app's immutable user id. Errors are `{ "error": "<code>", "message": "<text>" }` with
codes `not_found`, `invalid_request`, `conflict`, `unauthorized`.

## Publishing

### Publish an object

```http
POST /internal/actors/{id}/objects
Idempotency-Key: watch-123-finished
```

```json
{
  "audience": "followers",
  "object": {
    "type": "Note",
    "content": "<p>Finished Frieren S1, 28 episodes</p>",
    "url": "https://app.example.com/u/alex/watch/123",
    "inReplyTo": null,
    "summary": null,
    "sensitive": false,
    "attachment": [{ "type": "Image", "mediaType": "image/jpeg", "url": "https://app.example.com/img/frieren.jpg", "name": "Frieren cover" }]
  },
  "extensions": {
    "namespace": "https://app.example.com/ns#",
    "prefix": "app",
    "data": { "watch": { "malId": 52991, "episodes": 28 } }
  }
}
```

- `audience`: `public | unlisted | followers` (others are later features → 400).
- `Idempotency-Key`: optional but recommended, at most 200 characters, scoped per user (FR-042).
- **201** `{ "id": "<objectId>", "objectUrl": "https://{domain}/ap/objects/<objectId>", "deliveriesUrl": "/internal/objects/<objectId>/deliveries" }`.
- **200** with the same body when the key was already used with the same request.
- **409** `conflict` when the key was used with a different request.
- **404** when the app's actor lookup does not know `{id}`.

The object, its Create activity and one delivery per destination inbox are stored in one
transaction before the answer (FR-050).

### Update an object

```http
PUT /internal/objects/{objectId}
```

Body: the `object` part of the publish request (and optional `extensions`). The audience can't
change. **200** with the publish response shape; **404** unknown; **410** `gone` if deleted.
Sends `Update` with `updated` set to the same audience.

### Delete an object

```http
DELETE /internal/objects/{objectId}
```

**202** (deliveries queued); repeat calls also answer 202. Sends `Delete` with a Tombstone; the
object URL then answers 410.

### Delivery status

```http
GET /internal/objects/{objectId}/deliveries
```

```json
{
  "objectId": "…",
  "activities": [
    {
      "activityId": "https://{domain}/ap/activities/…",
      "type": "Create",
      "deliveries": [
        { "host": "mastodon.social", "inbox": "https://mastodon.social/inbox", "status": "delivered", "attempts": 1, "lastError": null, "nextAttemptAt": null }
      ]
    }
  ]
}
```

`status`: `pending | sending | delivered | failed | skipped` (FR-054).

## Profile

```http
PUT /internal/actors/{id}
```

No body. Megáfono looks the user up again through the actor lookup and sends `Update` of the actor
to every follower's server if anything changed (FR-007). **202**; **404** unknown user.

## Follows

### Answer a follow request

```http
POST /internal/actors/{id}/follow-requests/{followId}/approve
POST /internal/actors/{id}/follow-requests/{followId}/reject
```

`followId` comes from the `follow.requested` event. **200** `{ "followId": "…", "status": "accepted" | "rejected" }`.
Repeating the same answer returns 200 again; answering a follow that is no longer pending with the
other answer gets 409.

### Follow and unfollow a remote actor

```http
POST /internal/actors/{id}/following
```

```json
{ "target": "@bob@mastodon.social" }
```

`target` is a handle (`@user@host` or `user@host`) or an actor URL. **202**
`{ "followId": "…", "status": "pending", "actor": { "id", "handle", "name", "iconUrl", "profileUrl" } }`;
following someone already followed returns their current status. **404** if the target can't be
resolved; **400** for a blocked address (FR-021).

```http
DELETE /internal/actors/{id}/following?actor={remoteActorId}
```

**202**; sends `Undo{Follow}`. **404** if not followed.

### Lists

```http
GET /internal/actors/{id}/followers?status=accepted&limit=50&after={cursor}
GET /internal/actors/{id}/following?status=accepted&limit=50&after={cursor}
```

`status` defaults to all; `limit` at most 200.

```json
{
  "items": [
    { "followId": "…", "status": "accepted", "mutual": true, "since": "2026-10-10T18:00:00Z",
      "actor": { "id": "https://mastodon.social/users/bob", "handle": "@bob@mastodon.social", "name": "Bob", "iconUrl": "…", "profileUrl": "…" } }
  ],
  "next": "<cursor or null>"
}
```

`mutual` is true only when both directions are accepted (FR-035).

## Remote actors

```http
GET /internal/remote-actors?handle=@bob@mastodon.social
GET /internal/remote-actors?url=https://mastodon.social/users/bob
```

**200** `{ "id", "handle", "name", "iconUrl", "profileUrl", "type" }` (sanitized); **404** when
not found; **400** for a blocked address (FR-033, FR-021).

## Health

```http
GET /health
```

**200** `{ "status": "ok", "database": "ok" }`, or **503** when the database is unreachable
(FR-062). Not authenticated.
