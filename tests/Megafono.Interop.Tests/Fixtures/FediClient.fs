/// One Mastodon-compatible client API client for both servers (GoToSocial and Mastodon implement the
/// same subset): search with `resolve`, follow, relationships, follow requests, timelines, statuses.
/// Only the fields the federation tests read are typed; everything else in the JSON is ignored.
module Megafono.Interop.Tests.FediClient

open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

type Account =
    { Id: string
      Username: string
      Acct: string
      Url: string
      DisplayName: string
      Note: string
      Avatar: string
      Locked: bool }

type Status =
    { Id: string
      Uri: string
      Url: string
      Visibility: string
      Content: string
      /// ISO time of the last edit; null when the status was never edited or the server does not say
      EditedAt: string
      /// The local id of the status this one replies to; null for a post that replies to nothing
      InReplyToId: string
      /// The content warning text ("" when there is none)
      SpoilerText: string
      Sensitive: bool
      Account: Account }

type Relationship =
    { Id: string
      Following: bool
      Requested: bool
      FollowedBy: bool
      Blocking: bool }

/// A non-2xx answer from the server under test.
exception FediClientException of method': string * path: string * status: int * body: string

let private json =
    JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true)

/// Default polling budget for "eventually" assertions on federated state (R-15: 30-60 s).
let defaultTimeout = TimeSpan.FromSeconds 60.0

/// Polls `check` every 500 ms until it returns `Some`, then returns the value. Exceptions from `check`
/// (a 404 before the object arrived, say) count as "not yet" and are reported if the time runs out.
let pollUntil (timeout: TimeSpan) (what: string) (check: unit -> Task<'a option>) : Task<'a> =
    task {
        let deadline = DateTime.UtcNow + timeout
        let mutable result = None
        let mutable lastError = ""
        while result.IsNone do
            try
                let! r = check ()
                result <- r
            with e -> lastError <- e.Message
            if result.IsNone then
                if DateTime.UtcNow > deadline then
                    failwith $"timed out after {timeout.TotalSeconds:F0}s waiting for: {what}. Last error: {lastError}"
                do! Task.Delay 500
        return result.Value
    }

/// `pollUntil` with the 60 s default.
let pollFor (what: string) (check: unit -> Task<'a option>) = pollUntil defaultTimeout what check

/// Polls a boolean condition with the 60 s default.
let pollForTrue (what: string) (condition: unit -> Task<bool>) =
    pollFor what (fun () ->
        task {
            let! ok = condition ()
            return (if ok then Some() else None)
        })

let private parse<'a> (body: string) = JsonSerializer.Deserialize<'a>(body, json)

/// A user of one server. `baseUri` is `https://<domain>`; the `HttpClient` already trusts the test CA
/// and reaches the server's Caddy front, so the URL host (and `Host`/SNI) stays the fake domain.
type FediClient(http: HttpClient, baseUri: string, token: string, username: string, domain: string) =

    let send (method': HttpMethod) (path: string) (form: (string * string) list option) =
        task {
            use request = new HttpRequestMessage(method', baseUri + path)
            request.Headers.Authorization <- Headers.AuthenticationHeaderValue("Bearer", token)
            form
            |> Option.iter (fun pairs ->
                request.Content <-
                    new FormUrlEncodedContent(pairs |> List.map (fun (k, v) -> Collections.Generic.KeyValuePair(k, v))))
            use! response = http.SendAsync request
            let! body = response.Content.ReadAsStringAsync()
            if not response.IsSuccessStatusCode then
                raise (FediClientException(method'.Method, path, int response.StatusCode, body))
            return body
        }

    let get path = send HttpMethod.Get path None
    let post path form = send HttpMethod.Post path (Some form)

    member _.Username = username
    member _.Domain = domain
    /// The fediverse handle without the leading `@`, e.g. `alice@gts.test`.
    member _.Handle = $"{username}@{domain}"
    member _.Http = http

    /// The user's own account (`verify_credentials`), which also proves the token.
    member _.Me() : Task<Account> =
        task {
            let! body = get "/api/v1/accounts/verify_credentials"
            return parse<Account> body
        }

    /// Searches accounts. With `resolve` the server does a WebFinger lookup for unknown remote handles.
    /// Returns the first match, if any (handles may be given with or without the leading `@`).
    member _.SearchAccount(handle: string, resolve: bool) : Task<Account option> =
        task {
            let q = Uri.EscapeDataString(if handle.StartsWith "@" then handle else "@" + handle)
            let! body = get $"/api/v2/search?q={q}&resolve={resolve.ToString().ToLowerInvariant()}&type=accounts&limit=5"
            use doc = JsonDocument.Parse body
            let accounts = doc.RootElement.GetProperty("accounts").GetRawText() |> parse<Account list>
            return List.tryHead accounts
        }

    /// Searches accounts with the query exactly as given (a pasted URL, not a handle)
    member _.SearchQuery(query: string, resolve: bool) : Task<Account option> =
        task {
            let! body = get $"/api/v2/search?q={Uri.EscapeDataString query}&resolve={resolve.ToString().ToLowerInvariant()}&type=accounts&limit=5"
            use doc = JsonDocument.Parse body
            let accounts = doc.RootElement.GetProperty("accounts").GetRawText() |> parse<Account list>
            return List.tryHead accounts
        }

    member _.Follow(accountId: string) : Task<Relationship> =
        task {
            let! body = post $"/api/v1/accounts/{accountId}/follow" []
            return parse<Relationship> body
        }

    member _.Unfollow(accountId: string) : Task<Relationship> =
        task {
            let! body = post $"/api/v1/accounts/{accountId}/unfollow" []
            return parse<Relationship> body
        }

    member _.Relationships(ids: string list) : Task<Relationship list> =
        task {
            let key = Uri.EscapeDataString "id[]"
            let query = ids |> List.map (fun id -> $"{key}={id}") |> String.concat "&"
            let! body = get $"/api/v1/accounts/relationships?{query}"
            return parse<Relationship list> body
        }

    /// The relationship with one account (a convenience over `Relationships`).
    member this.Relationship(accountId: string) : Task<Relationship> =
        task {
            let! all = this.Relationships [ accountId ]
            return List.exactlyOne all
        }

    /// Pending follow requests (accounts that asked to follow this locked user).
    member _.FollowRequests() : Task<Account list> =
        task {
            let! body = get "/api/v1/follow_requests"
            return parse<Account list> body
        }

    member _.AuthorizeFollowRequest(accountId: string) : Task<Relationship> =
        task {
            let! body = post $"/api/v1/follow_requests/{accountId}/authorize" []
            return parse<Relationship> body
        }

    member _.RejectFollowRequest(accountId: string) : Task<Relationship> =
        task {
            let! body = post $"/api/v1/follow_requests/{accountId}/reject" []
            return parse<Relationship> body
        }

    /// Locks the account (`manuallyApprovesFollowers`), so every follow needs approval.
    member _.LockAccount() : Task<Account> =
        task {
            let! body = send HttpMethod.Patch "/api/v1/accounts/update_credentials" (Some [ "locked", "true" ])
            return parse<Account> body
        }

    /// Changes the display name (the server then sends an `Update` of the actor to its followers)
    member _.UpdateDisplayName(name: string) : Task<Account> =
        task {
            let! body = send HttpMethod.Patch "/api/v1/accounts/update_credentials" (Some [ "display_name", name ])
            return parse<Account> body
        }

    member _.HomeTimeline() : Task<Status list> =
        task {
            let! body = get "/api/v1/timelines/home?limit=40"
            return parse<Status list> body
        }

    /// Asks the server to resolve a status URL (it fetches the object itself, signed as this user's server).
    /// Empty when the server could not fetch it, which is what a 404 from the origin looks like.
    member _.SearchStatus(url: string) : Task<Status list> =
        task {
            let q = Uri.EscapeDataString url
            let! body = get $"/api/v2/search?q={q}&resolve=true&type=statuses&limit=5"
            use doc = JsonDocument.Parse body
            return doc.RootElement.GetProperty("statuses").GetRawText() |> parse<Status list>
        }

    member _.GetStatus(id: string) : Task<Status> =
        task {
            let! body = get $"/api/v1/statuses/{id}"
            return parse<Status> body
        }

    /// Posts a status (`visibility`: public, unlisted, private, direct).
    member _.PostStatus(visibility: string, text: string) : Task<Status> =
        task {
            let! body = post "/api/v1/statuses" [ "status", text; "visibility", visibility ]
            return parse<Status> body
        }

    /// Replies to the status with this local id (`visibility`: public, unlisted, private, direct). A `spoiler` makes
    /// it a content warning, with `sensitive` set to match.
    member _.PostReply(inReplyToId: string, visibility: string, text: string, spoiler: string option) : Task<Status> =
        task {
            let spoilerFields =
                match spoiler with
                | Some warning -> [ "spoiler_text", warning; "sensitive", "true" ]
                | None -> []

            let! body = post "/api/v1/statuses" ([ "status", text; "visibility", visibility; "in_reply_to_id", inReplyToId ] @ spoilerFields)
            return parse<Status> body
        }

    /// Edits one of the user's own statuses (`PUT /api/v1/statuses/:id`); the server sends an `Update` of the Note
    member _.EditStatus(id: string, text: string, spoiler: string option) : Task<Status> =
        task {
            let spoilerFields =
                match spoiler with
                | Some warning -> [ "spoiler_text", warning; "sensitive", "true" ]
                | None -> []

            let! body = send HttpMethod.Put $"/api/v1/statuses/{id}" (Some([ "status", text ] @ spoilerFields))
            return parse<Status> body
        }

    /// Deletes one of the user's own statuses; the server sends a `Delete`
    member _.DeleteStatus(id: string) : Task =
        task {
            let! _ = send HttpMethod.Delete $"/api/v1/statuses/{id}" None
            ()
        }

    interface IDisposable with
        member _.Dispose() = http.Dispose()
