# Research: Blocks

Numbering continues after 003 (R-30 to R-35).

## R-40 What a user block of an actor sends

- **Decision**: one `Block` (`actor` the local actor, `object` the blocked actor id, addressed `to` the blocked
  actor), signed by the local actor, to the blocked actor's own inbox. Lifting the block sends `Undo` with the
  stored `Block` embedded. No `Undo{Follow}` or `Reject{Follow}` next to it.
- **Why**: it is what Mastodon (`ActivityPub::BlockSerializer`, sent by `BlockService`) and GoToSocial send for a
  user block. On receipt, Mastodon's `ActivityPub::Activity::Block` unfollows in both directions and rejects a
  pending request before recording the block; GoToSocial deletes follows and follow requests in both directions.
  One activity therefore ends both follows on the remote side. Both verify it with the actor key they cached when
  the follow started, so the 404 the blocked actor gets on fetches (R-44) does not stop it.
- **Observed (interop T014, GoToSocial 0.22.2)**: to be filled in by the interop test.
- **Rejected**: `Reject{Follow}` + `Undo{Follow}` only: hides the block from the remote server but leaves it free to
  send a new follow request at once, and needs two activities. Sending nothing: the remote server keeps showing the
  follows and keeps delivering.

## R-41 What a domain block sends

- **Decision**: a user block of a domain sends `Reject` of every inbound follow it ends (embedding the stored Follow
  id, as the approve/reject call does) and `Undo{Follow}` of every outbound follow it ends, each to the remote
  actor's own inbox. An instance block (domain or actor) sends nothing.
- **Why**: Mastodon's user domain block (`AfterBlockDomainFromAccountService`) rejects the followers on that domain
  so their servers drop the follow; there is no "domain Block" activity. Mastodon sends nothing when an admin
  suspends a domain; the blocked server is not owed a notice and Megáfono refuses all its traffic anyway.
- **Rejected**: one `Block` per actor of the domain (announces a block of people the user never named; lifting it
  would need as many `Undo`s).

## R-42 Matching a domain

- **Decision**: domains are stored lowercase, ASCII (IDN to punycode), without port, validated as host names. A host
  (`remote_actors.host`, `deliveries.host`, the authority of a key id, all `host[:port]`) matches when its host name
  (port removed) equals the domain or ends with `.` + the domain. The app's own domain is refused.
- **Why**: Mastodon domain blocks cover subdomains; spam networks use them. Ports only appear in tests.

## R-43 Refusing inbound traffic before any work

- **Decision**: the inbox checks instance blocks right after the signature header is parsed (step 3): the key id's
  host against instance domain blocks, and the key id against instance actor blocks (the cached `remote_actors.key_id`
  owner, or the key id without its fragment), and answers **403** before any key fetch. After the signer is resolved
  (step 5) its id and host are checked again (a key id that maps to a blocked actor only after a fetch). User blocks
  are checked by the handlers, inside their transactions after the local actor row is locked (R-46), and answer 202
  `Ignored`; a personal inbox of a user who blocked the signer is dropped right after step 5.
- **Why**: Mastodon answers 403 for suspended domains in `SignatureVerification`, and senders treat a 4xx other than
  408/429 as final, so the blocked server stops retrying. A claim the signature has not proven is enough to refuse:
  someone who claims a blocked key only gets refused. User blocks must not be revealed by the inbox, and the same
  shared-inbox activity may concern other local users.
- **Rejected**: 202 for instance blocks (the sender keeps nothing back, but neither does it learn to stop; Mastodon
  set the precedent). Verifying before refusing (a key fetch to the blocked server is the work the task forbids).

## R-44 Refusing fetches

- **Decision**: actor documents, collections, objects, activities and reply/context collections answer 404 when the
  request's `Signature` claims a key of a blocked target: instance blocks for every actor, user blocks of the actor
  served (or of the object's author). Like R-43, the claim decides, without verifying. The instance actor `/ap/actor`
  is checked against instance blocks only.
- **Why**: the vision rule ("fetches from that actor or domain answer 404") and Principle II's 404-never-403. Mastodon
  and GoToSocial sign most fetches with their instance key, so a user block of one actor only catches fetches signed
  by that actor's own key; the docs state it.

## R-45 Stopping deliveries

- **Decision**: three layers.
  1. At block time, in the block's transaction: matching `pending`/`sending` deliveries become `skipped`,
     `last_error = 'blocked'`, lease cleared (instance: every delivery whose host matches the domain, or whose inbox
     is the blocked actor's own inbox; user: the same, limited to the user's activities).
  2. At enqueue time: `Outbox.enqueueDeliveries` leaves out inboxes that a block covers for the activity's author
     (instance blocks and the author's user blocks). A blocked actor's own inbox is matched through
     `remote_actors.inbox`; shared inboxes are only left out by a domain block, since they serve other followers too.
     The block's own activities go through `Outbox.enqueueDeliveriesUnfiltered`.
  3. At send time, `Workers/Deliveries` skips (`Skipped "blocked"`) a delivery that an instance block covers. This
     closes the race of an enqueue that committed just after the block's step 1 (instance blocks lock no row).
- **Why**: user blocks never race (R-46). The send-time check is limited to instance blocks because the only
  activities sent to a blocked target, the block's own, come from user blocks.
- **Rejected**: an advisory lock shared by every enqueue and taken exclusively by blocks: it deadlocks against the
  per-actor row locks that 003 already takes (a publish holds its actor row and waits for the advisory lock while an
  instance block holds the advisory lock and waits for that row to queue a `follow.removed`).

## R-46 Serializing a user block with the user's other writes

- **Decision**: the user block transaction locks the local actor row `FOR UPDATE` (as the delete of 003 does). Every
  write that queues an activity of the actor already takes `FOR SHARE` on it (`Outbox.lockLiveActor`, R-34), and so
  does the inbound Follow; the reply handlers take it on the thread's author before checking blocks.
- **Why**: under READ COMMITTED, a publish either commits before the block (its deliveries are then skipped by step
  1 of R-45) or waits and then sees the block at enqueue time.

## R-47 Hiding blocked replies from collections

- **Decision**: the reply and context collection queries leave out replies whose remote author is covered by an
  instance block or by a user block of the thread root's author. The rows stay; lifting the block shows them again.
- **Why**: the collections are public and a blocked actor's replies in the user's threads are what the block is for.
  Deleting the rows would make an unblock lossy and the counts wrong for no gain.
