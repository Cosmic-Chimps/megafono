# Research: Actor Deletion

Numbering continues after 002 (R-20 to R-27).

## R-30 What remote servers expect from an actor deletion

- **Decision**: one `Delete` per server with `actor` and `object` both the actor id, `to` Public, signed by the
  actor's own key, then 410 Gone (with a `Tombstone`) on the actor id.
- **Why**: this is what Mastodon itself sends when an account is deleted (`ActivityPub::DeleteActorSerializer`).
  Mastodon handles `Delete` where `object == actor` by deleting the remote account and its statuses and follows
  (`ActivityPub::Activity::Delete#delete_person`); GoToSocial does the same in its Delete handler when the object is
  the requesting account. Both verify the signature with the key they **cached** when they first saw the actor, so
  the actor document can already answer 410 when the `Delete` arrives. A server that does not know the actor
  (Mastodon: unknown signer + `Delete` of itself) drops it, which is fine: it has nothing to delete. A server that
  misses the `Delete` and later refetches the actor gets 410, which Mastodon and GoToSocial treat as deletion.
- **Rejected**: keep serving the actor (with `suspended`) until deliveries end: more state for no gain, since the
  cached key is used. Sending a `Delete` per post: servers delete posts with the account; it would multiply
  deliveries by the post count.

## R-31 Which servers get the Delete

- **Decision**: the union of (a) remote actors with a follow row of the actor in either direction, pending or
  accepted, and (b) every inbox an earlier delivery of the actor's activities went to (reply recipients, follow
  answers, profile updates). Grouped by host: one inbox per host, the shared inbox when any known remote actor
  on that host has it as `shared_inbox`, otherwise the smallest inbox URL. A `Delete{actor}` is not addressed to a
  recipient, so any inbox of the server processes it.
- **Why**: "every follower and following server" (US6) misses servers that only received replies; (b) covers them
  at no cost. Mastodon goes further (every server it knows of); a sidecar knows fewer servers and (a)+(b) are the
  ones that can hold the account.
- **Rejected**: every known host in `remote_actors`: sends the actor's name to servers that never heard of it.

## R-32 Keys live until the Delete deliveries end

- **Decision**: the private key stays (encrypted) after the delete call and is cleared by housekeeping once no
  delivery of the actor's activities is `pending` or `sending` (delivered, failed after 48 h, or skipped). The
  delivery worker's in-memory key cache drops the key at the same time.
- **Why**: the `Delete` must be signed by the actor's key (R-30); after that the key has no use and is personal
  data. Clearing on a timer, not inside the delivery transaction, keeps the queue code unchanged.

## R-33 The tombstone row and "never reused"

- **Decision**: the `local_actors` row is kept with `deleted_at` set; lookups by id check it first and answer
  "deleted" without calling the app; storing an app profile never touches a deleted row (the insert's conflict
  clause skips it). The handle stays on the row but the unique index on handles is already partial
  (`WHERE deleted_at IS NULL`), so another id can take it.
- **Why**: Principle IV. Deleting the row would let the next lookup create a fresh actor with a new key under the old
  id. The handle belongs to the app's namespace; refusing it would block a legitimate new user.

## R-34 Serializing deletion with other writes

- **Decision**: the delete transaction locks the actor row `FOR UPDATE`; every internal write that creates an
  activity for an actor (publish, update and delete of objects, profile refresh, follow, unfollow, follow answers)
  takes `FOR SHARE` on a live row in its transaction and answers 410 when it is gone. `Outbox.enqueueEvent` only
  queues for live actors (it already updates the row, which conflicts with the delete's lock).
- **Why**: under READ COMMITTED, without the lock a publish could commit a Create and its deliveries after the delete
  skipped the queue, and a `Create` would follow the `Delete`.

## R-35 What erasure covers

- **Decision**: in the delete transaction: follow rows deleted; `name` set to the empty string, `summary`,
  `icon_url` cleared, `profile_url` empty; object `document` set to `{}`; `body` of the actor's `Create` and
  `Update` activities set to empty bytes (their deliveries are skipped in the same transaction); queued app events
  deleted. Later: keys cleared (R-32). Kept: the actor id, handle and type, object ids and types and timestamps
  (needed for 410 tombstones), activity rows of other types, delivery history.
- **Why**: a 410 tombstone needs only id, type and deletion time (`ActivityStreams.tombstone`). Event rows that were
  never delivered are about a user the app deleted; the app deduplicates by id anyway.
