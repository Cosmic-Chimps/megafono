# Quickstart: Blocks

Validation guide for 004. Contracts: [internal-api.md](contracts/internal-api.md),
[federation.md](contracts/federation.md), [app-contract.md](contracts/app-contract.md). Tables:
[data-model.md](data-model.md).

## Prerequisites

- .NET 10 SDK and Docker (Postgres and the interop servers run in containers).
- From the repository root: `dotnet build` and `dotnet build -c Release` both clean.

## Automated validation

| Command | Proves |
| --- | --- |
| `dotnet test --project tests/Megafono.Tests` | unit (domain matching, builders) and integration: the calls, follow removal, events, delivery filtering, inbox refusals, fetch 404s, collections, inbound `Block` (FR-001 to FR-013, SC-004 to SC-006) |
| `dotnet test --project tests/Megafono.Interop.Tests -- --filter-not-trait "Suite=mastodon"` | GoToSocial: SC-001, SC-002, SC-003 |
| `dotnet test --project tests/Megafono.Interop.Tests -- --filter-trait "Suite=mastodon"` | Mastodon: SC-001, SC-002, SC-003 |

## Manual walk-through (compose deployment, `deploy/`)

With `TOKEN` the internal bearer token and `alex` a user of the app:

1. **User block by handle**: `POST http://megafono:8081/internal/actors/alex/blocks` with
   `{"actor":"@bob@mastodon.example"}` → 201 and the block object. Bob's Mastodon shows no follow either way; the app
   received `follow.removed` per ended follow. Repeat the call → 200, same body, nothing new in the queues.
2. **Inbound drop**: bob replies to a public post of alex's → no `reply.received` reaches the app.
3. **Fetch 404**: a GET of alex's post signed by bob's key answers 404 (the integration tests sign such requests).
4. **Instance block**: `POST /internal/blocks` with `{"domain":"spam.example"}` → 201. A POST to `/inbox` signed by a
   spam.example key answers 403 and Megáfono makes no request to spam.example; a.spam.example is refused too.
5. **Remote block**: a Mastodon user who follows alex (and whom alex follows) blocks alex in Mastodon → within a minute
   `GET /internal/actors/alex/followers?status=removed` lists them, and the app received two `follow.removed`.
6. **Lift**: `DELETE /internal/actors/alex/blocks?actor=@bob@mastodon.example` → 204; Mastodon receives `Undo{Block}`;
   no follow comes back. `GET /internal/actors/alex/blocks` → empty list.

Expected failures: `{"actor":"@bob@down.example"}` while that server is down → 502 `remote_unreachable` (use the URL
form instead); `{"domain":"<the app's own domain>"}` → 400 `invalid_request`.
