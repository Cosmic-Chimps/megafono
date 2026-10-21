# Data model: Posts from followed accounts

Two new tables, added to the idempotent schema in `src/Megafono/Infrastructure/Database.fs`. Nothing existing changes.
References only: Megáfono keeps no post content (FR-010), as `replies` (002 R-27).

## `feed_posts`

One row per remote post and local user it was passed to as `post.received` (R-51, R-55).

```sql
CREATE TABLE IF NOT EXISTS feed_posts (
    id            text NOT NULL,                                   -- the Note/Article id
    local_actor   text NOT NULL REFERENCES local_actors (app_user_id),
    remote_actor  text NOT NULL REFERENCES remote_actors (id),     -- the author: the signer of the Create
    audience      text NOT NULL CHECK (audience IN ('public', 'unlisted', 'followers', 'direct')),
    state         text NOT NULL DEFAULT 'live' CHECK (state IN ('live', 'deleted')),
    published_at  timestamptz NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    deleted_at    timestamptz NULL,
    PRIMARY KEY (id, local_actor)
);
```

| Field | Rules |
|---|---|
| `id` | the post's id as received (FR-004: one row per post and user, whatever activity or inbox brought it) |
| `local_actor` | a user the post went to; never the root author of a thread the post replies into (R-53) |
| `remote_actor` | the signer; `Update` and `Delete` act only on rows whose `remote_actor` is their signer |
| `audience` | how the post was addressed when it arrived; `post.updated` repeats it (the audience never changes) |
| `published_at` | the post's `published`, else when it arrived; repeated in `post.updated` |
| `state` | `live`, then `deleted` once; a deleted row is never live again |

- The primary key's leading `id` serves the lookups of `Update` and `Delete`
  (`WHERE id = @id AND remote_actor = @signer AND state = 'live'`).
- Rows are never deleted. A deleted local user's rows stay; `enqueueEvent` sends them nothing (003).

### State of one row

```
(none) --Create, user qualifies (R-52)--> live
live   --Update, user still follows and does not block (R-56)--> live (updated_at, post.updated)
live   --Update, otherwise--> live (no event)
live   --Delete, user does not block--> deleted (deleted_at, post.deleted)
live   --Delete, user blocks the author--> deleted (no event)
```

## `extension_namespaces`

The namespaces the app has published with (R-58).

```sql
CREATE TABLE IF NOT EXISTS extension_namespaces (
    namespace     text PRIMARY KEY,
    first_used_at timestamptz NOT NULL DEFAULT now()
);
```

- Written with `INSERT … ON CONFLICT DO NOTHING` in the publish and update transactions when the body has `extensions`.
- Filled once, when the table is first created, from `objects.document->'@context'`: every term of an object element
  of the context whose value is a JSON string and whose key is not one of Megáfono's own terms (`ostatus`,
  `conversation`, `gts`, `interactionPolicy`, `canReply`, `automaticApproval`: `ActivityStreams.ownTerms`). The create
  and the backfill run in one `DO` block guarded by the table's absence, so later starts do not scan `objects`.

## `processed_activities`

Unchanged. The feed path's `Update` marker is `post|{updateId}|{updated}` or `post|{updateId}` (R-55).

## Relationships used, not changed

- `follows` (`direction = 'outbound'`, `status = 'accepted'`, `remote_actor` = the signer): who follows the author.
- `blocks` via `Blocks.coveredSql`: user and instance blocks of the author or its domain.
- `objects` / `replies` via `Threads.resolveParent`: whether the post replies into one of the app's threads, and its
  root author (R-53).
