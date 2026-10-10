/// Remote actors: fetch, validate, cache, key lookup and WebFinger (research R-01, R-02, R-03; FR-011, FR-012)
module Megafono.Infrastructure.RemoteActors

open System
open System.Security.Cryptography
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql

/// A row of `remote_actors`
type RemoteActor =
    { Id: string
      /// Lowercase authority of `Id` (host, plus the port when it is not the default)
      Host: string
      /// `user@host`, without the leading @
      Handle: string option
      ActorType: string
      Inbox: string
      SharedInbox: string option
      FollowersUrl: string option
      /// Plain text, never HTML
      Name: string option
      IconUrl: string option
      ProfileUrl: string option
      KeyId: string
      PublicKeyPem: string
      FetchedAt: DateTimeOffset }

type RemoteError =
    /// The URL or its address is refused (FR-021)
    | UnsafeTarget of string
    | NotFound
    /// Reached the server but the document breaks a rule
    | Invalid of string
    | Unreachable of string

type Env =
    { DataSource: NpgsqlDataSource
      Http: SafeHttp.SafeHttpClient
      Instance: InstanceKey.InstanceKeyProvider
      Time: TimeProvider
      AllowPrivateAddresses: bool
      Log: ILogger }

let private cacheFor = TimeSpan.FromHours 24.0
let private actorTypes = set [ "Person"; "Service"; "Application"; "Group"; "Organization" ]

let authority (uri: Uri) : string = uri.Authority.ToLowerInvariant()

/// Same host, or a subdomain of it (ports ignored): where a shared inbox may live
let isSameOrSubdomain (actorHost: string) (otherHost: string) =
    let a = actorHost.ToLowerInvariant()
    let o = otherHost.ToLowerInvariant()
    o = a || o.EndsWith("." + a)

// ---- parsing the actor document --------------------------------------------------------------------------

type private Key = { Id: string; Owner: string option; Pem: string }

let private text (element: JsonElement) (name: string) : string option =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// A property that is a value, or an array of them (JSON-LD allows both): its elements
let private items (element: JsonElement) (name: string) : JsonElement list =
    match element.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [ for item in v.EnumerateArray() -> item ]
    | true, v when v.ValueKind = JsonValueKind.Null -> []
    | true, v -> [ v ]
    | _ -> []

let private keysOf (element: JsonElement) : Key list =
    items element "publicKey"
    |> List.choose (fun k ->
        if k.ValueKind <> JsonValueKind.Object then
            None
        else
            match text k "id", text k "publicKeyPem" with
            | Some id, Some pem -> Some { Id = id; Owner = text k "owner"; Pem = pem }
            | _ -> None)

let private absolute (allowHttp: bool) (value: string) : Uri option =
    match Uri.TryCreate(value, UriKind.Absolute) with
    | true, uri when uri.Scheme = Uri.UriSchemeHttps || (allowHttp && uri.Scheme = Uri.UriSchemeHttp) -> Some uri
    | _ -> None

/// The first usable URL of a JSON-LD link property: a string, `{url}`/`{href}`, or an array of those
let private linkOf (allowHttp: bool) (element: JsonElement) (name: string) : string option =
    items element name
    |> List.tryPick (fun item ->
        let candidate =
            match item.ValueKind with
            | JsonValueKind.String -> Some(item.GetString())
            | JsonValueKind.Object -> text item "url" |> Option.orElse (text item "href")
            | _ -> None

        candidate |> Option.bind (absolute allowHttp) |> Option.map (fun u -> u.AbsoluteUri))

let private actorType (element: JsonElement) : string option =
    items element "type"
    |> List.tryPick (fun t -> if t.ValueKind = JsonValueKind.String && actorTypes.Contains(t.GetString()) then Some(t.GetString()) else None)

/// Validates a fetched actor document against the URL it came from (data-model.md "Remote documents") and
/// returns the row to store. `preferredKeyId` picks which of several keys is kept.
let parseActor (allowHttp: bool) (now: DateTimeOffset) (fetchedFrom: Uri) (preferredKeyId: string option) (element: JsonElement) : Result<RemoteActor, string> =
    if element.ValueKind <> JsonValueKind.Object then
        Error "the document is not a JSON object"
    else
        match text element "id" |> Option.bind (absolute allowHttp) with
        | None -> Error "'id' is missing or not an absolute URL"
        | Some id when authority id <> authority fetchedFrom -> Error $"'id' is on {authority id} but the document came from {authority fetchedFrom}"
        | Some id ->
            let idText = id.AbsoluteUri

            match actorType element with
            | None -> Error "'type' is not an actor type"
            | Some kind ->
                match text element "inbox" |> Option.bind (absolute allowHttp) with
                | None -> Error "'inbox' is missing or not an absolute URL"
                | Some inbox ->
                    let keys = keysOf element

                    let key =
                        preferredKeyId
                        |> Option.bind (fun k -> keys |> List.tryFind (fun x -> x.Id = k))
                        |> Option.orElse (List.tryHead keys)

                    match key with
                    | None -> Error "'publicKey' is missing"
                    | Some k when k.Owner <> Some id.OriginalString && k.Owner <> Some idText -> Error "'publicKey.owner' is not the actor's id"
                    | Some k when (absolute allowHttp k.Id |> Option.map (fun u -> u.Host.ToLowerInvariant())) <> Some(id.Host.ToLowerInvariant()) ->
                        Error "the key id is not on the actor's host"
                    | Some k ->
                        let validPem =
                            try
                                use rsa = RSA.Create()
                                rsa.ImportFromPem k.Pem
                                true
                            with _ -> false

                        if not validPem then
                            Error "'publicKeyPem' is not a valid RSA public key"
                        else
                            let username = text element "preferredUsername"

                            let sharedInbox =
                                match element.TryGetProperty "endpoints" with
                                | true, endpoints when endpoints.ValueKind = JsonValueKind.Object ->
                                    text endpoints "sharedInbox"
                                    |> Option.bind (absolute allowHttp)
                                    // Only trusted when it is the actor's own server: it receives every delivery for the actor
                                    |> Option.filter (fun u -> isSameOrSubdomain id.Host u.Host)
                                    |> Option.map (fun u -> u.AbsoluteUri)
                                | _ -> None

                            let name =
                                text element "name"
                                |> Option.map Sanitizer.toText
                                |> Option.filter (fun n -> n <> "")
                                |> Option.orElse username

                            Ok
                                { Id = idText
                                  Host = authority id
                                  Handle = username |> Option.map (fun u -> $"{u}@{authority id}")
                                  ActorType = kind
                                  Inbox = inbox.AbsoluteUri
                                  SharedInbox = sharedInbox
                                  FollowersUrl = text element "followers" |> Option.bind (absolute allowHttp) |> Option.map (fun u -> u.AbsoluteUri)
                                  Name = name
                                  IconUrl = linkOf allowHttp element "icon"
                                  ProfileUrl = linkOf allowHttp element "url"
                                  KeyId = k.Id
                                  PublicKeyPem = k.Pem
                                  FetchedAt = now }

// ---- storage ---------------------------------------------------------------------------------------------

let private columns =
    "id, host, handle, actor_type, inbox, shared_inbox, followers_url, name, icon_url, profile_url, key_id, public_key_pem, fetched_at"

let private readActor (reader: System.Data.Common.DbDataReader) : RemoteActor =
    let optional (i: int) = if reader.IsDBNull i then None else Some(reader.GetString i)

    { Id = reader.GetString 0
      Host = reader.GetString 1
      Handle = optional 2
      ActorType = reader.GetString 3
      Inbox = reader.GetString 4
      SharedInbox = optional 5
      FollowersUrl = optional 6
      Name = optional 7
      IconUrl = optional 8
      ProfileUrl = optional 9
      KeyId = reader.GetString 10
      PublicKeyPem = reader.GetString 11
      FetchedAt = reader.GetFieldValue<DateTimeOffset> 12 }

let private querySingle (command: NpgsqlCommand) =
    task {
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(readActor reader) else None
    }

let private storedBy (env: Env) (column: string) (value: string) =
    task {
        use command = env.DataSource.CreateCommand $"SELECT {columns} FROM remote_actors WHERE {column} = @v"
        command.Parameters.AddWithValue("v", value) |> ignore
        return! querySingle command
    }

let private isFresh (env: Env) (actor: RemoteActor) = env.Time.GetUtcNow() - actor.FetchedAt < cacheFor

/// Inserts or refreshes. A key id already held by another actor is refused: it would let one actor claim the
/// signatures of another.
let private save (env: Env) (actor: RemoteActor) : Task<Result<RemoteActor, RemoteError>> =
    task {
        try
            use command =
                env.DataSource.CreateCommand
                    $"""INSERT INTO remote_actors ({columns})
                        VALUES (@id, @host, @handle, @type, @inbox, @shared, @followers, @name, @icon, @profile, @keyid, @pem, @now)
                        ON CONFLICT (id) DO UPDATE SET host = EXCLUDED.host, handle = EXCLUDED.handle, actor_type = EXCLUDED.actor_type,
                            inbox = EXCLUDED.inbox, shared_inbox = EXCLUDED.shared_inbox, followers_url = EXCLUDED.followers_url,
                            name = EXCLUDED.name, icon_url = EXCLUDED.icon_url, profile_url = EXCLUDED.profile_url,
                            key_id = EXCLUDED.key_id, public_key_pem = EXCLUDED.public_key_pem, fetched_at = EXCLUDED.fetched_at
                        RETURNING {columns}"""

            let add (name: string) (value: obj) = command.Parameters.AddWithValue(name, value) |> ignore
            let orNull (o: string option) = o |> Option.map box |> Option.defaultValue (box DBNull.Value)
            add "id" actor.Id
            add "host" actor.Host
            add "handle" (orNull actor.Handle)
            add "type" actor.ActorType
            add "inbox" actor.Inbox
            add "shared" (orNull actor.SharedInbox)
            add "followers" (orNull actor.FollowersUrl)
            add "name" (orNull actor.Name)
            add "icon" (orNull actor.IconUrl)
            add "profile" (orNull actor.ProfileUrl)
            add "keyid" actor.KeyId
            add "pem" actor.PublicKeyPem
            add "now" actor.FetchedAt.UtcDateTime
            let! stored = querySingle command
            return Ok stored.Value
        with :? PostgresException as e when e.SqlState = PostgresErrorCodes.UniqueViolation ->
            return Error(Invalid "the key id already belongs to another actor")
    }

// ---- fetching --------------------------------------------------------------------------------------------

let private mapFetchError (error: SafeHttp.FetchError) : RemoteError =
    match error with
    | SafeHttp.UnsafeUrl reason
    | SafeHttp.BlockedAddress reason -> UnsafeTarget reason
    | SafeHttp.TooManyRedirects -> Unreachable "too many redirects"
    | SafeHttp.ResponseTooLarge -> Invalid "the response is larger than 1 MB"
    | SafeHttp.TimedOut -> Unreachable "timed out"
    | SafeHttp.Network reason -> Unreachable reason

/// GET signed with the instance actor; 2xx bodies only
let private fetchDocument (env: Env) (accept: string option) (url: Uri) : Task<Result<SafeHttp.FetchResponse, RemoteError>> =
    task {
        let! instance = env.Instance.Get()

        let! result =
            match accept with
            | Some a -> SafeHttp.signedGetAccepting a env.Http instance.KeyId instance.PrivateKey url
            | None -> SafeHttp.signedGet env.Http instance.KeyId instance.PrivateKey url

        match result with
        | Error e -> return Error(mapFetchError e)
        | Ok response when response.Status = 404 || response.Status = 410 -> return Error NotFound
        | Ok response when response.Status < 200 || response.Status > 299 -> return Error(Unreachable $"HTTP {response.Status}")
        | Ok response -> return Ok response
    }

let private withJson (response: SafeHttp.FetchResponse) (f: JsonElement -> Task<Result<'a, RemoteError>>) : Task<Result<'a, RemoteError>> =
    task {
        try
            use document = JsonDocument.Parse response.Body
            return! f document.RootElement
        with :? JsonException as e ->
            return Error(Invalid $"the response is not valid JSON: {e.Message}")
    }

let private parseAndSave (env: Env) (response: SafeHttp.FetchResponse) (preferredKeyId: string option) (element: JsonElement) =
    task {
        match parseActor env.AllowPrivateAddresses (env.Time.GetUtcNow()) response.Uri preferredKeyId element with
        | Error reason -> return Error(Invalid reason)
        | Ok actor -> return! save env actor
    }

let private parseUrl (env: Env) (text: string) : Result<Uri, RemoteError> =
    match absolute env.AllowPrivateAddresses text with
    | Some uri -> Ok uri
    | None -> Error(UnsafeTarget "only absolute https URLs are fetched")

/// Fetches and stores an actor regardless of the cache
let private fetchActor (env: Env) (actorId: string) (preferredKeyId: string option) : Task<Result<RemoteActor, RemoteError>> =
    task {
        match parseUrl env actorId with
        | Error e -> return Error e
        | Ok url ->
            match! fetchDocument env None url with
            | Error e -> return Error e
            | Ok response -> return! withJson response (parseAndSave env response preferredKeyId)
    }

/// The actor with this id: the stored copy while it is under 24 h old, else a fresh fetch
let getActor (env: Env) (actorId: string) : Task<Result<RemoteActor, RemoteError>> =
    task {
        match! storedBy env "id" actorId with
        | Some actor when isFresh env actor -> return Ok actor
        | _ -> return! fetchActor env actorId None
    }

/// Fetches again whatever the cache holds (profile refresh, `actor.updated`)
let refreshActor (env: Env) (actorId: string) = fetchActor env actorId None

// ---- keys ------------------------------------------------------------------------------------------------

let private withoutFragment (keyId: string) : Result<Uri, string> =
    match Uri.TryCreate(keyId, UriKind.Absolute) with
    | true, uri ->
        let builder = UriBuilder uri
        builder.Fragment <- ""
        Ok builder.Uri
    | _ -> Error "the key id is not an absolute URL"

/// Fetches the document a key id points at and returns the actor that owns the key. Servers differ: Mastodon's
/// `…#main-key` is the actor document itself, GoToSocial's `…/main-key` is a key or a partial actor stub that
/// names its `owner`; in that case the owner's actor document is fetched and must hold the same key.
let private fetchKey (env: Env) (keyId: string) : Task<Result<RemoteActor, RemoteError>> =
    task {
        match withoutFragment keyId with
        | Error reason -> return Error(Invalid reason)
        | Ok url ->
            match parseUrl env url.AbsoluteUri with
            | Error e -> return Error e
            | Ok url ->
                match! fetchDocument env None url with
                | Error e -> return Error e
                | Ok response ->
                    return!
                        withJson response (fun element ->
                            task {
                                if element.ValueKind <> JsonValueKind.Object then
                                    return Error(Invalid "the document is not a JSON object")
                                elif (text element "inbox").IsSome then
                                    // A full actor document
                                    match! parseAndSave env response (Some keyId) element with
                                    | Ok actor when actor.KeyId <> keyId -> return Error(Invalid "the actor does not declare this key id")
                                    | other -> return other
                                else
                                    // A key object or a stub: find the entry for this key and follow its owner
                                    let ownKey =
                                        match text element "publicKeyPem", text element "id" with
                                        | Some pem, Some id when id = keyId || id = url.AbsoluteUri -> Some { Id = id; Owner = text element "owner"; Pem = pem }
                                        | _ -> None

                                    let owner =
                                        keysOf element
                                        |> List.tryFind (fun k -> k.Id = keyId)
                                        |> Option.orElse ownKey
                                        |> Option.bind (fun k -> k.Owner)

                                    match owner |> Option.bind (absolute env.AllowPrivateAddresses) with
                                    | None -> return Error(Invalid "the document does not name an owner for this key")
                                    | Some ownerUrl when ownerUrl.Host.ToLowerInvariant() <> url.Host.ToLowerInvariant() ->
                                        return Error(Invalid "the key's owner is on another host")
                                    | Some ownerUrl ->
                                        match! fetchActor env ownerUrl.AbsoluteUri (Some keyId) with
                                        | Ok actor when actor.KeyId <> keyId -> return Error(Invalid "the owner does not declare this key id")
                                        | other -> return other
                            })
    }

/// The actor that owns a signing key: from the cache while fresh, else fetched. `keyId` may use either form
/// (`#main-key` or `/main-key`); it is matched exactly against what the actor declares.
let findKey (env: Env) (keyId: string) : Task<Result<RemoteActor, RemoteError>> =
    task {
        match! storedBy env "key_id" keyId with
        | Some actor when isFresh env actor -> return Ok actor
        | _ -> return! fetchKey env keyId
    }

/// One more fetch after a signature failed, in case the actor rotated its key (FR-012). The caller does this once.
let refetchKey (env: Env) (keyId: string) : Task<Result<RemoteActor, RemoteError>> = fetchKey env keyId

// ---- WebFinger -------------------------------------------------------------------------------------------

/// `@user@host` or `user@host`, as (user, host)
let parseHandle (handle: string) : (string * string) option =
    let trimmed = (if isNull handle then "" else handle.Trim()).TrimStart('@')
    let at = trimmed.IndexOf '@'

    if at <= 0 || at = trimmed.Length - 1 || trimmed.IndexOf('@', at + 1) >= 0 then
        None
    else
        let user = trimmed.Substring(0, at)
        let host = trimmed.Substring(at + 1).ToLowerInvariant()

        let userOk = user |> Seq.forall (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '.' || c = '-')

        let hostOk =
            match Uri.TryCreate("https://" + host, UriKind.Absolute) with
            | true, uri -> uri.Authority.ToLowerInvariant() = host && Uri.CheckHostName uri.Host <> UriHostNameType.Unknown
            | _ -> false

        if userOk && hostOk then Some(user, host) else None

let private selfLink (element: JsonElement) : string option =
    items element "links"
    |> List.tryPick (fun link ->
        match text link "rel", text link "type", text link "href" with
        | Some "self", Some kind, Some href when kind.StartsWith "application/activity+json" || kind.StartsWith "application/ld+json" -> Some href
        | _ -> None)

/// `https://host/.well-known/webfinger?resource=acct:user@host`; under the test switch plain http is tried second
let private webFingerUrls (env: Env) (user: string) (host: string) : Uri list =
    let acct = Uri.EscapeDataString $"{user}@{host}"
    let make scheme = Uri $"{scheme}://{host}/.well-known/webfinger?resource=acct:{acct}"
    if env.AllowPrivateAddresses then [ make "https"; make "http" ] else [ make "https" ]

/// `user@host` → the actor, through WebFinger. The `self` link of type activity+json is followed.
let resolveHandle (env: Env) (handle: string) : Task<Result<RemoteActor, RemoteError>> =
    task {
        match parseHandle handle with
        | None -> return Error NotFound
        | Some(user, host) ->
            let rec attempt (urls: Uri list) (last: RemoteError) =
                task {
                    match urls with
                    | [] -> return Error last
                    | url :: rest ->
                        match! fetchDocument env (Some "application/jrd+json, application/json") url with
                        | Error(Unreachable _ as e) when not rest.IsEmpty -> return! attempt rest e
                        | Error e -> return Error e
                        | Ok response ->
                            return!
                                withJson response (fun jrd ->
                                    task {
                                        match selfLink jrd with
                                        | None -> return Error NotFound
                                        | Some href -> return! getActor env href
                                    })
                }

            match! attempt (webFingerUrls env user host) NotFound with
            | Error e -> return Error e
            | Ok actor ->
                // The handle the user typed is the one we know them by, when the actor agrees about the user name
                let typed = $"{user.ToLowerInvariant()}@{host}"

                match actor.Handle with
                | Some existing when existing.StartsWith($"{user}@", StringComparison.OrdinalIgnoreCase) && not (String.Equals(existing, typed)) ->
                    use command = env.DataSource.CreateCommand "UPDATE remote_actors SET handle = @h WHERE id = @id"
                    command.Parameters.AddWithValue("h", typed) |> ignore
                    command.Parameters.AddWithValue("id", actor.Id) |> ignore
                    let! _ = command.ExecuteNonQueryAsync()
                    return Ok { actor with Handle = Some typed }
                | _ -> return Ok actor
    }

/// A handle (`@user@host`, `user@host`) or an actor URL
let resolveTarget (env: Env) (target: string) : Task<Result<RemoteActor, RemoteError>> =
    let trimmed = if isNull target then "" else target.Trim()

    if trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) then
        getActor env trimmed
    else
        resolveHandle env trimmed
