# Quickstart: validating a Group's announces

Event and wire shapes are in [contracts/](contracts/), the columns in [data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK, Docker running (Postgres for the integration suite; GoToSocial and Mastodon for interop).
- From the repository root: `dotnet build` and `dotnet build -c Release` clean.

## 1. Integration suite (stub remote server, fake app)

```sh
dotnet test --project tests/Megafono.Tests
```

Expected: `GroupAnnounceTests` (outbound) and `InboundAnnouncesTests` (inbound) pass with the existing suite.

| Scenario | Expected |
|---|---|
| the series Group (`announcesReplies: true`) has carl as a follower; bob replies publicly | `reply.received` for the Group and one `Announce` of the reply's id delivered to carl's server and to bob's inbox, `to` Public, `cc` the Group's followers and bob |
| bob's reply is unlisted | the `Announce` addressed `to` followers, `cc` Public and bob |
| bob's reply is followers-only or direct; the Group has no `announcesReplies`; the actor is a Person; the Group replies in its own thread | nothing announced |
| bob edits, then deletes the announced reply | `Announce` of the `Update`, then `Undo` of the `Announce`, same recipients |
| alex (a local user) replies through the app into the Group's thread, edits, deletes | the same three, to the Group's followers |
| a lookup answer with `announcesReplies: true` on a Person | invalid answer |
| the stub plays a Group alex follows and announces bob's note (served by the stub) | `post.received` for alex: `actor` bob, `announcedBy` the Group, `extensions` when the namespace is the app's |
| the same Announce twice; another Announce id for the same note | one event; the repeat is not fetched |
| the Group announces an `Update` of the note; bea also follows bob | `post.updated` for alex only (bea hears from bob) |
| the Group sends `Undo` of its Announce | `post.deleted` for alex, nothing fetched |
| a Person announces; a Group nobody follows; our own object; a note attributed to another host; a followers-only note; alex blocks bob; alex blocks the Group; the instance blocks bob's domain; alex deleted; a note replying into alex's own thread | no event (alex gets `reply.received` only in the last case); nothing fetched where the contract says so |

## 2. Interop (real servers)

```sh
dotnet test --project tests/Megafono.Interop.Tests -- --filter-not-trait "Suite=mastodon"   # GoToSocial
dotnet test --project tests/Megafono.Interop.Tests -- --filter-trait "Suite=mastodon"       # Mastodon
```

Expected (`GroupAnnounceTests`): carol follows the app's announcing Group, the Group posts, carol replies publicly;
within 30 s carol's server lists the Group among the reply's boosters. A followers-only reply by carol is not boosted.
Carol's edit and deletion are followed by the Group's `Announce{Update}` and `Undo{Announce}`, both delivered (2xx).

## 3. By hand (two Megáfonos)

1. On instance B, answer `announcesReplies: true` for the series Group in the app's actor lookup.
2. On instance A, follow B's Group: `POST /internal/actors/{id}/following` with `{ "target": "@series@b.example" }`.
3. On B, post from the Group, then reply to that post from a Mastodon account or from one of B's own users.
4. A's webhook receiver gets `post.received` for A's follower with `announcedBy` B's Group; an edit of the reply gives
   `post.updated`, its deletion `post.deleted`.
