# Contract changes: what the app receives (events)

**Contract version**: 1, additive to [001's app contract](../../001-federation-core/contracts/app-contract.md) and
[002's reply events](../../002-replies-threads/contracts/app-contract.md). Envelope, signature, retries, `seq` and
dedup rules are unchanged. No new call: the app follows remote accounts with the existing
`POST /internal/actors/{id}/following`, and a post reaches the app only once that follow is `accepted`.

## Who receives feed events

| Event | Sent for |
|---|---|
| `post.received` | each local user with an accepted follow of the author who does not block it; for a `direct` post, only those of them addressed in it |
| `post.updated` | each user who received the `post.received` and still follows the author, and does not block it |
| `post.deleted` | each user who received the `post.received`, also after an unfollow, unless they block the author |

One event per user, in that user's stream (`user` in the envelope): several local users who follow the same author
each get their own event with the same `object`. Deleted users get nothing. Nothing is sent for posts from accounts the
user's follow of is pending, rejected or removed.

**Replies into the app's own threads**: when the post replies to one of the app's objects or to a reply recorded in
one of its threads, the thread's author gets 002's `reply.received` / `reply.updated` / `object.deleted` and **not**
the feed events; the author's other local followers get the feed events. No user gets both for one post.

## `post.received`

```json
{
  "id": "evt_01JB…",
  "version": 1,
  "type": "post.received",
  "occurredAt": "2026-10-18T18:00:00Z",
  "seq": 913,
  "user": { "id": "u-1", "handle": "alex" },
  "actor": {
    "id": "https://gts.example/users/bob",
    "handle": "@bob@gts.example",
    "name": "Bob",
    "iconUrl": "https://gts.example/fileserver/…/avatar.png",
    "profileUrl": "https://gts.example/@bob"
  },
  "object": {
    "id": "https://gts.example/users/bob/statuses/01JB…",
    "url": "https://gts.example/@bob/statuses/01JB…",
    "type": "Note",
    "content": "<p>Started Frieren S2</p>",
    "summary": null,
    "sensitive": false,
    "language": "en",
    "published": "2026-10-18T17:59:58Z",
    "updated": null,
    "audience": "followers",
    "inReplyTo": null,
    "attachment": [
      { "type": "Image", "mediaType": "image/png", "url": "https://gts.example/fileserver/…png", "name": "alt text" }
    ]
  },
  "extensions": null
}
```

| Field | Rules |
|---|---|
| `object.content` | sanitized HTML (001 allowlist), as in `reply.received` |
| `object.summary` | the content warning as plain text, or `null`; with `sensitive` the app should hide the content behind it |
| `object.sensitive` | `true`, `false` (missing on the wire → `false`) |
| `object.audience` | `public`, `unlisted`, `followers` or `direct` (a `direct` post reaches only addressed followers) |
| `object.inReplyTo` | the id of the post this one answers, on any server, or `null`; replies to third parties are feed posts too |
| `object.attachment` | URLs only; Megáfono stores no media |
| `extensions` | `{ namespace, prefix, data }` when the post carries data under a namespace the app has published with (any earlier publish or update with `extensions`), matched by the full namespace whatever the prefix; else `null` |

Unlike `reply.received` there is no `parent` or `thread`: those describe the app's own threads.

## `post.updated`

Same payload as `post.received`, with `object.updated` set (the edit's own time, else when Megáfono received it).
`audience` and `published` stay those the post arrived with. Use `seq` and `updated` to ignore stale copies.

## `post.deleted`

```json
{
  "id": "evt_01JC…",
  "version": 1,
  "type": "post.deleted",
  "occurredAt": "2026-10-18T19:00:00Z",
  "seq": 920,
  "user": { "id": "u-1", "handle": "alex" },
  "actor": { "id": "https://gts.example/users/bob", "handle": "@bob@gts.example", "name": "Bob", "iconUrl": null, "profileUrl": "https://gts.example/@bob" },
  "object": { "id": "https://gts.example/users/bob/statuses/01JB…" },
  "extensions": null
}
```

Sent once per user when the author deletes a post the user was told about. Unknown or already deleted posts produce
nothing.

## Not sent

Boosts (`Announce`: acknowledged and dropped, a v1 non-goal), direct posts or mentions from accounts the user does not
follow (the mentions feature), posts published before the follow was accepted (no backfill), a followed account's own
deletion, posts other than Note or Article. Megáfono keeps no copy of feed posts and offers no timeline query: the app
stores what it shows.
