# Quickstart: validating posts from followed accounts

How to see the feature work end to end. Event shapes are in [contracts/app-contract.md](contracts/app-contract.md),
the tables in [data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK, Docker running (Postgres for the integration suite; GoToSocial and Mastodon for interop).
- From the repository root: `dotnet build` and `dotnet build -c Release` clean.

## 1. Integration suite (stub remote server, fake app)

```sh
dotnet test --project tests/Megafono.Tests
```

Expected: the new `InboundPostsTests` (and the captured-payload replays) pass with the existing suite. They cover:

| Scenario | Expected |
|---|---|
| alex and carol follow bob (accepted); bob's public post arrives once on `/inbox` | one `post.received` for each, same `object` |
| dave's follow is pending / removed; erin follows nobody | nothing for dave or erin |
| bob's followers-only post; bob's direct post addressed to alex only | both to alex; the direct one not to carol |
| same `Create` twice, and the same post under another activity id on a personal inbox | still one event per user |
| bob replies to a third party's post | `post.received` with `inReplyTo` |
| bob replies to alex's published post; carol follows bob too | alex: `reply.received` only; carol: `post.received` |
| GoToSocial-style reused `Update` id with two different `updated` times; the same edit twice | two `post.updated`, not three |
| alex unfollows, then bob edits and deletes | no `post.updated` for alex; `post.deleted` for alex |
| alex blocks bob (seeded block next to an accepted follow); alex deleted | nothing for alex; carol as usual |
| instance blocks bob's domain | 403, nothing |
| `Announce` from bob | 202, nothing |
| bob's post carries data under a namespace the app published with | `extensions` with that namespace and the post's prefix |

## 2. Interop (real servers)

```sh
dotnet test --project tests/Megafono.Interop.Tests -- --filter-not-trait "Suite=mastodon"   # GoToSocial
dotnet test --project tests/Megafono.Interop.Tests -- --filter-trait "Suite=mastodon"       # Mastodon
```

Expected (`FeedTests`): a local user follows the server's account through `POST /internal/actors/{id}/following`
(approved when the account is locked); the account posts publicly and followers-only, edits, then deletes. The app
receives `post.received` (×2), `post.updated` and `post.deleted` for that user within 30 seconds each, and nothing for
a second local user who does not follow the account. After the user blocks the account, its next post produces no
event.

## 3. By hand (sample app)

1. Start the compose deployment (`deploy/`) and follow a Mastodon account from the sample app's user:
   `POST /internal/actors/{id}/following` with `{ "target": "@someone@mastodon.example" }`.
2. Post from that Mastodon account; the sample app's webhook receiver records a `post.received` event for the user.
3. Edit and delete the post; it records `post.updated` and `post.deleted`.
