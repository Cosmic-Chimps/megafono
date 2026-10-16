# Contract changes: federation

Additive to [001's federation contract](../../001-federation-core/contracts/federation.md).

## Outbound: `Block` and `Undo{Block}` (R-40)

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/{uuid}",
  "type": "Block",
  "actor": "https://{domain}/ap/actors/{id}",
  "object": "https://spam.example/users/bob",
  "to": ["https://spam.example/users/bob"]
}
```

```json
{
  "@context": "https://www.w3.org/ns/activitystreams",
  "id": "https://{domain}/ap/activities/{uuid2}",
  "type": "Undo",
  "actor": "https://{domain}/ap/actors/{id}",
  "object": { "id": "https://{domain}/ap/activities/{uuid}", "type": "Block",
              "actor": "https://{domain}/ap/actors/{id}", "object": "https://spam.example/users/bob" },
  "to": ["https://spam.example/users/bob"]
}
```

Signed with the local actor's key, POSTed to the blocked actor's own inbox (not the shared inbox). Neither id is
fetchable (404), like every activity that is not a Create or Update. A user block of a domain sends the existing
`Reject{Follow}` and `Undo{Follow}` documents instead (R-41).

## Inbound

| Request | Answer |
| --- | --- |
| `POST /inbox` or a personal inbox, `Signature` key id on an instance-blocked domain (or subdomain) | **403**, no key fetch, body not parsed |
| the same, key id of an instance-blocked actor (cached key owner, or key id without fragment), or verified signer blocked | **403**, body not parsed |
| `POST /ap/actors/{id}/inbox`, verified signer blocked by `{id}` (actor or domain) | **202**, dropped |
| `POST /inbox`, Follow of a user who blocks the signer | **202**, dropped, nothing sent |
| `POST /inbox`, Create/Update of a reply in a thread whose root author (or the reply's local parent author) blocks the signer | **202**, dropped, not recorded |

## Fetches

| Request | Answer |
| --- | --- |
| `GET /ap/actors/{id}`, its `followers`/`following`/`outbox`, `GET /ap/objects/{oid}` (and `/replies`), `/ap/contexts/{oid}`, `/ap/activities/{aid}`, signed with a key the instance blocks | **404** |
| the same for `{id}` (or the object's author) who blocks the key's actor or domain | **404** |
| `GET /ap/actor` signed with a key the instance blocks | **404** |
| reply and context collections | replies by blocked authors (instance, or the thread root's author) left out, counts included |

## Outbound fetches

None to an instance-blocked domain: key, actor, object and WebFinger fetches fail as blocked before any connection.
