# Changelog

What changed in each release, for the people who run Megáfono and the apps that integrate it. Newest
first. A release's notes on GitHub are its section here: `.github/scripts/changelog.sh notes <version>`
prints it, and the release workflow refuses a version when there is nothing under Unreleased.

Each section has the changes that matter to an operator or an integrating app, in this order and only
as needed: Added, Changed, Fixed, Security, then Upgrade when an upgrade needs more than pulling the new
images. Add to "Unreleased" as you work; a release renames it.

## Unreleased

## 0.2.0 - 2026-10-24

### Added
- Posts from followed accounts (feature 005): when a local user follows a remote account, that
  account's new, edited and deleted Notes and Articles reach the app as `post.received`,
  `post.updated` and `post.deleted`, one event per following user. Followers-only and direct posts
  go only to the users they were sent to; nothing is delivered from blocked authors or domains.
  Megáfono keeps no copy of the content. `Announce` (boosts) stays unhandled.
- The app's actor lookup may send `discoverable` and `indexable` (optional booleans) and the actor
  document carries them, so an app can keep non-public users out of fediverse directories and
  full-text search. Absent, they keep the old fixed values (`true`, `false`); a change goes out to
  followers with the next profile `Update`.
- `GET /healthz` on the gateway, answered by the gateway itself and never proxied, for a platform's
  HTTP health check. `/health` still reaches the app.
- `Gateway__TrustedProxies`: behind a TLS-terminating proxy (Traefik on Caravana) the gateway takes
  the client address and scheme from that proxy's `X-Forwarded-*` headers, so Megáfono's per-address
  rate limits see each client rather than the proxy. `docs/operating.md`, "Behind a TLS proxy".
- `deploy/dev-fediverse/`: a local fediverse for integration work: Caddy with a private CA for
  `*.test` domains, GoToSocial, Mastodon as an optional profile, the gateway, Megáfono and the
  sample app, with a setup script and a walk-through.

### Upgrade
- Nothing to do: the schema adds its new tables and columns at startup.

## 0.1.0 - 2026-10-17

### Added
- Federation core (001): WebFinger, NodeInfo, actor documents, one RSA key pair per actor encrypted
  at rest, HTTP Signatures both ways, approved follows in both directions, publishing Notes and
  Articles to `public`, `unlisted` and `followers` audiences, durable idempotent delivery with
  shared inboxes, signed webhooks. The app integrates through an actor lookup, a webhook receiver and
  the internal API on a separate port.
- Replies and threads (002): remote replies as `reply.received`, `reply.updated` and
  `object.deleted`, outbound replies, `replies` collections, Group actors, content warnings.
- Actor deletion (003): `DELETE /internal/actors/{id}` sends `Delete` to every follow-related server;
  the id and its objects answer 410 forever and are never reused.
- Blocks (004): per-user and per-instance blocks of an actor or a domain, with `follow.removed`
  events and the `blocked` error code.
- Handles may contain inner dashes.
- The internal API can bind to its own address (`Megafono__Ports__InternalBindAddress`), so a
  network the gateway shares cannot reach it.
- Multi-arch images `ghcr.io/cosmic-chimps/megafono` and `ghcr.io/cosmic-chimps/megafono-gateway`,
  MIT licensed.
