# Contract changes: what the app calls

Additive to [001's internal API](../../001-federation-core/contracts/internal-api.md). **Draft to agree with the
app side before implementation.**

## Publish a reply

Same call as a post: `POST /internal/actors/{id}/objects`, with `object.inReplyTo` set.

```http
POST /internal/actors/3f6c2a9e-…/objects
Idempotency-Key: comment:8812
```

```json
{
  "audience": "public",
  "object": {
    "type": "Note",
    "content": "<p>Fern carried this episode</p>",
    "url": "https://frunchy.example/anime/GRZX/episode/ABC#comment-8812",
    "inReplyTo": "https://frunchy.example/ap/objects/6f1c…",
    "summary": "Spoilers: S2 E5",
    "sensitive": true
  }
}
```

- `inReplyTo`: the `objectUrl` of one of the app's objects (from a publish response) **or** the `object.id` of a
  reply received in a `reply.received` event. Anything else → **400** `invalid_request` ("unknown inReplyTo").
  A deleted parent → **410** `gone`.
- `audience`: `public` or `unlisted` for replies; `followers`/`direct` → **400**.
- The replying actor is any of the app's actors (a user replying to a Group's thread is the normal case).
- **201** response as in 001, plus `"thread": { "objectId": "<root>", "id": "<root object url>" }`.
- Megáfono adds the addressing, the Mention of the parent's author when remote, `context`/`conversation` and the
  reply policy (research R-23, R-24). Deliveries go to the replier's followers and the parent's author.

## Threads (root posts)

Unchanged: a thread is any object the app publishes (for Frunchy, the series Group's episode post, with an
idempotency key per episode, e.g. `thread:{seriesId}:{episodeKey}`). Its `objectUrl` is what the app puts in the
episode page's `<link rel="alternate">` and `Link` header.

## Content warnings

Unchanged fields (`summary`, `sensitive`) on every publish and update; for a spoiler set both.

## Editing and deleting the app's replies (and any published object)

Already in 001; they work on replies unchanged. Use them for a user editing or deleting a comment and for an
operator removing one.

```http
PUT /internal/objects/{objectId}
```

Body: the `object` part of the publish request (`content`, `summary`, `sensitive`, …; `inReplyTo`, `type` and the
audience can't change → 400). Sends `Update{Note}` with `updated` set to the same recipients as the reply (the
replier's followers, the parent's remote author, mentioned actors). **200** with the publish response; **404**
unknown; **410** deleted.

```http
DELETE /internal/objects/{objectId}
```

Sends `Delete{Tombstone}` to the same recipients; the object then answers 410 to those who could see it, leaves the
thread's `replies` collection, and repeat calls answer **202**. Copies already on other servers may stay (a
protocol limit; the docs say so).

## Idempotency keys

Opaque to Megáfono and scoped per actor; any string up to 200 characters. Frunchy's convention: `thread:{seriesId}:{episodeKey}`
for threads and `comment:{id}` for comments.

## Actor ids and handles for non-user actors

The actor lookup may answer any mix of people and other accounts (for Frunchy: `users.id` uuids and
`series-{crunchyrollSeriesId}`). Megáfono requires only:

- **ids**: `^[A-Za-z0-9_-]{1,64}$`, never changed and never reassigned (constitution Principle IV);
- **handles**: one live handle maps to one id at a time (a collision is refused), lowercase, within the handle
  rule of the app contract (letters, digits, underscore today; inner dashes once the pending handle fix lands;
  at most 30 characters). Keeping user and series handles apart is the app's job.
- **Group**: `type: "Group"` is the only Group-specific field. Use `locked: false` so follows are accepted at once;
  `name`, `summary`, `iconUrl` and `profileUrl` as for users (the series page as `profileUrl`).
