# Operating Megáfono

Megáfono is one process with one Postgres database. A gateway is the only public service; your app sits behind it.
This page covers the reference deployment (`deploy/compose.yaml`), every setting, the keys you must back up, and the
rules any gateway must follow.

## Set it up in under 10 minutes

You need a host with Docker, a DNS name pointing at it, and a TLS certificate for that name (PEM files).

1. Copy the settings file and fill it in:

   ```sh
   cp deploy/.env.example deploy/.env
   ```

   Each variable in it has the command that generates it (`openssl rand -base64 32` for the master key,
   `openssl rand -hex 32` for the tokens). Set `DOMAIN` to the public host name, and `TLS_CERT_PATH` and
   `TLS_KEY_PATH` to the certificate chain and private key. The file is read by Compose only; keep it out of git.
2. **Back up the master key now** (see below).
3. Start everything:

   ```sh
   docker compose -f deploy/compose.yaml --env-file deploy/.env up -d --build
   ```

4. Check it from outside:

   ```sh
   curl -i "https://$DOMAIN/.well-known/webfinger?resource=acct:test@$DOMAIN"
   ```

   `https://$DOMAIN/u/test` is the sample app's profile page. From Mastodon, search `@test@$DOMAIN` and follow it.
   The sample's management pages (`/u/test/manage`) ask for the password `SAMPLE_ADMIN_PASSWORD`, with any user name.

The deployment has four services and two networks:

| Service | Network | Notes |
|---|---|---|
| `gateway` | `edge` | The only published port (443). TLS from the mounted certificate. |
| `megafono` | `edge`, `internal` | Federation port 8080 is routed by the gateway; the internal API (8081) listens only on Megáfono's address on `internal` (`172.31.0.10`) and your app reaches it there as `megafono-internal`. |
| `sample` | `edge`, `internal` | Your app's stand-in. Reaches the internal API over `internal`. |
| `db` | `internal` | Postgres 17, data in the `db-data` volume. |

`internal` is a Docker network with no route to the outside. Nothing publishes 8081, and the gateway has no route to it.
The internal API listener binds to `Megafono__Ports__InternalBindAddress`, which compose sets to Megáfono's fixed
address on `internal` (`172.31.0.10`), so a container on `edge` gets a refused connection on port 8081 and the bearer
token is a second lock, not the only one. The federation listener keeps `Megafono__Ports__BindAddress` (`0.0.0.0`). If you
run Megáfono outside this compose file, give the internal listener an address that only your app's network reaches
(or put the token's secrecy in charge of the boundary, knowing that every network the process joins can open the port).
The deployment test (`DeploymentShapeTests`) checks both: 8081 refuses connections from `edge`, and answers 401 without
the token from `internal`.

On a platform without private networks between containers (Caravana, for example, where every container on the host
can reach every other one), not routing 8081 is all the platform gives you: the bearer token is then the only lock on
the internal API. Use a long random token (`openssl rand -base64 32`), keep it out of logs, and rotate it if it leaks.

Megáfono creates its own schema at startup and answers `GET /health` (on the internal port) with `200` when the
database is reachable and `503` when it is not.

## Settings

Megáfono reads `Megafono__…` environment variables (double underscore for a nested key; `Megafono:App:Token` in
a JSON file) plus the connection string. A bad value stops startup with every problem listed.

| Setting | Required | Default | Meaning |
|---|---|---|---|
| `Megafono__Domain` | yes | | The public host name, without scheme or path (`app.example.com`). Actor ids are `https://{domain}/ap/actors/{id}`: changing it later orphans every follower. |
| `ConnectionStrings__Database` | yes | | Npgsql connection string of Megáfono's own database. |
| `Megafono__MasterKey` | yes | | Base64 of exactly 32 bytes. Encrypts every actor's private key at rest. |
| `Megafono__MasterKeyPrevious` | no | none | Only while rotating: still decrypts, never encrypts. Same format as the master key. |
| `Megafono__App__BaseUrl` | yes | | The app's private address, `http(s)://…` (not through the gateway). Megáfono calls `/megafono/*` there. |
| `Megafono__App__Token` | yes | | Bearer token: required on every `/internal/*` request and sent by Megáfono to the app. |
| `Megafono__App__WebhookSecret` | yes | | Signs webhooks (`Megafono-Signature`, HMAC-SHA256 of `"{t}.{body}"`). |
| `Megafono__Ports__Federation` | no | `8080` | Federation port, the one the gateway routes to. |
| `Megafono__Ports__Internal` | no | `8081` | Internal API port. Must differ from the federation port; never route it. |
| `Megafono__Ports__BindAddress` | no | `0.0.0.0` | Interface the federation port binds to (an IP address); also the internal port's, unless the next setting is given. |
| `Megafono__Ports__InternalBindAddress` | no | same as `BindAddress` | Interface the internal API port binds to (an IP address). Set it to the address Megáfono has on the network your app shares with it, so no other network can open the port. |
| `Megafono__Delivery__PerHost` | no | `2` | Concurrent deliveries to one remote host (at least 1). |
| `Megafono__Delivery__Global` | no | `32` | Concurrent deliveries overall (at least 1). |
| `Megafono__RateLimit__PerAddress` | no | `600` | Inbound requests per source address per window (at least 1). |
| `Megafono__RateLimit__PerHost` | no | `600` | Inbound requests per signing host per window (at least 1). |
| `Megafono__TrustedProxies` | no | none | Comma-separated addresses or CIDR ranges of proxies whose `X-Forwarded-For` is trusted (also as `Megafono__TrustedProxies__0`, `__1`…). Set it to the gateway's address (the compose file fixes it at `172.30.0.10`), or to its network's range when the address is not fixed ([behind a TLS proxy](#behind-a-tls-proxy-caravana)). Without it, every request counts as coming from the gateway and the per-address rate limit applies to the whole fediverse at once. |
| `Megafono__Software__Name` | no | `megafono` | Software name in NodeInfo; lowercase letters, digits and `-`. |
| `Megafono__Software__Version` | no | `0.1.0` | Software version in NodeInfo. |
| `Megafono__Federation__AllowPrivateAddresses` | no | `false` | **Tests only.** Lets outbound requests reach private addresses and plain http. Refused when `ASPNETCORE_ENVIRONMENT` is `Production`. |

The gateway's own settings are the standard ASP.NET ones: `Kestrel__Certificates__Default__Path` and `__KeyPath`
(PEM; with them it listens on 443), and `ReverseProxy__Clusters__{megafono|app}__Destinations__primary__Address`
for the two upstreams. One is its own: `Gateway__TrustedProxies` (none by default), the addresses or CIDR ranges of a
TLS proxy in front of the gateway whose `X-Forwarded-For` and `X-Forwarded-Proto` it believes (also as
`Gateway__TrustedProxies__0`, `__1`…; see [behind a TLS proxy](#behind-a-tls-proxy-caravana)). The sample app uses `MEGAFONO__URL`, `MEGAFONO__TOKEN`, `MEGAFONO__WEBHOOKSECRET`,
`APP__DOMAIN` and `SAMPLE__ADMINPASSWORD` (required outside Development).

## The master key

Every actor has an RSA-2048 key pair. Megáfono stores the private keys encrypted with `Megafono__MasterKey` and the
keys never leave it. **If you lose the master key you lose every actor**: remote servers have cached the public keys,
so the actors cannot be re-created under the same ids, and every follower relationship is cut.

- Generate it once: `openssl rand -base64 32`.
- Store a copy **apart from the database and its backups**: a password manager or a secrets store. A database backup
  that sits next to the key is a backup of every actor's private key.
- Back up the database as usual (`pg_dump`); it is useless without the key and the key is useless without it.
- To rotate: set `Megafono__MasterKeyPrevious` to the old key and `Megafono__MasterKey` to the new one; keys are
  re-encrypted with the new key as they are used. Remove the previous key once nothing needs it.

## Gateway routing rules

The reference gateway (`src/Megafono.Gateway`, YARP) follows these rules, and any other reverse proxy (Caddy, nginx,
Traefik) must too:

1. To Megáfono: exactly `/.well-known/webfinger` and `/.well-known/nodeinfo`, then `/nodeinfo/*`, `/ap/*` and
   `/inbox`. Nothing else under `/.well-known/` goes to Megáfono (ACME challenges and `host-meta` belong to the app).
2. Everything else goes to the app. There is no routing by `Accept` header: actor and object URLs live under
   `/ap/*`, and the app's profile page advertises the actor with a `rel=alternate` link.
3. Preserve the original `Host` header (YARP: `RequestHeaderOriginalHost: true`). Signatures cover it.
4. Do not rewrite the path, query or body on Megáfono routes. Signatures cover the path and the body digest.
5. Forward `X-Forwarded-Proto` and `X-Forwarded-For`.
6. **Never route the internal port (8081).** It is the app's control surface (publish, approve, block) behind one
   bearer token. Keep it on a network only the app and Megáfono share, and never publish it.

## Behind a TLS proxy (Caravana)

On a platform where a shared proxy terminates TLS and routes by host name only (Traefik on Caravana), the gateway
still owns the domain: the proxy sends the whole domain to the gateway, and the gateway splits the paths as above.
Run it without a certificate (it then listens on plain http, port 8080 in the image) and point the proxy at it.

The chain is client → proxy → gateway → Megáfono, and every hop sees only the previous one's address. The gateway
**replaces** `X-Forwarded-For` and `X-Forwarded-Proto` with what its own connection shows (YARP's default), so
without more settings Megáfono sees every client as the proxy's address and `http`, and the per-address rate limit
(FR-016) puts the whole fediverse in one bucket. Two settings fix it:

| Where | Setting | Value |
|---|---|---|
| gateway | `Gateway__TrustedProxies` | The address the proxy connects from, as the gateway sees it. On Caravana, Traefik connects from `10.99.0.1`. |
| Megáfono | `Megafono__TrustedProxies` | The gateway's address. Container addresses change on every deploy, so give the CIDR range of the network the gateway runs on (the one the platform assigns container addresses from). |

With `Gateway__TrustedProxies` set, the gateway takes the client's address from the proxy's `X-Forwarded-For` (the
right-most entry that is not itself a trusted proxy: what a client writes to its left is ignored) and the scheme from
its `X-Forwarded-Proto`, then forwards `X-Forwarded-For: <client>` and `X-Forwarded-Proto: https`. Megáfono, trusting
the gateway, counts each client in its own bucket. Headers from any other peer are ignored, as before. The `Host`
header is not taken from the proxy: it must reach the gateway unchanged, which Traefik does by default.

Trusting a whole range in Megáfono means any container in it that can reach the federation port can choose its own
rate-limit bucket. Keep that range to the platform's container network, never `0.0.0.0/0`.

For the platform's HTTP health check, use `GET /healthz` on port 8080: the gateway answers `200` with the body `ok`
itself and never proxies it, so it says the gateway is up and nothing about the app or Megáfono. Any other path,
`/health` included, is proxied as before (`/health` belongs to the app).

## Profile links

The app's profile pages must live on the same domain as Megáfono (as in the reference deployment, where the
gateway serves both). Mastodon ignores an actor's profile link on another host and links to the actor id instead.

## Directories and search (`discoverable`, `indexable`)

The app's actor lookup may send two optional booleans per user. `discoverable` (default `true`) says whether the user
may appear in profile directories and follow suggestions (on Mastodon: the "suggest account to others" and
directory settings); `indexable` (default `false`) says whether the user's public posts may be found by full-text
search. Both are requests: other servers may ignore them, as with followers-only (see "What followers-only means"). They
do not hide the actor: its document (name, summary, icon, `url`) and WebFinger stay public whatever the values, so an
app with private profiles should send a minimal profile (a neutral name and summary, no icon) for those users. A
change in either value is sent to followers as an `Update` of the actor when the app calls `PUT /internal/actors/{id}`.

## Replies and threads

Any post the app publishes can be a thread: people on other servers reply to it, the app's users reply too, and a
reader on Mastodon can load the whole thread. The wire shapes are in
[`specs/002-replies-threads/contracts/`](../specs/002-replies-threads/contracts/).

**What the app receives.** A reply to a post the app published, or to a reply already in one of its threads, becomes a
webhook for the author of the thread's root post, however deep the reply is:

- `reply.received` with the reply's id, `url`, sanitized `content`, `summary` (the content warning, as text) and
  `sensitive`, `language`, `published`, attachments as URLs, the `audience` (`public`, `unlisted`, `followers` or
  `direct`), the verified author, the `parent` it answers and the `thread` it belongs to. Hide the content behind the
  warning when `sensitive` is true; what to show for a `followers` or `direct` reply is the app's call.
- `reply.updated`, the same payload with `updated` set, when the author edits it.
- `object.deleted` with the reply's id and thread, when the author deletes it. Edits and deletes of replies Megáfono
  never recorded are ignored.

Megáfono records each reply's id, author, parent and audience, never its content: the app keeps what it needs from
the webhooks, which are retried until it accepts them.

**What the app sends.** Publish as usual with `object.inReplyTo` set to the `objectUrl` of one of the app's posts or
the `object.id` of a reply it received. Replies are `public` or `unlisted`; any other audience is `400`, an unknown
`inReplyTo` is `400` and a deleted one is `410`. The `201` answer adds `thread`. Megáfono addresses the reply to the
replier's followers, mentions the author of the post replied to when they are on another server and delivers it to
them. Edit and delete a reply with `PUT` and `DELETE /internal/objects/{id}`: they reach the same recipients.

**What to tell your users.**

- A reply the app hides or removes stays on other servers; Megáfono can only send a `Delete`, and servers may ignore it.
- A reply only reaches Megáfono when its author's server sends it to one of the app's actors: when the reply
  mentions or is addressed to that actor, or when that actor follows the reply's author. GoToSocial sends a reply to nobody else,
  so replies that do not mention the thread owner's account never arrive, and a thread seen on another server can
  hold replies the app never heard of. Each post's `replies` collection lists only what Megáfono has seen.
- Pasting the app's page URL into a search finds the thread post on **Mastodon only**, because the page links to its
  ActivityPub object. On GoToSocial paste the post's own URL (its `objectUrl`) or the account's handle.
- A `Group` actor (for example one account per series) is a plain account: it is found, followed and posts like a
  person. Megáfono does not boost its followers' replies, so it is not a Lemmy-style community.

## Deleting a user

When a user deletes their account, the app calls `DELETE /internal/actors/{id}` with the user's immutable id
(contract in [`specs/003-actor-deletion/contracts/`](../specs/003-actor-deletion/contracts/)). The call does not ask
the app's actor lookup, so it works before or after the app removes the user. It answers `202` with the number of
servers told, the same answer on every repeat, or `404` when Megáfono never had an actor for the id (nothing to do).

- Megáfono sends one signed `Delete` of the actor to every server that follows the user, asked to, or is followed by
  them, one per server (shared inbox), retried for 48 hours like any delivery. Mastodon and GoToSocial then remove
  the account, its posts and its follows.
- From that moment the actor id, its collections and its public posts answer `410 Gone`; followers-only posts answer
  `404`. The id is never served again, even if the app's lookup keeps answering it. The handle is free for a new user
  with a different id.
- Queued deliveries and webhooks of the user are cancelled, and no webhook is sent about them afterwards. Calls for
  the user answer `410 gone`.
- Megáfono erases at once the follow relationships, the profile and its copies of the posts, and erases the key pair
  once every `Delete` delivery has ended (housekeeping checks every hour). Only a tombstone (id, handle, type,
  dates) remains, so the id can never be reused.

**What to tell your users.** A deletion cannot recall what other servers already showed. A server that was down for
more than 48 hours, or that only received a reply or a mention and has no follow relationship with the user, gets no
`Delete`; it learns of the deletion when it next fetches the account or a post (`410`). Servers other than Mastodon
and GoToSocial may keep copies.

## Blocks

Two kinds of block, both through the internal API (contract in
[`specs/004-blocks/contracts/`](../specs/004-blocks/contracts/)):

- **A user block** covers one of your users: `POST /internal/actors/{id}/blocks` with `{"actor": "..."}` or
  `{"domain": "spam.example"}`. List with `GET`, lift with `DELETE …/blocks?actor=` or `?domain=`.
- **An instance block** covers every user: `POST /internal/blocks` with `{"domain": "..."}` (the usual case) or
  `{"actor": "..."}`. List with `GET /internal/blocks`, lift with `DELETE /internal/blocks?domain=` or `?actor=`.

`actor` is the remote actor's URL (as webhooks and follower lists give it) or its handle (`@bob@spam.example`). A
handle needs the remote server to answer once, to find the actor; a URL never does, so block by URL when the server is
down. A domain block also covers every subdomain (`spam.example` covers `a.spam.example`). Every call is idempotent: a
repeat answers the existing block (`200`) and does nothing more.

What a block does, at once and in one step:

- Every follow between the covered users and the blocked side ends, in both directions, pending or accepted. Your app
  gets one `follow.removed` webhook per ended follow (`follow.direction` says whose follow it was). Lifting a block
  restores none of them.
- Deliveries to the blocked side are cancelled and none are sent afterwards.
- Anything the blocked side sends is dropped before it can become a webhook: follow requests, replies (no
  `reply.received`), edits. For an instance block, its servers get `403` before Megáfono fetches a key or reads the
  request; for a user block, Megáfono answers as if it accepted the activity, so the blocked actor is not told.
- Fetches signed by the blocked side answer `404`, even for public posts, and the blocked side's replies disappear
  from your users' reply collections (they come back if you lift the block). Megáfono itself fetches nothing from an
  instance-blocked domain, and following or looking up a blocked actor answers `409 blocked`.

What the other side is told:

- **A user block of an actor** sends one signed `Block` to that actor, as Mastodon and GoToSocial do; their servers end
  the follows on their side too. The blocked actor's server learns of the block. Lifting it sends `Undo` of the
  `Block`.
- **A user block of a domain** sends a `Reject` of each ended follow from that domain and an `Undo` of each follow of
  your user's there, so those servers drop the follows too. No `Block` is sent.
- **An instance block** sends nothing. The blocked servers may keep showing old follows on their side; they simply
  stop reaching anyone here.

When a remote user blocks one of your users, their server sends a `Block`: Megáfono ends the follows between them and
your app gets the `follow.removed` webhooks. Mastodon first rejects your user's follow of the blocker, so that one
arrives as `follow.rejected`. Megáfono keeps no record of the block; the blocker's server refuses new follows while
its block lasts.

**Limits to tell your users.** Most servers (Mastodon and GoToSocial included) sign their fetches with a server-wide
key, not the user's own, so a user block of one actor cannot catch those fetches: only a block of the whole domain
does. Unsigned fetches of public posts cannot be attributed to anyone and are still answered. And a block stops what
comes next: what a server already received, it keeps.

## What followers-only means

Followers-only (`followers`) is a request, not a guarantee. Megáfono sends such a post only to the inboxes of the
user's followers and answers `404` to everyone else who asks for it. But once the post is delivered, the receiving
server holds a copy, and **it can show that post to anyone**: a server may ignore the audience, a follower may copy it
or boost it, and an admin can read it. Treat a followers-only post as one you would be willing to have seen by any
server that follows you, and tell your users the same.
