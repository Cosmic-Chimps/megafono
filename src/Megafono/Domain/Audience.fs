/// Audiences and their addressing (research R-06). Pure. The same `to`/`cc` go on the object and on its
/// activity, and the followers value is exactly the actor's `followers` URL.
module Megafono.Domain.Audience

open System

type Audience =
    | Public
    | Unlisted
    | Followers

/// The audience as the internal API and the `objects.audience` column spell it
let name (audience: Audience) : string =
    match audience with
    | Public -> "public"
    | Unlisted -> "unlisted"
    | Followers -> "followers"

/// None for anything else (`mutuals` and `direct` are later features, so they are invalid for now)
let parse (text: string) : Audience option =
    match text with
    | "public" -> Some Public
    | "unlisted" -> Some Unlisted
    | "followers" -> Some Followers
    | _ -> None

let publicCollection = "https://www.w3.org/ns/activitystreams#Public"

type Addressing = { To: string list; Cc: string list }

/// | audience  | to          | cc          |
/// |-----------|-------------|-------------|
/// | public    | [Public]    | [followers] |
/// | unlisted  | [followers] | [Public]    |
/// | followers | [followers] | []          |
let addressing (followersUrl: string) (audience: Audience) : Addressing =
    match audience with
    | Public -> { To = [ publicCollection ]; Cc = [ followersUrl ] }
    | Unlisted -> { To = [ followersUrl ]; Cc = [ publicCollection ] }
    | Followers -> { To = [ followersUrl ]; Cc = [] }

/// Only a followers-only object needs a signed fetch; public and unlisted ones are served to anyone
let isPrivate (audience: Audience) : bool = audience = Followers

/// How an inbound object was addressed (feature 002, research R-22). Unlike `Audience` this includes `direct`,
/// which Megáfono receives but never publishes.
type Received =
    | ReceivedPublic
    | ReceivedUnlisted
    | ReceivedFollowers
    | ReceivedDirect

let receivedName (audience: Received) : string =
    match audience with
    | ReceivedPublic -> "public"
    | ReceivedUnlisted -> "unlisted"
    | ReceivedFollowers -> "followers"
    | ReceivedDirect -> "direct"

let receivedFromName (text: string) : Received option =
    match text with
    | "public" -> Some ReceivedPublic
    | "unlisted" -> Some ReceivedUnlisted
    | "followers" -> Some ReceivedFollowers
    | "direct" -> Some ReceivedDirect
    | _ -> None

/// Servers spell the public collection three ways
let private isPublic (iri: string) = iri = publicCollection || iri = "as:Public" || iri = "Public"

/// `public` when Public is in `to`; `unlisted` when it is in `cc`; `followers` when `to` holds the author's own
/// followers collection; anything else (only mentioned actors) is `direct`.
let fromAddressing (authorFollowers: string option) (to': string list) (cc: string list) : Received =
    if to' |> List.exists isPublic then ReceivedPublic
    elif cc |> List.exists isPublic then ReceivedUnlisted
    elif authorFollowers |> Option.exists (fun f -> List.contains f to') then ReceivedFollowers
    else ReceivedDirect

/// Replies carry the audiences a thread collection may list: public and unlisted only
let isListable (audience: Received) : bool = audience = ReceivedPublic || audience = ReceivedUnlisted
