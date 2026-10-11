# Contract: Public federation endpoints

Served on the federation port (default 8080), reached through the gateway. The gateway routes
exactly these paths to Megáfono and everything else to the app, preserving `Host` and never
rewriting the path or body (FR-063). Wire shapes follow [research.md](../research.md) R-01 to R-08.

| Method and path | Answer | Content-Type | Notes |
|---|---|---|---|
| `GET /.well-known/webfinger?resource=acct:{handle}@{domain}` | 200 JRD, 404, 400 | `application/jrd+json` | R-03; other domains and unknown handles 404 |
| `GET /.well-known/nodeinfo` | 200 | `application/json` | links 2.0 and 2.1 |
| `GET /nodeinfo/2.0`, `GET /nodeinfo/2.1` | 200 | `application/json; profile="http://nodeinfo.diaspora.software/ns/schema/2.x#"` | R-04 |
| `GET /ap/actor` | 200 | `application/activity+json` | instance actor, Application (FR-022) |
| `GET /ap/actors/{id}` | 200, 404 | `application/activity+json` | R-02; 404 when the app says unknown |
| `GET /ap/actors/{id}/outbox`, `/followers`, `/following` | 200, 404 | `application/activity+json` | `OrderedCollection` with `totalItems`, no items |
| `GET /ap/objects/{objectId}` | 200, 404, 410 | `application/activity+json` | audience rules below |
| `GET /ap/activities/{activityId}` | 200, 404, 410 | `application/activity+json` | Create/Update only, same rules as the object |
| `POST /ap/actors/{id}/inbox` | 202, 400, 401, 404, 413, 429 | n/a | personal inbox |
| `POST /inbox` | 202, 400, 401, 413, 429 | n/a | shared inbox |

## Fetch rules (FR-045, Principle II)

| Object audience | Who gets 200 | Everyone else |
|---|---|---|
| `public`, `unlisted` | anyone (signed or not) | n/a |
| `followers` | a valid signed GET whose key owner is an accepted follower of the author now | **404** (never 403), including unsigned requests and remote instance actors |
| `public`, `unlisted`, deleted | anyone gets **410** with a Tombstone | n/a |
| `followers`, deleted | an accepted follower (signed, as above) gets **410** with a Tombstone | **404**, so the object's existence stays hidden |

Signatures on GETs are verified when present (they are required to see followers-only objects).
Actor documents and collections are public.

## Inbox rules (FR-010 to FR-016)

Processing order, each step refusing before the next runs:

1. Per-address rate limit → 429 `Retry-After`.
2. Body over 1 MB (declared or actual) → 413.
3. Missing `Signature`, `Date` outside ±12 h, required headers not signed → 401.
4. `Digest` missing or not matching the raw bytes → 401.
5. Key fetch (signed with the instance actor) and signature check, one refetch on failure → 401.
6. Per-verified-host rate limit → 429 `Retry-After`.
7. Parse JSON → 400 if invalid.
8. Activity `id` seen in the last 7 days → 202, ignored.
9. Embedded object whose id host differs from the signer's host → refetched from its origin.
10. Dispatch: Follow, Undo{Follow}, Accept{Follow}, Reject{Follow}, Update{actor} handled; anything
    else 202 and dropped.

Answers are 202 for accepted activities, even before the app hears of them.

## Outbound requests

Every outbound request is signed (R-01): deliveries with the author's key, follow traffic with the
user's key, fetches with the instance actor's key. `User-Agent: Megafono/<version> (+https://{domain}/)`.
Only `https` targets; private addresses refused (FR-021).
