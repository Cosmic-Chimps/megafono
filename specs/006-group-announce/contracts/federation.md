# Contract changes: federation

Additive to [001's](../../001-federation-core/contracts/federation.md), [002's](../../002-replies-threads/contracts/federation.md)
and [005's](../../005-followed-posts/contracts/federation.md) federation contracts. The verification pipeline is
unchanged; every verified activity is still answered 202 whether acted on or not.

## Outbound: what an announcing Group sends

All three are signed by the Group's key and delivered like any activity (shared inboxes, retries, 004's block filter,
none for a deleted Group). Recipients: the Group's accepted followers' servers and, when the reply's author is remote,
the author's own inbox. `{domain}` is this instance.

### A new reply (public shown; unlisted swaps `to` and `cc`'s Public and followers)

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/6b1e…",
  "type": "Announce",
  "actor": "https://{domain}/ap/actors/series-52991",
  "published": "2026-11-04T18:00:00Z",
  "to": ["https://www.w3.org/ns/activitystreams#Public"],
  "cc": ["https://{domain}/ap/actors/series-52991/followers", "https://mastodon.social/users/bob"],
  "object": "https://mastodon.social/users/bob/statuses/1131"
}
```

The object is the reply's id, never a copy: receivers fetch it from its origin. The shape is a Mastodon boost.

### An edit of an announced reply

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/8d4a…",
  "type": "Announce",
  "actor": "https://{domain}/ap/actors/series-52991",
  "published": "2026-11-04T18:30:00Z",
  "to": ["https://www.w3.org/ns/activitystreams#Public"],
  "cc": ["https://{domain}/ap/actors/series-52991/followers", "https://mastodon.social/users/bob"],
  "object": {
    "id": "https://mastodon.social/users/bob/statuses/1131#updates/1762280400",
    "type": "Update",
    "actor": "https://mastodon.social/users/bob",
    "object": "https://mastodon.social/users/bob/statuses/1131"
  }
}
```

The embedded `Update` is a pointer (the edit's id as the author's server gave it, or Megáfono's own Update activity id
for a local reply); receivers re-read the reply from its origin.

### A deleted reply: the announce retracted

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/9f07…",
  "type": "Undo",
  "actor": "https://{domain}/ap/actors/series-52991",
  "published": "2026-11-04T19:00:00Z",
  "to": ["https://www.w3.org/ns/activitystreams#Public"],
  "cc": ["https://{domain}/ap/actors/series-52991/followers", "https://mastodon.social/users/bob"],
  "object": {
    "id": "https://{domain}/ap/activities/6b1e…",
    "type": "Announce",
    "actor": "https://{domain}/ap/actors/series-52991",
    "object": "https://mastodon.social/users/bob/statuses/1131"
  }
}
```

### When

| Reply | Announce | Announce{Update} | Undo{Announce} |
|---|---|---|---|
| remote, into the Group's thread, `public` or `unlisted` (002 `reply.received`) | queued in the transaction of `reply.received` | in that of `reply.updated` | in that of `object.deleted` |
| local, published through the app by another local user into the Group's thread | in the publish transaction | in the `PUT`'s | in the `DELETE`'s |
| `followers` or `direct`; the Group's own reply; a reply recorded while `announcesReplies` was off; a reply in a Person's or Service's thread | — | — | — |

## Inbound: what Megáfono does with an `Announce` and an `Undo{Announce}`

| Activity | Accepted when | Effect |
|---|---|---|
| `Announce` of an IRI, of an object with an `id` that is not an activity, or of a `Create{…}` | actor = signer, activity id on the signer's host, signer's actor type `Group`, some live local user has an accepted outbound follow of it, the id not processed before; the target not on this instance; fetched from its origin signed: same `id`, type Note or Article, `attributedTo` an actor on the target's host that resolves, audience `public` or `unlisted` | `feed_posts` rows (`announced_by` = the Group, `remote_actor` = the author) and `post.received` with `announcedBy`, per qualifying user; marker = the Announce id |
| `Announce` of an `Update{…}` | the same checks; the target is the `Update`'s `object` | `post.updated` with `announcedBy` to the users with a live row announced by this Group who still follow it, block neither it nor the author, and do not follow the author; marker = the Announce id |
| `Undo` of an `Announce` (embedded, with its `object`) | actor = signer, activity id on the signer's host, the embedded Announce's `actor` = signer | live rows announced by this Group for the object turn `deleted`; `post.deleted` with `announcedBy` to their users who block neither; nothing fetched |
| `Announce` from a non-Group, from a Group nobody follows, of a `Delete`, of anything without an id, of our own object; an `Announce` whose target fails a check | — | 202, dropped, nothing fetched (the checks before the fetch cost no request) |
| `Undo` of a bare id or of a `Follow` | as before | 002/004's `Undo{Follow}` handling, unchanged |

The one fetch per handled `Announce` is signed by the instance actor and refuses instance-blocked hosts before any
connection (004). Nothing is fetched for a repeated `Announce` id (checked read-only first).
