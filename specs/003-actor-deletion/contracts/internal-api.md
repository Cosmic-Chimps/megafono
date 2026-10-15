# Contract changes: internal API

Additive to [001's internal API](../../001-federation-core/contracts/internal-api.md) (contract version 1, Principle
I): one new call, and a 410 answer on existing calls for an actor that was deleted. Errors keep the
`{ "error", "message" }` shape; `gone` is an existing code.

## Delete an actor (US6)

```http
DELETE /internal/actors/{id}
```

No body. `{id}` is the app's immutable user id. Megáfono does not consult the app's actor lookup, so the app may
call this before or after deleting the user on its side.

- **202** the first time:

  ```json
  { "id": "3f6c2a9e-…", "deletedAt": "2026-10-15T09:30:00Z", "deliveries": 3 }
  ```

  `deliveries` is the number of servers the `Delete` was queued for (0 when the actor never had followers,
  follows or deliveries). They are sent and retried like every delivery (48 h with backoff); once they have all
  ended, the actor's keys are erased.

- **202** with the same body on every repeat (the call is idempotent; nothing is queued again).
- **404** `not_found` when Megáfono has no actor for `{id}` (it was never looked up or published). Nothing exists to
  delete: treat the user as done.

What happens at once, in one transaction: the actor id, its collections and its objects start answering 410; its
queued deliveries and webhooks are cancelled; its follow rows, display profile and stored post copies are erased.
The id is never served again, even if the app's lookup keeps answering it; the handle is free for a new user with
a different id.

## Existing calls on a deleted actor

These answer **410** `gone` ("The actor was deleted") when `{id}` (or the author of `{objectId}`) is a deleted actor:

| Call | Before this feature |
| --- | --- |
| `POST /internal/actors/{id}/objects` | 404 after the lookup fallback |
| `PUT /internal/actors/{id}` (profile refresh) | 404 if the app forgot the user |
| `PUT /internal/objects/{objectId}` | 410 `gone` already: every object of a deleted actor is deleted |
| `POST /internal/actors/{id}/following`, `DELETE /internal/actors/{id}/following` | 404 |
| `POST /internal/actors/{id}/follow-requests/{followId}/approve` and `/reject` | 404 |
| `GET /internal/actors/{id}/followers`, `GET /internal/actors/{id}/following` | 404 |

`DELETE /internal/objects/{objectId}` keeps its answer for an already deleted object (202, nothing sent).
`GET /internal/objects/{objectId}/deliveries` keeps answering for the deleted actor's objects (skipped deliveries
show as `skipped`, `lastError` "actor deleted").
