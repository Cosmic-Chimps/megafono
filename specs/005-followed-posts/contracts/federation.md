# Contract changes: federation (inbound)

Additive to [001's federation contract](../../001-federation-core/contracts/federation.md) and
[002's](../../002-replies-threads/contracts/federation.md). The verification pipeline (`Federation/Inbox.fs`, steps 1
to 9) is unchanged, and so is every answer: 202 for anything verified, whether acted on or not; 403 for
instance-blocked signers (004). Personal inboxes behave as the shared inbox: recipients come from follows and
addressing, never from the inbox a post arrived on (004's early 202 for a user who blocks the signer is unchanged).

| Activity | Accepted when | Effect (feed) | Also (002, unchanged) |
|---|---|---|---|
| `Create{Note\|Article}` | actor = signer, activity id on the signer's host, object attributed to the signer (embedded on the signer's host, else fetched signed from its origin) | `feed_posts` rows and `post.received` for each live local user with an accepted outbound follow of the signer who does not block it; `direct`: only addressed ones; the root author of a thread the post replies into is left out | the reply handling when `inReplyTo` resolves into one of our threads |
| `Update{Note\|Article}` | actor = signer, activity id on the signer's host, live `feed_posts` rows of the object by the signer; the object re-read must have the same id | marker `post|{updateId}|{updated}`; `post.updated` to row users who still follow and do not block | the reply update when it is a recorded reply |
| `Delete` (object an IRI or a `Tombstone`) | actor = signer, activity id on the signer's host, live `feed_posts` rows of the object by the signer | rows `deleted`; `post.deleted` to row users who do not block the signer | the reply delete |
| `Announce` | — | none: unhandled type, 202 and dropped | — |

The object of `Create`/`Update` is read at most once per activity and shared by both paths. Nothing is fetched for an
`Update` of an object that is neither a recorded reply nor a live feed post.
