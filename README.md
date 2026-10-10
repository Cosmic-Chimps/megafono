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
   list followers and refresh a profile.

[`samples/SampleApp`](samples/SampleApp) shows all three in under 100 lines of integration code
(`samples/SampleApp/Megafono.fs`).

## Quick start

[`deploy/compose.yaml`](deploy/compose.yaml) runs the gateway, Megáfono, the sample app and Postgres. Copy
`deploy/.env.example` to `deploy/.env`, fill in the secrets and the certificate paths, and start it with
`docker compose -f deploy/compose.yaml --env-file deploy/.env up -d --build`.
[`docs/operating.md`](docs/operating.md) has the full walk-through, every setting, the master key and the gateway
routing rules.

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

Feature 001 (federation core) is in progress: [`specs/001-federation-core`](specs/001-federation-core) holds its spec,
plan and tasks, and [`docs/vision.md`](docs/vision.md) the product vision. Expect changes until it is done.

## Related projects

- **Umbral**: a sign-in project. Megáfono does not depend on it.
- **Frunchy**: an F# app and the first intended consumer of Megáfono. Megáfono does not depend on it.
