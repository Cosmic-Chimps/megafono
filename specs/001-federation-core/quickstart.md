# Quickstart: validating Federation Core

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md)

How to prove the feature works, from the fastest loop to a real deployment. Wire shapes are in
[contracts/](contracts/); the data model is in [data-model.md](data-model.md).

## Prerequisites

- .NET SDK 10 (`global.json` pins it and selects Microsoft.Testing.Platform).
- Docker (Testcontainers starts Postgres, Caddy, GoToSocial and Mastodon).
- For the deployment check only: a host with a public domain, TLS and ports 80/443 open.

## 1. Unit and integration tests (every change)

```bash
dotnet build
dotnet test --project tests/Megafono.Tests
```

Runs against a real Postgres container and a real Kestrel host with a fake app. Expected: all green.
It covers, among others:

- signature parsing, signing strings, digest, date window (unit);
- addressing per audience and the followers-only fetch rule (SC-002), including unsigned,
  non-follower, removed-follower and instance-actor requests;
- publish idempotency: 10 retries with one key → one object, one delivery per inbox (SC-005);
- the delivery queue: backoff schedule with a fake clock, 48 h give-up, 7-day unreachable host,
  per-host and global limits, clean-stop drain, and the hard-kill fault hook (SC-004);
- app events: never dropped, `seq` per user, retried after the fake app returns 500;
- port separation: `/internal/*` 404 on the federation port, bearer required on the internal port;
- rate limiting: 429 with `Retry-After` past the limit, no key fetch;
- SSRF: handle resolution to a private address refused.

## 2. Interop suite against GoToSocial (every change)

```bash
dotnet test --project tests/Megafono.Interop.Tests
```

Starts a test CA, Caddy fronts for `megafono.test` and `gts.test`, GoToSocial, and Megáfono with
the fake app on host ports. Expected, each as an automated test (SC-001, SC-008):

1. GTS searches `@alex@megafono.test` and shows Alex's name and avatar.
2. GTS follows a locked user → fake app gets `follow.requested`; approve → GTS shows the follow as
   accepted. Same with reject, and with an unlocked user (`follow.accepted` without approval).
3. Publish followers-only → appears in the GTS follower's home timeline within 60 s as
   followers-only; a second GTS account that does not follow gets 404 fetching the object URL.
4. Publish public, edit, delete → GTS shows the edit, then the post disappears; the URL answers 410.
5. Alex follows a GTS account; it accepts; it follows back → both lists show `mutual: true`.
6. A tampered body or stale `Date` sent to the inbox → 401 and no event.

## 3. Interop suite against Mastodon (scheduled and before release)

```bash
dotnet test --project tests/Megafono.Interop.Tests -- --filter-trait "Suite=mastodon"
```

Same scenarios against Mastodon 4.x with `AUTHORIZED_FETCH=true`, plus: Mastodon shows the
followers-only post with the lock icon, and pasting `https://megafono.test/u/alex` (the sample
app's profile, which carries the `rel=alternate` link) finds Alex. Expected run time 6–12 min.

## 4. Throughput (before release)

```bash
dotnet test --project tests/Megafono.Tests -- --filter-trait "Suite=load"
```

A stub fleet of 200 inbox hosts with 1,000 followers; expected: every host receives the Create
within 2 minutes on a 2-vCPU limit (SC-003).

## 5. Real deployment (US5, SC-006)

```bash
cp deploy/.env.example deploy/.env       # set DOMAIN, MEGAFONO_MASTER_KEY, MEGAFONO_TOKEN, MEGAFONO_WEBHOOK_SECRET, TLS cert paths
docker compose -f deploy/compose.yaml up -d
curl -s "https://$DOMAIN/.well-known/webfinger?resource=acct:test@$DOMAIN"
```

Expected: the WebFinger JRD for the sample app's `test` user. Then, from any Mastodon account,
search `@test@$DOMAIN` and follow; the sample app's page shows the follower. Time it from
`docker compose up`: under 10 minutes. Check that the internal port is not reachable from outside:
`curl -m 5 https://$DOMAIN:8081/health` must fail.

The same shape runs automatically, against a real GoToSocial, with the images built from `deploy/Dockerfile.*`
(about a minute with warm image caches; it is `Explicit`, so a plain `dotnet test` skips it, and the nightly
workflow runs it):

```bash
dotnet test --project tests/Megafono.Interop.Tests -- --explicit on --filter-trait "Suite=deployment"
```

## 6. Integration size (SC-007)

```bash
scripts/count-integration-lines.sh samples/SampleApp
```

Expected: under 100 lines of integration code (actor lookup, event receiver, internal API calls),
excluding the sample's own UI.
