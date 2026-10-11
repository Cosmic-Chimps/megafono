# Contract changes: what the app calls

Additive to [001's internal API](../../001-federation-core/contracts/internal-api.md). **Draft to agree with the
app side before implementation.**

## Publish a reply

Same call as a post: `POST /internal/actors/{id}/objects`, with `object.inReplyTo` set.

```http
POST /internal/actors/3f6c2a9e-…/objects
Idempotency-Key: comment-8812
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

## Edits and deletes of the app's replies

Unchanged: `PUT /internal/objects/{objectId}` and `DELETE /internal/objects/{objectId}` work on replies too; the
Update and Delete go to the same recipients as the reply.
