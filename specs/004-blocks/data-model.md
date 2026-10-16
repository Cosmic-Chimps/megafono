# Data Model: Blocks

Changes to the data models of [001](../001-federation-core/data-model.md), [002](../002-replies-threads/data-model.md)
and [003](../003-actor-deletion/data-model.md). All idempotent in `Infrastructure/Database.fs`.

## Schema changes

```sql
CREATE TABLE IF NOT EXISTS blocks (
    id             uuid PRIMARY KEY,
    local_actor    text NULL REFERENCES local_actors (app_user_id),  -- NULL: the whole instance
    actor          text NULL,   -- remote actor id; not a foreign key, the actor may never have been fetched
    domain         text NULL,   -- lowercase ASCII host name, no port (R-42)
    block_activity uuid NULL REFERENCES activities (id),  -- the Block sent for a user's actor block (R-40)
    created_at     timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_blocks_one_target CHECK ((actor IS NULL) <> (domain IS NULL))
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_blocks_target
    ON blocks ((COALESCE(local_actor, '')), (COALESCE(actor, '')), (COALESCE(domain, '')));
CREATE INDEX IF NOT EXISTS ix_blocks_actor ON blocks (actor) WHERE actor IS NOT NULL;
CREATE INDEX IF NOT EXISTS ix_blocks_domain ON blocks (domain) WHERE domain IS NOT NULL;

-- 'Block' joins the activity types (the check is replaced once, like the handle check)
ALTER TABLE activities DROP CONSTRAINT activities_activity_type_check;  -- only when it lacks 'Block'
ALTER TABLE activities ADD CONSTRAINT activities_activity_type_check
    CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo', 'Block'));
```

A lifted block is deleted (it has no id outside Megáfono; the `Block` activity row stays for the history, and the
`Undo` embeds it).

## Matching (R-42), in SQL

`host` is `host[:port]`; `d` a stored domain:

```sql
split_part(host, ':', 1) = d OR right(split_part(host, ':', 1), length(d) + 1) = '.' || d
```

A remote actor is covered for local user `u` when a block with `local_actor IS NULL OR local_actor = u` has
`actor = <actor id>` or a `domain` matching the actor's host. One SQL fragment builder in `Infrastructure/Blocks.fs`
produces this `EXISTS (…)` for the queries that need it (enqueue filter, collections, skips).

## The user block transaction (in order)

1. `SELECT … FROM local_actors WHERE app_user_id = @u AND deleted_at IS NULL FOR UPDATE` (none: 404 or 410).
2. `INSERT INTO blocks … ON CONFLICT DO NOTHING RETURNING id`; nothing returned → answer the existing block, end.
3. Find the follows it ends: `pending`/`accepted` rows of `u` in both directions whose remote actor is covered.
4. For an actor block: insert the `Block` activity (`audience 'direct'`) and its delivery to the actor's inbox with
   `enqueueDeliveriesUnfiltered`; store its id in `blocks.block_activity`. Skipped when the actor's document cannot be
   had (no stored row and the fetch fails, done before the transaction opens).
   For a domain block: per ended follow, a `Reject` (inbound) or `Undo{Follow}` (outbound) activity and its delivery
   to that actor's inbox, unfiltered.
5. Set the ended follows to `removed` and queue a `follow.removed` event per row (direction and row id).
6. Skip the user's matching `pending`/`sending` deliveries except the ones just queued: `status = 'skipped'`,
   `last_error = 'blocked'`, lease cleared.

## The instance block transaction

1. `INSERT INTO blocks (local_actor NULL, …) ON CONFLICT DO NOTHING RETURNING id`; nothing → existing block, end.
2. Every `pending`/`accepted` follow of any local user with a covered actor becomes `removed`, with a
   `follow.removed` event to its user (skipped for deleted actors by `enqueueEvent` itself).
3. Every matching `pending`/`sending` delivery becomes `skipped` (`last_error = 'blocked'`).

## Lifting

- User actor block: delete the row; when `block_activity` is set, insert `Undo` (embedding that `Block`) and queue it
  to the actor's inbox (the block is gone, so the normal filter lets it through). Same transaction, actor row locked.
- Any domain block, instance actor block: delete the row; nothing else.
