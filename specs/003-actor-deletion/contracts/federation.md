# Contract changes: federation

Additive to [001's federation contract](../../001-federation-core/contracts/federation.md).

## Outbound: `Delete{actor}` (R-30)

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/{uuid}",
  "type": "Delete",
  "actor": "https://{domain}/ap/actors/{id}",
  "object": "https://{domain}/ap/actors/{id}",
  "published": "2026-10-15T09:30:00Z",
  "to": ["https://www.w3.org/ns/activitystreams#Public"]
}
```

Signed with the actor's key (`keyId` `https://{domain}/ap/actors/{id}#main-key`), one POST per server (R-31). The
activity id is not fetchable (404), like every activity that is not a Create or Update.

## Fetches after deletion

| URL | Answer |
| --- | --- |
| `GET /ap/actors/{id}` | **410** `application/activity+json` `Tombstone` (below) |
| `GET /ap/actors/{id}/followers`, `/following`, `/outbox` | **410**, the same `Tombstone` |
| `POST /ap/actors/{id}/inbox` (validly signed) | **410**, nothing processed |
| `POST /inbox` with a Follow of the deleted actor | **404**, nothing processed |
| `GET /ap/objects/{oid}` of a public or unlisted object | **410** object `Tombstone` (unchanged builder) |
| `GET /ap/objects/{oid}` of a followers-only object | **404** to everyone (no follower is left to authorize) |
| `GET /.well-known/webfinger?resource=acct:{handle}@{domain}` | **404**, unless the app gave the handle to another id |

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/actors/{id}",
  "type": "Tombstone",
  "formerType": "Person",
  "deleted": "2026-10-15T09:30:00Z"
}
```

`formerType` is the actor's type (`Person`, `Service` or `Group`).
