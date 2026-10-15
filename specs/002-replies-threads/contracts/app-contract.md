# Contract changes: what the app receives (events)

**Contract version**: 1 (additive to [001's app contract](../../001-federation-core/contracts/app-contract.md)).
Envelope, signature, retries, `seq` and dedup rules are unchanged. **Draft to agree with the app side before
implementation.**

## Who receives reply events

The **author of the thread's root** (the `user` in the envelope), whatever the depth of the reply. For Frunchy
that is the series Group, so all episode comments arrive under one user id. The parent is in the payload, so the
app can also notify the person replied to.

## `reply.received`

```json
{
  "id": "evt_01JA…",
  "version": 1,
  "type": "reply.received",
  "occurredAt": "2026-10-12T18:00:00Z",
  "seq": 812,
  "user": { "id": "series-52991", "handle": "frieren" },
  "actor": {
    "id": "https://mastodon.social/users/bob",
    "handle": "@bob@mastodon.social",
    "name": "Bob",
    "iconUrl": "https://files.mastodon.social/bob.png",
    "profileUrl": "https://mastodon.social/@bob"
  },
  "object": {
    "id": "https://mastodon.social/users/bob/statuses/1131",
    "url": "https://mastodon.social/@bob/1131",
    "type": "Note",
    "content": "<p>that ending!</p>",
    "summary": "Spoilers: S2 E5",
    "sensitive": true,
    "language": "en",
    "published": "2026-10-12T17:59:58Z",
    "updated": null,
    "audience": "public",
    "inReplyTo": "https://frunchy.example/ap/objects/6f1c…",
    "parent": { "local": true, "objectId": "6f1c…", "userId": "series-52991" },
    "thread": { "objectId": "6f1c…", "id": "https://frunchy.example/ap/objects/6f1c…" },
    "attachment": [
      { "type": "Image", "mediaType": "image/png", "url": "https://files.mastodon.social/…png", "name": "alt text" }
    ]
  },
  "extensions": null
}
```

| Field | Rules |
|---|---|
| `object.content` | sanitized HTML (001 allowlist) |
| `object.summary` | the content warning as plain text, or `null`; with `sensitive` the app should hide the content behind it |
| `object.sensitive` | `true`, `false` (missing on the wire → `false`) |
| `object.audience` | `public`, `unlisted`, `followers` or `direct`; only `public`/`unlisted` replies are shown to the fediverse in the thread, the app decides what to display |
| `object.parent` | `local: true` with `objectId` (Megáfono's id from the publish response) and `userId` when the parent is the app's own post or reply; `local: false` when it is a remote reply (`inReplyTo` is then that reply's id) |
| `object.thread` | the root the app published (`objectId` as returned by publish) |
| `object.attachment` | URLs only; Megáfono stores no media |
| `extensions` | as in the vision: data under the app's own namespace when the reply carries it, else `null` |

## `reply.updated`

Same payload as `reply.received`, with `object.updated` set. Sent when the reply's author edits it. Use `seq` and
`updated` to ignore stale copies.

## `object.deleted`

```json
{
  "type": "object.deleted",
  "user": { "id": "series-52991", "handle": "frieren" },
  "actor": { "id": "https://mastodon.social/users/bob", "handle": "@bob@mastodon.social", "name": "Bob", "iconUrl": null, "profileUrl": "https://mastodon.social/@bob" },
  "object": {
    "id": "https://mastodon.social/users/bob/statuses/1131",
    "thread": { "objectId": "6f1c…", "id": "https://frunchy.example/ap/objects/6f1c…" }
  }
}
```

Sent once, when the author deletes a reply Megáfono recorded. Unknown or already deleted replies produce nothing.

## Not sent in this feature

Likes, mentions outside a thread, boosts, posts from followed accounts (a later feature), replies to posts the app
did not publish.
