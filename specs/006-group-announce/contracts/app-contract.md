# Contract changes: the actor lookup and the feed events

**Contract version**: 1, additive to [001's app contract](../../001-federation-core/contracts/app-contract.md),
[002's reply events](../../002-replies-threads/contracts/app-contract.md) and
[005's feed events](../../005-followed-posts/contracts/app-contract.md). Envelope, signature, retries, `seq` and dedup
rules are unchanged. No new call and no new event type.

## The actor lookup: `announcesReplies`

`GET /megafono/actors?handle=` and `GET /megafono/actors/{id}` may answer one more optional field:

```json
{ "id": "series-52991", "handle": "frieren", "name": "Frieren", "profileUrl": "https://frunchy.example/series/frieren",
  "type": "Group", "locked": false, "announcesReplies": true }
```

| Field | Rules |
|---|---|
| `announcesReplies` | optional boolean; absent or `null` is `false`. `true` only with `"type": "Group"`: on a `Person` or `Service` the answer is invalid (the lookup fails and is logged, like a bad `type`). Any other JSON type is invalid. |

While it is `true`, the Group announces to its followers every public or unlisted reply in its threads (from other
servers or published through the app by the app's other users), the edit of such a reply and its deletion (see
[federation.md](federation.md)). Turning it off stops new announces; earlier ones stay. It is read when the profile is
read (the 5-minute cache, `PUT /internal/actors/{id}`); it does not change the actor document.

## Feed events: `announcedBy`

`post.received`, `post.updated` and `post.deleted` gain one top-level field, **`announcedBy`**: the remote `Group`
whose announce brought the post to this user, in the same shape as `actor`, or `null` when the post came straight from
an account the user follows (every 005 event so far).

```json
{
  "id": "evt_01JC…",
  "version": 1,
  "type": "post.received",
  "occurredAt": "2026-11-04T18:00:00Z",
  "seq": 931,
  "user": { "id": "series-52991", "handle": "frieren" },
  "actor": {
    "id": "https://otherfrunchy.io/ap/actors/u-dave",
    "handle": "@dave@otherfrunchy.io",
    "name": "Dave",
    "iconUrl": null,
    "profileUrl": "https://otherfrunchy.io/u/dave"
  },
  "announcedBy": {
    "id": "https://otherfrunchy.io/ap/actors/series-52991",
    "handle": "@frieren@otherfrunchy.io",
    "name": "Frieren",
    "iconUrl": "https://otherfrunchy.io/img/frieren.png",
    "profileUrl": "https://otherfrunchy.io/series/frieren"
  },
  "object": {
    "id": "https://otherfrunchy.io/ap/objects/7c2e…",
    "url": "https://otherfrunchy.io/series/frieren/episodes/5#c-12",
    "type": "Note",
    "content": "<p>that ending!</p>",
    "summary": null,
    "sensitive": false,
    "language": null,
    "published": "2026-11-04T17:59:58Z",
    "updated": null,
    "audience": "public",
    "inReplyTo": "https://otherfrunchy.io/ap/objects/5a90…",
    "attachment": []
  },
  "extensions": { "namespace": "https://frunchy.example/ns#", "prefix": "frunchy", "data": { "series": 52991, "episode": 5 } }
}
```

| Field | Rules |
|---|---|
| `actor` | the post's author, verified from the post's own server (never the Group) |
| `announcedBy` | the Group that announced it, or `null`; present on all three feed events |
| `object` | exactly 005's shape and sanitizing; `inReplyTo` is the thread post the comment answers (on the Group's server) |
| `extensions` | as 005: data under a namespace the app has published with, matched by the full namespace, else `null` |

### Who receives announced feed events

| Event | Sent for |
|---|---|
| `post.received` | each local user with an accepted follow of the announcing Group who blocks neither the Group nor the author (nor their domains), except the author of a thread of the app's the post replies into (they get 002's reply events) |
| `post.updated` | each user who received the post through this Group, still follows the Group, blocks neither, and does not follow the author directly (those get `post.updated` from the author's own edit, as in 005) |
| `post.deleted` | each user who received the post through this Group and blocks neither the Group nor the author |

One event per user and change, whatever the number of announces or roads it arrived by: a user who follows both the
author and the Group is told once, by whichever arrived first, and once per edit. The author's own `Update` and
`Delete` still reach every user as 005 says.

### Not passed on

Announces from actors that are not a `Group`, from a Group the user's follow of is not `accepted`, of objects other
than Note or Article, of followers-only or direct objects, of the app's own objects, of objects whose server does not
serve them or attributes them to another server's actor, and announces of a `Delete` (deletions arrive as the Group's
`Undo` of its announce). Megáfono keeps no copy of an announced post.
