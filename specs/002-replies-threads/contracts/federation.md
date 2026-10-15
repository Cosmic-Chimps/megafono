# Contract changes: public federation endpoints

Additive to [001's federation contract](../../001-federation-core/contracts/federation.md). All new paths are under
`/ap/*`, so the gateway rules do not change.

## Object documents (all published objects)

New fields (research R-20, R-23, R-24):

```json
{
  "id": "https://frunchy.example/ap/objects/7a2…",
  "type": "Note",
  "inReplyTo": "https://frunchy.example/ap/objects/6f1c…",
  "context": "https://frunchy.example/ap/contexts/6f1c…",
  "conversation": "https://frunchy.example/ap/contexts/6f1c…",
  "tag": [ { "type": "Mention", "href": "https://mastodon.social/users/bob", "name": "@bob@mastodon.social" } ],
  "interactionPolicy": { "canReply": { "automaticApproval": [ "https://www.w3.org/ns/activitystreams#Public" ] } },
  "replies": {
    "id": "https://frunchy.example/ap/objects/7a2…/replies",
    "type": "Collection",
    "totalItems": 12,
    "first": {
      "id": "https://frunchy.example/ap/objects/7a2…/replies?page=1",
      "type": "CollectionPage",
      "partOf": "https://frunchy.example/ap/objects/7a2…/replies",
      "items": [ "https://frunchy.example/ap/objects/…", "https://mastodon.social/users/bob/statuses/1131" ],
      "next": "https://frunchy.example/ap/objects/7a2…/replies?page=2"
    }
  }
}
```

`inReplyTo` and `tag` appear only on replies. `context` and `conversation` use the root's id for every object in
the thread (a root uses its own).

## New endpoints

| Method and path | Answer | Notes |
|---|---|---|
| `GET /ap/objects/{id}/replies` | 200 `Collection` with embedded `first` | same fetch rules as the object (404 to the unauthorized for followers-only, 410 when deleted) |
| `GET /ap/objects/{id}/replies?page=N` | 200 `CollectionPage`, `items` as ids, `next` when more | at most 50 items; public and unlisted replies only; the app's own first |
| `GET /ap/contexts/{rootId}` | 200 `Collection` whose first page lists the root and its public replies' ids | follows the root's fetch rules |

## Inbox additions

After the 001 pipeline (verification, rate limits, deduplication):

| Activity | Accepted when | Result |
|---|---|---|
| `Create{Note|Article}` | `attributedTo` = signer's actor; `inReplyTo` is a live object of ours or a live recorded reply | reply recorded, `reply.received` |
| `Update{Note|Article}` | signer = the recorded reply's author | `reply.updated` |
| `Delete{id|Tombstone}` | signer = the recorded reply's author | reply marked deleted, `object.deleted` |
| anything else | n/a | 202, dropped (as in 001) |
