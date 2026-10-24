/// Actor lookup against the app (contracts/app-contract.md) with the stored profile as a cache and a fallback
module Megafono.Infrastructure.AppClient

open System
open System.Net.Http
open System.Net.Http.Headers
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Extensions.Logging
open Npgsql
open Megafono.Domain

/// A row of `local_actors`
type LocalActor =
    { AppUserId: string
      Handle: string
      ActorType: string
      Locked: bool
      Name: string
      Summary: string option
      IconUrl: string option
      ProfileUrl: string
      ProfileCheckedAt: DateTimeOffset
      PublicKeyPem: string
      EncPrivateKey: byte[]
      CreatedAt: DateTimeOffset
      Discoverable: bool
      Indexable: bool }

type LookupResult =
    | Found of LocalActor
    /// The app does not know this user
    | NotFound
    /// The app did not answer usefully; the stored profile is under 24 h old and stands in
    | UnavailableWithFallback of LocalActor
    | UnavailableNoFallback
    /// The actor was deleted (specs/003-actor-deletion): answered from the stored tombstone, the app is not asked.
    /// Only `ById` returns it; a handle may belong to another id by now, so `ByHandle` answers `NotFound`.
    | Deleted

/// What the app's answer says, validated (data-model.md "Validation rules")
type AppProfile =
    { Id: string
      Handle: string
      Name: string
      Summary: string option
      IconUrl: string option
      ProfileUrl: string
      Type: string
      Locked: bool
      /// Optional in the answer: absent or null is true
      Discoverable: bool
      /// Optional in the answer: absent or null is false
      Indexable: bool }

/// What the app answered to an explicit profile fetch
type FetchResult =
    | Fetched of AppProfile
    | FetchNotFound
    | FetchUnavailable

type AppClient =
    { ByHandle: string -> Task<LookupResult>
      ById: string -> Task<LookupResult>
      /// Always asks the app (no 5 minute cache) and stores nothing
      Fetch: string -> Task<FetchResult>
      /// Stores a fetched profile on the caller's transaction (so the write commits with what depends on it) and
      /// returns the stored actor and whether anything shown in the actor document changed (FR-007), or None when
      /// the id belongs to a deleted actor (never revived or updated, FR-006 of 003). A handle held by another
      /// live user raises the unique violation.
      StoreIn: NpgsqlTransaction -> AppProfile -> Task<(LocalActor * bool) option> }

type Env =
    { DataSource: NpgsqlDataSource
      Keys: KeyVault.MasterKeys
      Http: HttpClient
      /// The app's private address, `Megafono:App:BaseUrl`
      BaseUrl: string
      Token: string
      Domain: string
      Time: TimeProvider
      AllowPrivateAddresses: bool
      Log: ILogger }

let private freshFor = TimeSpan.FromMinutes 5.0
let private fallbackFor = TimeSpan.FromHours 24.0
let private requestTimeout = TimeSpan.FromSeconds 5.0

let private actorTypes = set [ "Person"; "Service"; "Group" ]

let private isAbsoluteUrl (allowHttp: bool) (text: string) =
    match Uri.TryCreate(text, UriKind.Absolute) with
    | true, uri -> uri.Scheme = Uri.UriSchemeHttps || (allowHttp && uri.Scheme = Uri.UriSchemeHttp)
    | _ -> false

let private optionalString (root: JsonElement) (name: string) : Result<string option, string> =
    match root.TryGetProperty name with
    | false, _ -> Ok None
    | true, value when value.ValueKind = JsonValueKind.Null -> Ok None
    | true, value when value.ValueKind = JsonValueKind.String -> Ok(Some(value.GetString()))
    | _ -> Error $"'{name}' must be a string"

/// An optional boolean: absent or null takes the default, any other type is an error
let private optionalBool (root: JsonElement) (name: string) (fallback: bool) : Result<bool, string> =
    match root.TryGetProperty name with
    | false, _ -> Ok fallback
    | true, v when v.ValueKind = JsonValueKind.Null -> Ok fallback
    | true, v when v.ValueKind = JsonValueKind.True -> Ok true
    | true, v when v.ValueKind = JsonValueKind.False -> Ok false
    | _ -> Error $"'{name}' must be true or false"

/// Parses and validates the 200 body of an actor lookup
let parseProfile (allowHttp: bool) (body: string) : Result<AppProfile, string> =
    try
        use document = JsonDocument.Parse body
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error "the answer is not a JSON object"
        else
            let required name =
                match optionalString root name with
                | Ok(Some value) when value.Trim() <> "" -> Ok value
                | Ok _ -> Error $"'{name}' is required"
                | Error e -> Error e

            result {
                let! id = required "id"
                let! rawHandle = required "handle"
                let! name = required "name"
                let! profileUrl = required "profileUrl"
                let! summary = optionalString root "summary"
                let! iconUrl = optionalString root "iconUrl"
                let! kind = optionalString root "type"

                let! locked =
                    match root.TryGetProperty "locked" with
                    | true, v when v.ValueKind = JsonValueKind.True -> Ok true
                    | true, v when v.ValueKind = JsonValueKind.False -> Ok false
                    | _ -> Error "'locked' is required and must be true or false"

                let! discoverable = optionalBool root "discoverable" true
                let! indexable = optionalBool root "indexable" false

                let! handle =
                    match Ids.normalizeHandle rawHandle with
                    | Some handle -> Ok handle
                    | None -> Error "'handle' must match ^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?$"

                let kind = defaultArg kind "Person"

                do!
                    if not (Ids.isValidAppUserId id) then Error "'id' must match ^[A-Za-z0-9_-]{1,64}$"
                    elif not (actorTypes.Contains kind) then Error "'type' must be Person, Service or Group"
                    elif not (isAbsoluteUrl allowHttp profileUrl) then Error "'profileUrl' must be an absolute https URL"
                    elif iconUrl.IsSome && not (isAbsoluteUrl allowHttp iconUrl.Value) then Error "'iconUrl' must be an absolute https URL"
                    else Ok()

                return
                    { Id = id
                      Handle = handle
                      Name = name
                      Summary = summary
                      IconUrl = iconUrl
                      ProfileUrl = profileUrl
                      Type = kind
                      Locked = locked
                      Discoverable = discoverable
                      Indexable = indexable }
            }
    with :? JsonException as e ->
        Error $"the answer is not valid JSON: {e.Message}"

// ---- storage ---------------------------------------------------------------------------------------------

let private columns =
    "app_user_id, handle, actor_type, locked, name, summary, icon_url, profile_url, profile_checked_at, public_key_pem, enc_private_key, created_at, discoverable, indexable"

let private readActor (reader: System.Data.Common.DbDataReader) : LocalActor =
    let optional (i: int) = if reader.IsDBNull i then None else Some(reader.GetString i)

    { AppUserId = reader.GetString 0
      Handle = reader.GetString 1
      ActorType = reader.GetString 2
      Locked = reader.GetBoolean 3
      Name = reader.GetString 4
      Summary = optional 5
      IconUrl = optional 6
      ProfileUrl = reader.GetString 7
      ProfileCheckedAt = reader.GetFieldValue<DateTimeOffset> 8
      PublicKeyPem = reader.GetString 9
      EncPrivateKey = reader.GetFieldValue<byte[]> 10
      CreatedAt = reader.GetFieldValue<DateTimeOffset> 11
      Discoverable = reader.GetBoolean 12
      Indexable = reader.GetBoolean 13 }

let private querySingle (command: NpgsqlCommand) =
    task {
        use! reader = command.ExecuteReaderAsync()
        return if reader.Read() then Some(readActor reader) else None
    }

let private storedById (env: Env) (id: string) =
    task {
        use command = env.DataSource.CreateCommand $"SELECT {columns} FROM local_actors WHERE app_user_id = @id AND deleted_at IS NULL"
        command.Parameters.AddWithValue("id", id) |> ignore
        return! querySingle command
    }

let private isDeleted (env: Env) (id: string) : Task<bool> =
    task {
        use command = env.DataSource.CreateCommand "SELECT 1 FROM local_actors WHERE app_user_id = @id AND deleted_at IS NOT NULL"
        command.Parameters.AddWithValue("id", id) |> ignore
        let! value = command.ExecuteScalarAsync()
        return not (isNull value)
    }

let private storedByHandle (env: Env) (handle: string) =
    task {
        use command = env.DataSource.CreateCommand $"SELECT {columns} FROM local_actors WHERE handle = @handle AND deleted_at IS NULL"
        command.Parameters.AddWithValue("handle", handle) |> ignore
        return! querySingle command
    }

let private bindProfile (command: NpgsqlCommand) (profile: AppProfile) (now: DateTimeOffset) =
    let add (name: string) (value: obj) = command.Parameters.AddWithValue(name, value) |> ignore
    let orNull (o: string option) = o |> Option.map box |> Option.defaultValue (box DBNull.Value)
    add "id" profile.Id
    add "handle" profile.Handle
    add "type" profile.Type
    add "locked" profile.Locked
    add "name" profile.Name
    add "summary" (orNull profile.Summary)
    add "icon" (orNull profile.IconUrl)
    add "profile" profile.ProfileUrl
    add "discoverable" profile.Discoverable
    add "indexable" profile.Indexable
    add "now" now.UtcDateTime

/// A command on the data source, or on the caller's transaction when there is one
let private commandFor (env: Env) (tx: NpgsqlTransaction option) (sql: string) : NpgsqlCommand =
    match tx with
    | Some t -> new NpgsqlCommand(sql, t.Connection, t)
    | None -> env.DataSource.CreateCommand sql

/// Updates the profile of a known id; a different handle is a handle change and is stored (nothing else about
/// the identity moves). None when the id has no row yet.
let private updateKnown (env: Env) (tx: NpgsqlTransaction option) (profile: AppProfile) (now: DateTimeOffset) =
    task {
        use command =
            commandFor env tx
                $"""UPDATE local_actors SET handle = @handle, actor_type = @type, locked = @locked, name = @name, summary = @summary,
                    icon_url = @icon, profile_url = @profile, discoverable = @discoverable, indexable = @indexable,
                    profile_checked_at = @now
                    WHERE app_user_id = @id AND deleted_at IS NULL RETURNING {columns}"""

        bindProfile command profile now
        return! querySingle command
    }

/// First sight of an id: the key pair is created and stored in the same insert (FR-006). If another lookup
/// inserted the row first, the conflict clause only refreshes the profile, so the first key stays. A deleted
/// row is never touched: nothing is returned (R-33).
let private insertNew (env: Env) (tx: NpgsqlTransaction option) (profile: AppProfile) (now: DateTimeOffset) =
    task {
        let key = KeyVault.createActorKey env.Keys (Ids.actorId env.Domain profile.Id)

        use command =
            commandFor env tx
                $"""INSERT INTO local_actors (app_user_id, handle, actor_type, locked, name, summary, icon_url, profile_url,
                        discoverable, indexable, profile_checked_at, public_key_pem, enc_private_key, created_at)
                    VALUES (@id, @handle, @type, @locked, @name, @summary, @icon, @profile, @discoverable, @indexable,
                        @now, @pem, @enc, @now)
                    ON CONFLICT (app_user_id) DO UPDATE SET handle = EXCLUDED.handle, actor_type = EXCLUDED.actor_type,
                        locked = EXCLUDED.locked, name = EXCLUDED.name, summary = EXCLUDED.summary,
                        icon_url = EXCLUDED.icon_url, profile_url = EXCLUDED.profile_url,
                        discoverable = EXCLUDED.discoverable, indexable = EXCLUDED.indexable,
                        profile_checked_at = EXCLUDED.profile_checked_at
                    WHERE local_actors.deleted_at IS NULL
                    RETURNING {columns}"""

        bindProfile command profile now
        command.Parameters.AddWithValue("pem", key.PublicKeyPem) |> ignore
        command.Parameters.AddWithValue("enc", key.EncryptedPrivateKey) |> ignore
        return! querySingle command
    }

/// The stored actor, or None when the id belongs to a deleted actor
let private store (env: Env) (tx: NpgsqlTransaction option) (profile: AppProfile) : Task<LocalActor option> =
    task {
        let now = env.Time.GetUtcNow()

        match! updateKnown env tx profile now with
        | Some actor -> return Some actor
        | None -> return! insertNew env tx profile now
    }

// ---- the call --------------------------------------------------------------------------------------------

type private Answer =
    | Answered of AppProfile
    | Unknown
    | Failed of string

let private call (env: Env) (path: string) : Task<Answer> =
    task {
        try
            use request = new HttpRequestMessage(HttpMethod.Get, env.BaseUrl.TrimEnd('/') + path)
            request.Headers.Authorization <- AuthenticationHeaderValue("Bearer", env.Token)
            request.Headers.Accept.Add(MediaTypeWithQualityHeaderValue "application/json")
            use! response = env.Http.SendAsync request

            match int response.StatusCode with
            | 404 -> return Unknown
            | 200 ->
                let! body = response.Content.ReadAsStringAsync()

                match parseProfile env.AllowPrivateAddresses body with
                | Ok profile -> return Answered profile
                | Error reason -> return Failed $"invalid answer: {reason}"
            | status -> return Failed $"HTTP {status}"
        with
        | :? HttpRequestException as e -> return Failed e.Message
        | :? TaskCanceledException -> return Failed $"no answer within {requestTimeout.TotalSeconds} s"
    }

/// Fresh stored row, else the app; on failure the stored row if it is under 24 h old. `whenDeleted` is the answer
/// when the app's profile turns out to belong to a deleted actor.
let private lookup
    (env: Env)
    (stored: Task<LocalActor option>)
    (whenDeleted: LookupResult)
    (path: string)
    (accept: AppProfile -> bool)
    : Task<LookupResult> =
    task {
        let! known = stored
        let now = env.Time.GetUtcNow()

        match known with
        | Some actor when now - actor.ProfileCheckedAt < freshFor -> return Found actor
        | _ ->
            let unavailable (reason: string) =
                env.Log.LogWarning("Actor lookup {Path} failed: {Reason}", path, reason)

                match known with
                | Some actor when now - actor.ProfileCheckedAt < fallbackFor -> UnavailableWithFallback actor
                | _ -> UnavailableNoFallback

            match! call env path with
            | Unknown -> return NotFound
            | Failed reason -> return unavailable reason
            | Answered profile when not (accept profile) -> return unavailable "the answer is for a different user than asked"
            | Answered profile ->
                try
                    match! store env None profile with
                    | Some actor -> return Found actor
                    | None -> return whenDeleted
                with :? PostgresException as e when e.SqlState = PostgresErrorCodes.UniqueViolation ->
                    // The new handle is still held by another live user: keep serving what we had
                    return unavailable $"handle '{profile.Handle}' belongs to another user"
    }

/// The fields that appear in the actor document, and so are worth an `Update` when they change
let private profileDiffers (before: LocalActor) (after: LocalActor) =
    before.Handle <> after.Handle
    || before.ActorType <> after.ActorType
    || before.Locked <> after.Locked
    || before.Name <> after.Name
    || before.Summary <> after.Summary
    || before.IconUrl <> after.IconUrl
    || before.ProfileUrl <> after.ProfileUrl
    || before.Discoverable <> after.Discoverable
    || before.Indexable <> after.Indexable

let private fetch (env: Env) (id: string) : Task<FetchResult> =
    task {
        if not (Ids.isValidAppUserId id) then
            return FetchNotFound
        else
            match! call env $"/megafono/actors/{Uri.EscapeDataString id}" with
            | Unknown -> return FetchNotFound
            | Failed reason ->
                env.Log.LogWarning("Profile fetch of {Id} failed: {Reason}", id, reason)
                return FetchUnavailable
            | Answered profile when profile.Id <> id -> return FetchUnavailable
            | Answered profile -> return Fetched profile
    }

let private storeIn (env: Env) (tx: NpgsqlTransaction) (profile: AppProfile) : Task<(LocalActor * bool) option> =
    task {
        let! previous = storedById env profile.Id

        match! store env (Some tx) profile with
        | None -> return None
        | Some actor ->
            let changed =
                match previous with
                | Some before -> profileDiffers before actor
                | None -> true

            return Some(actor, changed)
    }

let create (env: Env) : AppClient =
    { Fetch = fetch env
      StoreIn = storeIn env
      ByHandle =
        fun handle ->
            match Ids.normalizeHandle handle with
            | None -> Task.FromResult NotFound
            | Some h -> lookup env (storedByHandle env h) NotFound $"/megafono/actors?handle={Uri.EscapeDataString h}" (fun p -> p.Handle = h)
      ById =
        fun id ->
            if not (Ids.isValidAppUserId id) then
                Task.FromResult NotFound
            else
                task {
                    // A deleted id is answered from the stored tombstone, before anything else and without the app
                    let! deleted = isDeleted env id

                    if deleted then
                        return Deleted
                    else
                        return! lookup env (storedById env id) Deleted $"/megafono/actors/{Uri.EscapeDataString id}" (fun p -> p.Id = id)
                } }

/// The plain HttpClient for the app's private address (SafeHttp is for remote servers and would refuse it)
let createHttpClient () : HttpClient =
    new HttpClient(Timeout = requestTimeout)
