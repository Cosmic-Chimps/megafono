# Megáfono

Megáfono is a self-hosted ActivityPub sidecar. Your app keeps its own users, data and UI; Megáfono owns everything
at the protocol level: WebFinger, NodeInfo, actor documents, keys, HTTP signatures, followers, delivery and inbound
verification. Your app talks to it through a small HTTP API and webhooks and contains no ActivityPub code, so a
profile in your app can be followed from Mastodon and other servers. It runs as one process on Postgres, behind a
gateway.

## Integrating an app

Three pieces, all described in [`specs/001-federation-core/contracts/`](specs/001-federation-core/contracts/):

1. **Actor lookup.** Your app answers `GET /megafono/actors?handle=…` and `GET /megafono/actors/{id}` with the
   user's immutable id, handle and profile, or 404.
2. **Event receiver.** Your app accepts `POST /megafono/webhooks` (follow requests, accepted follows, failed
   deliveries…), verifies the `Megafono-Signature` header and answers 2xx when it has handled the event.
3. **Internal API calls.** Your app calls `/internal/*` on Megáfono to publish, approve or reject follow requests,
   list followers, refresh a profile, delete a user everywhere
   ([`specs/003-actor-deletion/contracts/`](specs/003-actor-deletion/contracts/)) and block remote accounts or whole
   servers, per user or for the instance ([`specs/004-blocks/contracts/`](specs/004-blocks/contracts/)).

[`samples/SampleApp`](samples/SampleApp) shows all three in under 100 lines of integration code
(`samples/SampleApp/Megafono.fs`).

## Replies and threads

Posts can be conversations. A reply from Mastodon or GoToSocial to a post your app published reaches it as
`reply.received` (edits as `reply.updated`, deletions as `object.deleted`), and your app publishes a reply with
`inReplyTo`. A reply your app removes stays on other servers, and replies that never mention one of your actors never
reach Megáfono. [`docs/operating.md`](docs/operating.md#replies-and-threads) has the details and the limits.

## Following and a feed

When your users follow remote accounts, the posts of those accounts reach your app as `post.received` (edits as
`post.updated`, deletions as `post.deleted`), one event per following user, so your app can build a feed. Megáfono
keeps no copy and offers no timeline query; boosts and past posts are not passed on.
[`docs/operating.md`](docs/operating.md#posts-from-followed-accounts-a-feed) has the details and the limits.

## Blocks

A user can block a remote account or a whole server, and the operator can block a server (or one account) for the
whole instance. Follows end both ways, nothing more is delivered to the blocked side, what it sends is dropped before
it reaches your app, and its signed fetches answer 404. A user block of an account tells that account's server with a
`Block`, as Mastodon and GoToSocial do. [`docs/operating.md`](docs/operating.md#blocks) has the details and the limits.

## Quick start

[`deploy/compose.yaml`](deploy/compose.yaml) runs the gateway, Megáfono, the sample app and Postgres. Copy
`deploy/.env.example` to `deploy/.env`, fill in the secrets and the certificate paths, and start it with
`docker compose -f deploy/compose.yaml --env-file deploy/.env up -d --build`.
[`docs/operating.md`](docs/operating.md) has the full walk-through, every setting, the master key and the gateway
routing rules.

## Images

Each release tag (`v0.1.0`) publishes two multi-arch (amd64, arm64) images:

- `ghcr.io/cosmic-chimps/megafono`: the service (8080 federation, 8081 internal API).
- `ghcr.io/cosmic-chimps/megafono-gateway`: the reference YARP gateway that splits a domain by path between
  Megáfono and the app. Without a certificate it serves plain http on 8080, for running behind a TLS proxy.

Tags: `0.1.0`, `0.1` and `latest` (prereleases such as `v0.2.0-rc.1` get only their own tag). Pin a version in
production. The workflow is [`.github/workflows/release.yml`](.github/workflows/release.yml).

## Privacy limit

Followers-only on the fediverse is a request, not a guarantee: once a post is delivered, the receiving server can
show it to anyone. Megáfono sends such posts only to followers' servers and answers 404 to everyone else, but it
cannot control what other servers do. See
[What followers-only means](docs/operating.md#what-followers-only-means).

## Build and test

From the repository root:

```sh
dotnet build
dotnet test                                                   # everything
dotnet test --project tests/Megafono.Tests                    # unit and integration tests
dotnet test --project tests/Megafono.Interop.Tests            # interop against GoToSocial and Mastodon (needs Docker)
```

The stack is F# on .NET 10, Oxpecker, raw Npgsql and Postgres. Tests use xUnit v3 on Microsoft.Testing.Platform.
`scripts/count-integration-lines.sh` checks the sample's integration budget.

## Status

Features 001 (federation core, [`specs/001-federation-core`](specs/001-federation-core)), 002 (replies and threads,
[`specs/002-replies-threads`](specs/002-replies-threads)), 003 (actor deletion,
[`specs/003-actor-deletion`](specs/003-actor-deletion)), 004 (blocks, [`specs/004-blocks`](specs/004-blocks)) and 005
(posts from followed accounts, [`specs/005-followed-posts`](specs/005-followed-posts)) are done; each folder holds the spec, plan and tasks. [`docs/vision.md`](docs/vision.md) holds the product vision.

## License

MIT, see [`LICENSE`](LICENSE).

## Related projects

- **Umbral**: a self-hosted OpenID Connect provider for social login, so apps don't wire OAuth by hand. Megáfono does not depend on it; signing users in stays the app's job.
- **Frunchy**: an F# app and the first intended consumer of Megáfono. Megáfono does not depend on it.
