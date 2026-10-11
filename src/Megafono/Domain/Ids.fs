/// Id validation and the IRIs Megáfono publishes. Pure; the domain comes from configuration.
module Megafono.Domain.Ids

open System
open System.Text.RegularExpressions

// \z, not $: in .NET `$` also matches before a trailing newline
let private appUserIdPattern = Regex(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled)
// Inner dashes only: Mastodon refuses a remote username that starts or ends with one. No dots, so a handle
// cannot look like a domain (WebFinger relies on it).
let private handlePattern = Regex(@"^[a-z0-9_]([a-z0-9_-]{0,28}[a-z0-9_])?\z", RegexOptions.Compiled)

/// The app's immutable user id (FR-004); case is significant
let isValidAppUserId (id: string) = not (isNull id) && appUserIdPattern.IsMatch id

/// A handle exactly as stored: already lowercase
let isValidHandle (handle: string) = not (isNull handle) && handlePattern.IsMatch handle

/// Lowercases, then validates; the app may send mixed case
let normalizeHandle (handle: string) : string option =
    if isNull handle then
        None
    else
        let lowered = handle.ToLowerInvariant()
        if isValidHandle lowered then Some lowered else None

let private origin (domain: string) = $"https://{domain}"

// Actors (FR-005)
let actorId (domain: string) (appUserId: string) = $"{origin domain}/ap/actors/{appUserId}"
let actorKeyId (domain: string) (appUserId: string) = $"{actorId domain appUserId}#main-key"
let actorInbox (domain: string) (appUserId: string) = $"{actorId domain appUserId}/inbox"
let actorOutbox (domain: string) (appUserId: string) = $"{actorId domain appUserId}/outbox"
let actorFollowers (domain: string) (appUserId: string) = $"{actorId domain appUserId}/followers"
let actorFollowing (domain: string) (appUserId: string) = $"{actorId domain appUserId}/following"

// Instance actor (FR-022)
let instanceActorId (domain: string) = $"{origin domain}/ap/actor"
let instanceActorKeyId (domain: string) = $"{instanceActorId domain}#main-key"
let instanceActorInbox (domain: string) = $"{instanceActorId domain}/inbox"

let sharedInbox (domain: string) = $"{origin domain}/inbox"

// Objects and activities: UUIDs, never derived from content
let objectId (domain: string) (id: Guid) = $"{origin domain}/ap/objects/{id:D}"
let activityId (domain: string) (id: Guid) = $"{origin domain}/ap/activities/{id:D}"
