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
