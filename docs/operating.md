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
| `megafono` | `edge`, `internal` | Federation port 8080 is routed by the gateway; the internal API (8081) is used by your app over `internal`. |
| `sample` | `edge`, `internal` | Your app's stand-in. Reaches the internal API over `internal`. |
| `db` | `internal` | Postgres 17, data in the `db-data` volume. |

`internal` is a Docker network with no route to the outside. Nothing publishes 8081, and the gateway has no route to it.
Be aware that Megáfono listens on every interface of the networks it joins, so a container on `edge` can open a
connection to `megafono:8081`; what stops it is the bearer token (`/internal/*` answers 401 without it, and nothing
else is served on that port). Keep other containers off `edge` and keep the token secret. The deployment test
(`DeploymentShapeTests`) checks exactly this.

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
| `Megafono__Ports__BindAddress` | no | `0.0.0.0` | Interface both ports bind to (an IP address). |
| `Megafono__Delivery__PerHost` | no | `2` | Concurrent deliveries to one remote host (at least 1). |
| `Megafono__Delivery__Global` | no | `32` | Concurrent deliveries overall (at least 1). |
| `Megafono__RateLimit__PerAddress` | no | `600` | Inbound requests per source address per window (at least 1). |
| `Megafono__RateLimit__PerHost` | no | `600` | Inbound requests per signing host per window (at least 1). |
| `Megafono__TrustedProxies` | no | none | Comma-separated addresses of proxies whose `X-Forwarded-*` headers are trusted (also as `Megafono__TrustedProxies__0`, `__1`…). Set it to the gateway's address (the compose file fixes it at `172.30.0.10`). Without it, every request counts as coming from the gateway and the per-address rate limit applies to the whole fediverse at once. |
| `Megafono__Software__Name` | no | `megafono` | Software name in NodeInfo; lowercase letters, digits and `-`. |
| `Megafono__Software__Version` | no | `0.1.0` | Software version in NodeInfo. |
| `Megafono__Federation__AllowPrivateAddresses` | no | `false` | **Tests only.** Lets outbound requests reach private addresses and plain http. Refused when `ASPNETCORE_ENVIRONMENT` is `Production`. |

The gateway's own settings are the standard ASP.NET ones: `Kestrel__Certificates__Default__Path` and `__KeyPath`
(PEM; with them it listens on 443), and `ReverseProxy__Clusters__{megafono|app}__Destinations__primary__Address`
for the two upstreams. The sample app uses `MEGAFONO__URL`, `MEGAFONO__TOKEN`, `MEGAFONO__WEBHOOKSECRET`,
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

## Profile links

The app's profile pages must live on the same domain as Megáfono (as in the reference deployment, where the
gateway serves both). Mastodon ignores an actor's profile link on another host and links to the actor id instead.

## What followers-only means

Followers-only (`followers`) is a request, not a guarantee. Megáfono sends such a post only to the inboxes of the
user's followers and answers `404` to everyone else who asks for it. But once the post is delivered, the receiving
server holds a copy, and **it can show that post to anyone**: a server may ignore the audience, a follower may copy it
or boost it, and an admin can read it. Treat a followers-only post as one you would be willing to have seen by any
server that follows you, and tell your users the same.
