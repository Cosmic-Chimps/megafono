/// Blocks (specs/004-blocks): domain normalization and matching (R-42), the SQL fragments that say whether a remote
/// actor, a host or an inbox is covered by a block, and the small queries the rest of the service asks. A block is
/// a user's (`local_actor` set) or the instance's (`local_actor` NULL); it names one remote actor or one domain.
/// Compiled before `RemoteActors.fs`, which refuses to fetch from an instance-blocked host.
module Megafono.Infrastructure.Blocks

open System
open System.Globalization
open System.Threading.Tasks
open Npgsql

let private idn = IdnMapping(AllowUnassigned = false, UseStd3AsciiRules = true)

let private validLabel (label: string) =
    label.Length >= 1
    && label.Length <= 63
    && label |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '-')
    && label[0] <> '-'
    && label[label.Length - 1] <> '-'

/// A domain as stored: trimmed, one trailing dot removed, lowercase, internationalized names as punycode. None for
/// anything that is not a bare host name: a port, a scheme, a path, a user, spaces or a label breaking the host name rules.
let normalizeDomain (input: string) : string option =
    if isNull input then
        None
    else
        let trimmed = input.Trim()
        let trimmed = if trimmed.EndsWith "." then trimmed.Substring(0, trimmed.Length - 1) else trimmed

        if trimmed = "" || trimmed |> Seq.exists (fun c -> c = ':' || c = '/' || c = '?' || c = '#' || c = '@' || c = '\\' || Char.IsWhiteSpace c) then
            None
        else
            try
                let ascii = idn.GetAscii(trimmed.ToLowerInvariant()).ToLowerInvariant()

                if ascii.Length <= 253 && ascii.Split('.') |> Array.forall validLabel then Some ascii else None
            with :? ArgumentException ->
                None

/// The host name of an authority (`host[:port]`, as stored in `remote_actors.host` and `deliveries.host`), lowercase, no port
let hostName (authority: string) : string =
    let lower = authority.ToLowerInvariant()
    let colon = lower.IndexOf ':'
    if colon < 0 then lower else lower.Substring(0, colon)

/// True when the host (`host[:port]`) is the blocked domain or one of its subdomains; the port is ignored
let domainMatches (domain: string) (host: string) : bool =
    let name = hostName host
    name = domain || name.EndsWith("." + domain)

/// The SQL test of data-model.md "Matching": the host `hostExpr` (`host[:port]`) is the domain `domainExpr` or one of
/// its subdomains. The expressions are SQL (a parameter or a column).
let hostInDomainSql (domainExpr: string) (hostExpr: string) =
    $"(split_part({hostExpr}, ':', 1) = {domainExpr} OR right(split_part({hostExpr}, ':', 1), length({domainExpr}) + 1) = '.' || {domainExpr})"

let private domainMatchSql (hostExpr: string) = hostInDomainSql "b.domain" hostExpr

/// `EXISTS (…)` true when a block covers the remote actor `actorIdExpr` living on `hostExpr`, for the local user
/// `localActorExpr`: the user's own blocks and the instance's. Pass `NULL` as `localActorExpr` to look at instance
/// blocks only, and `NULL` as `actorIdExpr` to match by host alone. The expressions are SQL (a parameter or a column).
let coveredSql (localActorExpr: string) (actorIdExpr: string) (hostExpr: string) : string =
    $"""EXISTS (SELECT 1 FROM blocks b
              WHERE (b.local_actor IS NULL OR b.local_actor = {localActorExpr})
                AND (b.actor = {actorIdExpr} OR (b.domain IS NOT NULL AND {domainMatchSql hostExpr})))"""

/// `EXISTS (…)` true when a block covers a delivery to `inboxExpr` on `hostExpr` for the local user `localActorExpr`:
/// a domain block by the host, an actor block only for that actor's own inbox (a shared inbox serves others too).
/// `NULL` as `localActorExpr` looks at instance blocks only.
let deliveryCoveredSql (localActorExpr: string) (inboxExpr: string) (hostExpr: string) : string =
    $"""EXISTS (SELECT 1 FROM blocks b
              WHERE (b.local_actor IS NULL OR b.local_actor = {localActorExpr})
                AND ((b.domain IS NOT NULL AND {domainMatchSql hostExpr})
                     OR (b.actor IS NOT NULL AND EXISTS (SELECT 1 FROM remote_actors r WHERE r.id = b.actor AND r.inbox = {inboxExpr}))))"""

let private scalarBool (command: NpgsqlCommand) : Task<bool> =
    task {
        let! value = command.ExecuteScalarAsync()
        return (match value with :? bool as b -> b | _ -> false)
    }

/// True when an instance block covers this remote actor id on this host (`host[:port]`)
let isInstanceBlocked (dataSource: NpgsqlDataSource) (actorId: string) (host: string) : Task<bool> =
    task {
        use command = dataSource.CreateCommand("SELECT " + (coveredSql "NULL" "@actor" "@host"))
        command.Parameters.AddWithValue("actor", actorId) |> ignore
        command.Parameters.AddWithValue("host", host) |> ignore
        return! scalarBool command
    }

/// True when an instance block covers the host (`host[:port]`) as a domain: no connection is made to it
let instanceBlocksHost (dataSource: NpgsqlDataSource) (host: string) : Task<bool> =
    task {
        use command = dataSource.CreateCommand("SELECT " + (coveredSql "NULL" "NULL" "@host"))
        command.Parameters.AddWithValue("host", host) |> ignore
        return! scalarBool command
    }

/// True when an instance block stops a delivery to this inbox on this host (`host[:port]`)
let instanceBlocksDelivery (dataSource: NpgsqlDataSource) (inbox: string) (host: string) : Task<bool> =
    task {
        use command = dataSource.CreateCommand("SELECT " + (deliveryCoveredSql "NULL" "@inbox" "@host"))
        command.Parameters.AddWithValue("inbox", inbox) |> ignore
        command.Parameters.AddWithValue("host", host) |> ignore
        return! scalarBool command
    }

/// True when the instance or the local user `localUser` blocks the remote actor `actorId` on `host` (`host[:port]`),
/// read in the caller's transaction (after it locked the user's row, R-46)
let isBlockedBy (tx: NpgsqlTransaction) (localUser: string) (actorId: string) (host: string) : Task<bool> =
    task {
        use command = new NpgsqlCommand("SELECT " + (coveredSql "@user" "@actor" "@host"), tx.Connection, tx)
        command.Parameters.AddWithValue("user", localUser) |> ignore
        command.Parameters.AddWithValue("actor", actorId) |> ignore
        command.Parameters.AddWithValue("host", host) |> ignore
        return! scalarBool command
    }

/// The claim check of R-43 and R-44, on a signature's `keyId` before anything is verified or fetched: true when the
/// key's host is on a blocked domain, or the key's owner is a blocked actor (the cached owner of the key, else the key
/// id without its fragment, which is the actor id on Mastodon). With `localUser`, that user's blocks count next to the
/// instance's; with None, instance blocks only.
let isKeyRefused (dataSource: NpgsqlDataSource) (keyId: string) (localUser: string option) : Task<bool> =
    task {
        match Uri.TryCreate(keyId, UriKind.Absolute) with
        | false, _ -> return false
        | true, uri ->
            let host = uri.Authority.ToLowerInvariant()
            let withoutFragment = (UriBuilder(uri, Fragment = "")).Uri.AbsoluteUri

            use owner = dataSource.CreateCommand "SELECT id FROM remote_actors WHERE key_id = @key"
            owner.Parameters.AddWithValue("key", keyId) |> ignore
            let! cached = owner.ExecuteScalarAsync()
            let actorId = (match cached with :? string as id -> id | _ -> withoutFragment)

            let localExpr = if localUser.IsSome then "@user" else "NULL"
            use command = dataSource.CreateCommand("SELECT " + (coveredSql localExpr "@actor" "@host"))
            command.Parameters.AddWithValue("actor", actorId) |> ignore
            command.Parameters.AddWithValue("host", host) |> ignore

            match localUser with
            | Some user -> command.Parameters.AddWithValue("user", user) |> ignore
            | None -> ()

            return! scalarBool command
    }
