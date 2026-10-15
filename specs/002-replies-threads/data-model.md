# Data Model: Replies and Threads

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-10

Additions to 001's schema ([001 data-model](../001-federation-core/data-model.md)), made idempotently at startup.

## `objects` (new columns)

| Column | Type | Rules |
|---|---|---|
| `in_reply_to` | `text` null | the parent's id (one of ours or a recorded remote reply) |
| `thread_root` | `uuid` null | the root object of the thread; null for a root (its own id is the thread) |

A root has `in_reply_to IS NULL`. A local reply has both set. Index on `(thread_root)`.

## `replies` (new table): every reply in one of our threads, local or remote

| Column | Type | Rules |
|---|---|---|
| `id` | `text` PK | the reply's ActivityPub id |
| `local_object` | `uuid` FK → `objects` null | set for the app's own replies |
| `remote_actor` | `text` FK → `remote_actors` null | set for remote replies |
| `parent` | `text` | `in_reply_to` of the reply |
| `parent_object` | `uuid` FK → `objects` null | the parent when it is one of our objects (for the `replies` collection) |
| `thread_root` | `uuid` FK → `objects` | the root object |
| `audience` | `text` | `public`, `unlisted`, `followers`, `direct` |
| `state` | `text` | `live`, `deleted` |
| `published_at`, `first_seen_at`, `updated_at`, `deleted_at` | `timestamptz` | |

Constraints: exactly one of `local_object` and `remote_actor` is set. Indexes on `(parent, state)` and
`(thread_root, state)`. No content columns (research R-27).

**Collection query** (FR-020): `WHERE parent = @objectIri AND state = 'live' AND audience IN ('public','unlisted')
ORDER BY (local_object IS NULL), published_at, id`, paged by 50.

## States

```text
remote reply: (none) --Create, parent known--> live --Update by author--> live (updated_at, event)
                                                    --Delete by author--> deleted (event)
local reply:  mirrors the object's state (live → deleted with the object)
```

## App events (new types)

`reply.received`, `reply.updated` and `object.deleted` (for replies) go to the **thread root's author**, with that
user's `seq` (001 FR-057). Payloads in [contracts/app-contract.md](contracts/app-contract.md).

## Validation

- Inbound: the reply's `attributedTo` equals the signer's actor; `id` on the signer's host (else refetched,
  001 FR-015); `inReplyTo` resolves to a live object of ours or a live recorded reply.
- Outbound: `inReplyTo` must resolve the same way; audience `public` or `unlisted`; the replying actor is any live
  local actor (a Person replying to a Group's thread is the normal case).
