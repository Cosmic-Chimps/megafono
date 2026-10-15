# Data Model: Actor Deletion

Changes to [001's data model](../001-federation-core/data-model.md) and [002's](../002-replies-threads/data-model.md).
All idempotent in `Infrastructure/Database.fs`.

## Schema changes

```sql
-- A deleted actor's keys are cleared once its Delete deliveries end (R-32)
ALTER TABLE local_actors ALTER COLUMN public_key_pem DROP NOT NULL;
ALTER TABLE local_actors ALTER COLUMN enc_private_key DROP NOT NULL;
```

`local_actors.deleted_at` already exists, and `ux_local_actors_handle` is already partial on `deleted_at IS NULL`.
No new table: the actor's `Delete` is an `activities` row (`activity_type = 'Delete'`, `object IS NULL`,
`audience = 'public'`), which tells it apart from object deletes (their `object` is set).

## State of a local actor

| State | `deleted_at` | keys | profile | follows | objects |
| --- | --- | --- | --- | --- | --- |
| live | NULL | set | set | rows | live or deleted |
| deleted, Delete in flight | set | set | cleared | none | all deleted, documents `{}` |
| deleted, done | set | NULL | cleared | none | all deleted, documents `{}` |

Transitions: live → deleted (the delete call, one transaction); deleted → done (housekeeping, when no delivery of
the actor's activities is `pending` or `sending`). Nothing leaves "deleted".

## The delete transaction (in order)

1. `SELECT … FROM local_actors WHERE app_user_id = @id FOR UPDATE`: none → 404; `deleted_at` set → answer the
   stored result (count of the deliveries of the actor's `Delete` activity).
2. Compute the destination inboxes (R-31) while the follow rows still exist.
3. Insert the `Delete{actor}` activity and its deliveries (`Outbox.enqueueDeliveries`).
4. Skip every other `pending`/`sending` delivery of the actor's activities: `status = 'skipped'`,
   `last_error = 'actor deleted'`, `lease_until`/`lease_token` cleared (a worker holding a lease then writes
   nothing).
5. Delete the actor's queued `app_events` (`pending`, `sending`).
6. Mark live objects deleted (`state = 'deleted'`, `deleted_at = now`), set every object `document` to `{}`, mark
   the actor's local replies deleted in `replies`, empty the `body` of its `Create`/`Update` activities.
7. Delete its `follows` rows.
8. Set `deleted_at = now` and clear `name` (empty), `summary`, `icon_url`, `profile_url` (empty).

## Key clearing (housekeeping)

```sql
UPDATE local_actors l SET public_key_pem = NULL, enc_private_key = NULL
WHERE l.deleted_at IS NOT NULL AND l.enc_private_key IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM deliveries d JOIN activities a ON a.id = d.activity
                  WHERE a.local_actor = l.app_user_id AND d.status IN ('pending', 'sending'))
RETURNING l.app_user_id
```

The returned ids are evicted from the delivery worker's key cache.
