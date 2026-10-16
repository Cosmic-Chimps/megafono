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

-- 'Block' joins the activity types: the check is replaced once, only when it lacks 'Block' (like the handle check)
ALTER TABLE activities DROP CONSTRAINT activities_activity_type_check;
ALTER TABLE activities ADD CONSTRAINT activities_activity_type_check
    CHECK (activity_type IN ('Create', 'Update', 'Delete', 'Accept', 'Reject', 'Follow', 'Undo', 'Block'));
```

A lifted block is deleted (it has no id outside Megáfono; its `Block` activity row stays, and the `Undo` embeds it).
Remote blocks of local users (R-49) are not stored: only their effect on `follows` remains.

### Block entity

| Field | Rule |
| --- | --- |
| `local_actor` | a live local actor (user block) or NULL (instance block) |
| `actor` / `domain` | exactly one; `actor` an absolute `https` actor id not on the app's domain (a handle is resolved to it first, R-48); `domain` normalized (R-42), not covering the app's domain |
| uniqueness | one block per (owner, target); a repeat answers the existing row |
| `block_activity` | set only for a user block of an actor whose inbox was known or fetched |

## Matching (R-42), in SQL

`host` is `host[:port]`; `d` a stored domain:

```sql
split_part(host, ':', 1) = d OR right(split_part(host, ':', 1), length(d) + 1) = '.' || d
```

A remote actor is covered for local user `u` when a block with `local_actor IS NULL OR local_actor = u` has
`actor = <actor id>` or a `domain` matching the actor's host. One SQL fragment builder in `Infrastructure/Blocks.fs`
produces this `EXISTS (…)` for every query that needs it (enqueue filter, collections, skips, follow removal).

## The user block transaction (in order)

0. Before it: resolve a handle (R-48); for an actor block, get the actor's document (cached or fetched) to learn its
   inbox; failure only means no `Block` is sent.
1. `SELECT … FROM local_actors WHERE app_user_id = @u AND deleted_at IS NULL FOR UPDATE` (none: 404 or 410).
2. `INSERT INTO blocks … ON CONFLICT DO NOTHING RETURNING id`; nothing returned → answer the existing block, end.
3. Find the follows it ends: `pending`/`accepted` rows of `u` in both directions whose remote actor is covered.
4. Actor block: insert the `Block` activity (`audience 'direct'`) and its delivery to the actor's inbox with
   `enqueueDeliveriesUnfiltered`; store its id in `blocks.block_activity`. Domain block: per ended follow, a `Reject`
   (inbound) or `Undo{Follow}` (outbound) activity and its delivery to that actor's inbox, unfiltered.
5. Set the ended follows to `removed` and queue a `follow.removed` event per row (direction, row id, actor summary).
6. Skip the user's matching `pending`/`sending` deliveries except those just queued: `status = 'skipped'`,
   `last_error = 'blocked'`, lease cleared.

## The instance block transaction

1. `INSERT INTO blocks (local_actor NULL, …) ON CONFLICT DO NOTHING RETURNING id`; nothing → existing block, end.
2. Every `pending`/`accepted` follow of any local user with a covered actor becomes `removed`, with a `follow.removed`
   event to its user (`enqueueEvent` itself skips deleted actors).
3. Every matching `pending`/`sending` delivery becomes `skipped` (`last_error = 'blocked'`).

## Inbound `Block` (R-49)

One transaction: `markProcessed`, `lockLiveActor` on the target (not live → `Ignored`), every `pending`/`accepted`
follow between target and signer in either direction → `removed` with a `follow.removed` event per row. Nothing is
queued for delivery.

## Lifting

- User actor block: delete the row; when `block_activity` is set, insert `Undo` (embedding that `Block`) and queue it
  to the actor's inbox (the block is gone, so the normal filter lets it through). Same transaction, actor row locked.
- Domain blocks and instance actor blocks: delete the row; nothing else.

## State of a follow row

Unchanged states (001). New transitions: `pending`/`accepted` → `removed` by a block (user, instance or remote). A
`removed` row can become `pending` again through a new Follow, exactly as after an unfollow, once no block covers it.
