# Contract changes: internal API

Additive to [001's internal API](../../001-federation-core/contracts/internal-api.md) (contract version 1, Principle
I): six new calls and one new error code, `blocked`. Errors keep the `{ "error", "message" }` shape. Bearer token as
for every internal call.

## The block object

```json
{ "actor": "https://spam.example/users/bob", "domain": null, "createdAt": "2026-10-15T09:30:00Z" }
```

Exactly one of `actor` (a remote actor id) and `domain` (a host name, lowercase, no port) is set.

## User blocks

```http
POST /internal/actors/{id}/blocks
{ "actor": "https://spam.example/users/bob" }      or      { "domain": "spam.example" }
```

- **201** with the block object when it is new. Its effects happen in the same transaction: the follows between the
  user and the target end in both directions (each with a `follow.removed` webhook), the user's waiting deliveries
  to the target are cancelled, and for an actor block a `Block` is queued to the actor (when its document can be
  fetched or is already known); for a domain block, a `Reject` or `Undo` of each ended follow.
- **200** with the existing block object on a repeat; nothing happens again.
- **400** `invalid_request`: not exactly one of `actor`/`domain`; `actor` not an absolute `https` URL; `domain` not
  a host name; the target is this instance's own domain or one of its actors.
- **404** `not_found`: the app does not know the user. **410** `gone`: the actor was deleted (003).

```http
GET /internal/actors/{id}/blocks
```

**200** `{ "items": [ <block object>, … ] }`, newest first. 404/410 as above.

```http
DELETE /internal/actors/{id}/blocks?actor={actorId}      or      ?domain={domain}
```

- **204** when the block existed and is lifted; for an actor block that had sent a `Block`, an `Undo` of it is
  queued. Ended follows are not restored.
- **404** `not_found` when there is no such block (or the app does not know the user). **400** without exactly one
  of the parameters. **410** for a deleted actor.

## Instance blocks

```http
POST /internal/blocks        { "domain": "spam.example" }   or   { "actor": "https://spam.example/users/bob" }
GET /internal/blocks
DELETE /internal/blocks?domain={domain}      or      ?actor={actorId}
```

Same bodies and answers as the user calls, without 404/410 for a user. Creating one ends every follow between any
local user and the target (a `follow.removed` webhook to each user concerned) and cancels every waiting delivery to
it; nothing is sent to the blocked server. Lifting one sends nothing.

## Existing calls

| Call | New answer |
| --- | --- |
| `POST /internal/actors/{id}/following` | **409** `blocked` when the user or the instance blocks the target or its domain |
| `GET /internal/remote-actors?handle=` | **409** `blocked` for a handle on an instance-blocked domain |
