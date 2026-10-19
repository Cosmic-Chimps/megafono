# A local fediverse for development

This folder starts a small fediverse on your machine, so an app that integrates Megáfono can try real federation:
follow from a remote account, approve, publish, reply, block, delete. It runs Megáfono behind its gateway, the sample
app (standing in for your app), GoToSocial and, if you want it, Mastodon. One Caddy serves all of them over HTTPS on
`app.test`, `gts.test` and `mastodon.test`, signed by its own local CA, and every container reaches those names
through Caddy, the way a remote server would. Nothing leaves your machine.

**Development only.** Megáfono runs with `ASPNETCORE_ENVIRONMENT=Development` and
`Megafono__Federation__AllowPrivateAddresses=true` (outbound requests may reach private addresses and plain http), and
its internal API is published on `127.0.0.1:8081`. None of that is acceptable in production, and
`deploy/compose.yaml` does none of it. For a real deployment follow `deploy/compose.yaml` and
[`docs/operating.md`](../../docs/operating.md).

| Service | Name | Port | Notes |
|---|---|---|---|
| `caddy` | `app.test`, `gts.test`, `mastodon.test` | 443 (published) | TLS with its own CA; the only public port |
| `gateway` | `app.test` behind Caddy | 8080 | The reference YARP gateway, plain http |
| `megafono` | | 8080 federation, 8081 internal | 8081 is published on `127.0.0.1` only |
| `sample` | `app.test` behind the gateway | 8080 | Users `alice` (locked) and `test` |
| `db` | | 5432 | Megáfono's Postgres |
| `gotosocial` | `gts.test` | 8080 | 0.22.2, SQLite |
| `mastodon-*` | `mastodon.test` | 3000, 4000 | Only with `--profile mastodon`: web, sidekiq, streaming, Postgres, Redis |

The Compose project is `megafono-dev`, on one network of the same name (`172.29.0.0/24`).

## Quick start

You need Docker with Compose, `curl` and `openssl`. Port 443 on the host must be free.

```sh
cd deploy/dev-fediverse
./setup.sh                  # add --mastodon to start Mastodon too; --user NAME changes the remote user (default bob)
```

`setup.sh` generates `.env` (never overwritten: its master key belongs to the database volume), builds and starts
everything, exports Caddy's root certificate to `caddy-root.crt`, waits for the services, creates the user on
GoToSocial, gets a token for it (saved in `.gts-token`) and prints a summary. With `--mastodon` it also creates the
Mastodon user and prints its password. Running it again is safe.

Then, once:

1. Make the names resolve:

   ```sh
   echo "127.0.0.1 app.test gts.test mastodon.test" | sudo tee -a /etc/hosts
   ```

2. Trust the CA (the script prints these with the right path; it never runs `sudo` itself). Firefox keeps its own
   store: import `caddy-root.crt` in its settings.

   ```sh
   # macOS
   sudo security add-trusted-cert -d -r trustRoot -k /Library/Keychains/System.keychain "$PWD/caddy-root.crt"
   # Debian, Ubuntu
   sudo cp caddy-root.crt /usr/local/share/ca-certificates/megafono-dev.crt && sudo update-ca-certificates
   # Fedora, Arch
   sudo trust anchor --store caddy-root.crt
   ```

3. Open `https://app.test/u/alice` (the sample's profile) and `https://app.test/u/alice/manage` (any user name, the
   password is `SAMPLE_ADMIN_PASSWORD` in `.env`). GoToSocial is at `https://gts.test`; Mastodon at
   `https://mastodon.test`.

To start from zero: `docker compose down -v`, then delete `.env` and `caddy-root.crt` and remove the old CA from your
trust store. `down` without `-v` keeps everything, including the follows. The volumes belong to the project name
`megafono-dev`, not to this folder, so they are shared by every checkout: `setup.sh` refuses to make a new `.env` while
`megafono-dev_db-data` exists (a new master key could not read the keys stored there) and prints what to do.

To use the published images instead of building: `./setup.sh --published`, or
`docker compose -f compose.yaml -f compose.published.yaml up -d`. `MEGAFONO_VERSION` in `.env` picks the tag. The
packages may be private until they are made public (`docker login ghcr.io` first). The sample app is always built.

## Walk-through

GoToSocial has no web page for posting, so the steps below use its Mastodon-compatible API with curl (and `jq` to
read the answers), after the `/etc/hosts` line and the CA trust of the quick start. A Mastodon
profile (`--mastodon`) gives you a full web UI for the same steps. `bob` is the default user of `setup.sh`:

```sh
cd deploy/dev-fediverse
set -a; . ./.env; set +a              # MEGAFONO_TOKEN and the other settings
TOKEN=$(cat .gts-token)
GTS=https://gts.test
INTERNAL=http://127.0.0.1:8081
ALICE=8d2e5b1c-4a6f-4e3d-9c7b-1f0a2b3c4d5e    # the sample's locked user (samples/SampleApp/Users.fs)
auth=(-H "Authorization: Bearer $TOKEN")
```

**1. Find alice from GoToSocial.**

```sh
curl -s "${auth[@]}" "$GTS/api/v2/search?q=@alice@app.test&resolve=true&type=accounts" | jq '.accounts[0] | {id, acct, locked}'
ID=<the id from the answer>
```

**2. Follow her.** Alice is locked, so the follow waits.

```sh
curl -s "${auth[@]}" -X POST "$GTS/api/v1/accounts/$ID/follow" | jq '{following, requested}'
```

**3. Approve.** Open `https://app.test/u/alice/manage`: the request is under "Pending follow requests". Approve it. The
"Events received" list shows `follow.requested`. Then:

```sh
curl -s "${auth[@]}" "$GTS/api/v1/accounts/relationships?id[]=$ID" | jq '.[0] | {following, requested}'
```

shows `following: true`.

**4. Publish.** On the manage page choose the audience and publish a post. Bob follows alice now, so `followers`
reaches him as well as `public`. Read it on bob's home timeline:

```sh
curl -s "${auth[@]}" "$GTS/api/v1/timelines/home" | jq '.[0] | {id, content}'
STATUS=<the status id>
```

**5. Reply.** Mention alice: a reply only reaches Megáfono when it mentions her or when she follows its author
(`docs/operating.md`, "Replies and threads").

```sh
curl -s "${auth[@]}" -X POST "$GTS/api/v1/statuses" \
  --data-urlencode "status=@alice@app.test that is great" \
  --data-urlencode "in_reply_to_id=$STATUS" | jq '{id, uri}'
```

The manage page lists `reply.received`. To answer from alice, publish through the internal API with `inReplyTo` set
to the reply's `object.id` (shown in the event; Megáfono accepts only the app's own posts and replies it received).
Replies are `public` or `unlisted` ([contract](../../specs/002-replies-threads/contracts/internal-api.md)):

```sh
curl -s -X POST "$INTERNAL/internal/actors/$ALICE/objects" \
  -H "Authorization: Bearer $MEGAFONO_TOKEN" -H 'Content-Type: application/json' -H 'Idempotency-Key: dev-reply-1' \
  -d '{"audience":"public","object":{"type":"Note","content":"<p>Thanks!</p>","url":"https://app.test/u/alice","inReplyTo":"<object.id of the reply>"}}'
```

**Follow outward.** A Megáfono user can follow bob too (`target` is a handle or an actor URL; the answer is `202`
with `status: pending`). Accounts made with GoToSocial's admin command are locked, so bob approves the request; `$ID`
is alice's account id on GoToSocial from step 1. The manage page then shows `follow.accepted`:

```sh
curl -s -X POST "$INTERNAL/internal/actors/$ALICE/following" \
  -H "Authorization: Bearer $MEGAFONO_TOKEN" -H 'Content-Type: application/json' -d '{"target":"@bob@gts.test"}'
curl -s "${auth[@]}" -X POST "$GTS/api/v1/follow_requests/$ID/authorize" | jq '{followed_by}'
```

**6. Block.** Alice blocks bob. The follows between them end, in both directions, and the manage page shows one
`follow.removed` per follow. GoToSocial receives the `Block` (`relationships` shows `blocked_by: true`):

```sh
curl -s -i -X POST "$INTERNAL/internal/actors/$ALICE/blocks" \
  -H "Authorization: Bearer $MEGAFONO_TOKEN" -H 'Content-Type: application/json' -d '{"actor":"@bob@gts.test"}'
```

Lift it with `DELETE "$INTERNAL/internal/actors/$ALICE/blocks?actor=%40bob%40gts.test"`. Lifting does not restore the
follows. Blocking a whole domain (`{"domain":"gts.test"}`) or the instance (`POST /internal/blocks`) works the same
way ([`docs/operating.md`](../../docs/operating.md#blocks)).

**7. Delete.** **This is permanent for that id**: actor ids are never reused, so alice answers `410 Gone` afterwards,
even though the sample still knows her. Do it on `test` (`3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10`) if you only want to
watch it, or reset everything with `docker compose down -v`:

```sh
curl -s -i -X DELETE "$INTERNAL/internal/actors/3f6c2a9e-1b7d-4c55-9b0e-7a2f4f1d9c10" -H "Authorization: Bearer $MEGAFONO_TOKEN"
```

The answer is `202` with `deliveries`, the number of servers told. Servers that know the user get a `Delete` and
remove the account, and `https://app.test/ap/actors/<id>` answers `410`.

### Checked

Run by hand once on 2026-10-18 (macOS, Docker Desktop, arm64, images built from the checkout), with curl's `--resolve`
and `--cacert caddy-root.crt` instead of `/etc/hosts` and a trusted CA. Every step answered as described above:

- `./setup.sh`: `.env`, stack, CA export, `bob@gts.test` and its token in 31 s (with the .NET build cached). Run again
  with `--mastodon`: `.env` kept, bob found existing, a fresh token, Mastodon healthy and `bob@mastodon.test` created
  in 43 s.
- GoToSocial: found `@alice@app.test` (locked), follow request, `follow.requested`, approved on the manage page,
  `following: true`, `follow.accepted`; a followers post on bob's home timeline; bob's reply as `reply.received`;
  alice's answer (`inReplyTo`) as a mention for bob; alice's follow of bob, approved by bob; the block (`blocked_by:
  true` on GoToSocial, two `follow.removed`), lifted with `204`; `test` deleted (`202`, `deliveries: 1`, actor `410`).
- Mastodon (`AUTHORIZED_FETCH=true`): resolved `@alice@app.test`, its follow request reached the manage page, and after
  the approval Mastodon shows bob following alice.

The interop suite (`tests/Megafono.Interop.Tests`) stays the regression net; this folder is not run in CI.

## Pointing a real app at it

Your app implements the contract: `GET /megafono/actors?handle=`, `GET /megafono/actors/{id}` and
`POST /megafono/webhooks` (see `samples/SampleApp/Megafono.fs` and
[`specs/001-federation-core/contracts/`](../../specs/001-federation-core/contracts/)). Its public domain is `app.test`.

**An app on the host.** Set its address in `.env`, as the containers see it:

```sh
APP_URL=http://host.docker.internal:5000
```

`APP_URL` feeds the gateway's `app` destination and `Megafono__App__BaseUrl`. Then `docker compose up -d`, and
optionally `docker compose stop sample`. Configure your app with Megáfono's URL `http://127.0.0.1:8081`, the
`MEGAFONO_TOKEN` and `MEGAFONO_WEBHOOK_SECRET` from `.env`, and the public domain `app.test`. Your app needs no HTTPS of
its own: the gateway reaches it over plain http. `host.docker.internal` works on Docker Desktop and, through the
`extra_hosts` entry, on Linux.

**An app in a container.** Attach it to the external network `megafono-dev` (`networks: default: {name: megafono-dev,
external: true}` in its compose file), and set `APP_URL=http://<container name>:<port>`. The app reaches Megáfono as
`http://megafono:8081`.

## Running it from an Aspire AppHost

**Option 1, recommended.** Keep this compose running and let the AppHost run only your app, on a fixed port, with
`APP_URL=http://host.docker.internal:<that port>` in `.env`. The AppHost then needs nothing from this folder, and the
fediverse survives AppHost restarts.

**Option 2.** The AppHost runs the same containers. This is a sketch, not tested here:

```csharp
var caddy = builder.AddContainer("caddy", "docker.io/library/caddy", "2.11.7-alpine")
    .WithBindMount("./dev-fediverse/Caddyfile", "/etc/caddy/Caddyfile", isReadOnly: true)
    .WithVolume("caddy-data", "/data")
    .WithEndpoint(port: 443, targetPort: 443, scheme: "https", name: "https")
    .WithContainerRuntimeArgs("--network-alias", "app.test", "--network-alias", "gts.test", "--network-alias", "mastodon.test")
    .WithLifetime(ContainerLifetime.Persistent);

builder.AddContainer("gotosocial", "docker.io/superseriousbusiness/gotosocial", "0.22.2")
    .WithEnvironment("GTS_HOST", "gts.test") // and the other GTS_* settings of compose.yaml
    .WithEnvironment("SSL_CERT_FILE", "/ca/root.crt")
    .WithVolume("gts-data", "/gotosocial/storage")
    .WithLifetime(ContainerLifetime.Persistent)
    .WaitFor(caddy);
```

What has to stay the same, wherever it runs: `app.test`, `gts.test` and `mastodon.test` resolve to Caddy inside the
network; every container that calls them trusts Caddy's root through `SSL_CERT_FILE` (copy it out of Caddy's storage
first, as the `ca` service does); Megáfono runs in `Development` with `AllowPrivateAddresses`; GoToSocial and Mastodon
allow the network's addresses (`GTS_HTTP_CLIENT_ALLOW_IPS`, `ALLOWED_PRIVATE_ADDRESSES`); and the gateway and Megáfono
trust the address Caddy connects from (`Gateway__TrustedProxies`, `Megafono__TrustedProxies`).

## Troubleshooting

- **GoToSocial answers 503 for the first seconds.** It runs migrations and compiles its WASM modules. `setup.sh` waits
  for a 200; by hand, repeat the request. `docker compose logs gotosocial` shows progress.
- **`dial within blocked / reserved IP range` in GoToSocial's logs.** `GTS_HTTP_CLIENT_ALLOW_IPS` must contain the
  network (`172.29.0.0/24`). Mastodon's equivalent is `ALLOWED_PRIVATE_ADDRESSES`.
- **Certificate errors.** The CA is not exported or not trusted, or it changed: `docker compose down -v` (or removing
  only `caddy-data`) creates a new CA. Run `./setup.sh` again: it re-exports the root (and says when it changed), the
  `ca` service copies it again for the containers, and you trust the new file.
- **Port 443 is in use.** Stop whatever holds it (`lsof -iTCP:443 -sTCP:LISTEN`). Changing the host side of the
  `caddy` port only affects your browser and curl (the containers reach Caddy on 443 inside the network), but
  `setup.sh` expects 443.
- **Mastodon is slow the first time.** `db:prepare` loads the schema and the web container needs a few minutes to
  become healthy; `setup.sh --mastodon` waits up to ten minutes. `docker compose --profile mastodon logs mastodon-web`.
- **A reply never shows up in the manage page.** It must mention alice (`@alice@app.test`) or come from someone she
  follows. See "Replies and threads" in `docs/operating.md`.
- **`docker compose exec` says a Mastodon service does not exist.** Add `--profile mastodon` to the command.
