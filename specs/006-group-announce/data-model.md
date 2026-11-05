# Data model: Group actors announce the replies they receive

Three new columns and one widened check, added to the idempotent schema in `src/Megafono/Infrastructure/Database.fs`.
No new table. References only: no announced content is stored (FR-011).

## `local_actors.announces_replies`

```sql
ALTER TABLE local_actors ADD COLUMN IF NOT EXISTS announces_replies boolean NOT NULL DEFAULT false;
```

| Rules |
|---|
| Written from the lookup answer's `announcesReplies` by `AppClient.store` (insert and update), like `discoverable` and `indexable` (R-61) |
| Only a `Group` can have it `true`: `parseProfile` refuses the answer otherwise, so the column never says true for another type |
| Not part of the actor document: a change is not announced to followers |

## `replies.announce_activity`

```sql
ALTER TABLE replies ADD COLUMN IF NOT EXISTS announce_activity uuid NULL REFERENCES activities (id);
```

| Rules |
|---|
| The id of the `Announce` the thread's Group sent for this reply (R-62), set in the transaction that recorded the reply and queued the announce |
| NULL for a reply recorded while the Group was not announcing, for a followers-only or direct reply, for the Group's own replies, and for every reply in a Person's or Service's thread |
| An edit of a reply with it set queues `Announce{Update}`; a deletion queues `Undo{Announce}` embedding this activity's id (R-63); without it, nothing |

## `feed_posts.announced_by`

```sql
ALTER TABLE feed_posts ADD COLUMN IF NOT EXISTS announced_by text NULL REFERENCES remote_actors (id);
```

| Rules |
|---|
| The remote `Group` whose `Announce` brought the post to this user (R-66); NULL when the post came straight from a followed account (005) |
| `remote_actor` stays the post's author, so 005's `Update` and `Delete` by the author act on announced rows too |
| `Announce{Update}` and `Undo{Announce}` from a Group act only on rows whose `announced_by` is that Group |

### State of an announced row

```
(none) --Announce by a followed Group, user qualifies (R-66)--> live (announced_by = group)
live   --Announce{Update} by the group; user still follows it, blocks neither, does not follow the author--> live (post.updated)
live   --Update by the author (005); user follows the author--> live (post.updated, 005's rule)
live   --Undo{Announce} by the group; user blocks neither--> deleted (post.deleted)
live   --Delete by the author (005)--> deleted (post.deleted, 005's rule)
```

## `activities.activity_type`

`'Announce'` joins the check (`Create`, `Update`, `Delete`, `Accept`, `Reject`, `Follow`, `Undo`, `Block`, `Announce`),
replaced once on databases that lack it, in the same `DO` block pattern 004 used for `'Block'`. An announce row has
`object` NULL (the reply is not one of our objects) and `audience` the reply's (`public` or `unlisted`); the `Undo`
of an announce is an `'Undo'` row with the same audience.

## `processed_activities`

Unchanged. The inbound `Announce` id (new post or edit) is recorded as its own marker (R-67).

## Relationships used, not changed

- `follows` (`direction = 'inbound'`, `status = 'accepted'`, `local_actor` = the Group): the Group's followers'
  inboxes for the announce (shared via `Outbox.followerInboxes`, the query `Internal/Objects.fs` and
  `Internal/Actors.fs` already had).
- `follows` (`direction = 'outbound'`, `status = 'accepted'`, `remote_actor` = the announcing Group, and = the
  author for R-67's exclusion).
- `remote_actors.actor_type` (`Group`): who may announce into the feed; `remote_actors.inbox` of the author: where the
  Group's announce also goes.
- `blocks` via `Blocks.coveredSql`, for the Group and for the author.
- `objects` / `replies` via `Threads.resolveParent`: the Group as root author (outbound), and R-53's exclusion
  (inbound).
