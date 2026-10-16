# Contract changes: app contract (webhooks)

Additive to [001's app contract](../../001-federation-core/contracts/app-contract.md).

`follow.removed` is also sent when a block ends a follow, in either direction:

| Event | When | Fields |
| --- | --- | --- |
| `follow.removed` | a remote follower unfollows (001), or a block ends a follow: a user block, an instance block, or a remote actor's `Block` of the user | `follow` (`inbound` or `outbound`, `removed`), `actor` |

An `outbound` direction is new: the user's own follow of the remote actor ended. Receivers that only expected
`inbound` must read `follow.direction`; the payload shape is unchanged. Nothing else in the app contract changes:
the actor lookup is not consulted for remote actors, and no webhook announces a block itself (clarified: the app made
its own blocks, and a remote block shows only through its `follow.removed` events). New replies from a blocked actor
or domain produce no `reply.received`.
