/// What the reply path (002) and the feed path (005) share when they read a Note or Article from the fediverse:
/// the object of an activity (trusted by 001 FR-015: embedded only when on the signer's host, else fetched signed from
/// its origin, attributed to the signer) and the readers of its fields.
module Megafono.Federation.InboxNotes

open System
open System.Globalization
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Megafono.Domain
open Megafono.Infrastructure
open Megafono.Infrastructure.RemoteActors
open Megafono.Federation.InboxCommon

let private isNoteType (node: JsonNode) =
    match str node "type" with
    | Some("Note" | "Article") -> true
    | _ -> false

/// True for an `Update` whose object is a Note or Article (a post edit), as opposed to a profile update
let isNoteUpdate (activity: JsonNode) : bool =
    match activity["object"] with
    | :? JsonObject as o -> isNoteType o
    | _ -> false

/// The note of an activity: the embedded one when it is on the signer's host, else fetched from its own origin.
/// It must be a Note or Article attributed to the signer.
let private fetchNote (env: Env) (id: string) : Task<JsonObject option> =
    task {
        match! RemoteActors.fetchObject env.Remote id with
        | Ok fetched when str fetched "id" = Some id -> return Some fetched
        | _ -> return None
    }

/// Reads the note an activity carries (`activity["object"]`), once
let noteOf (env: Env) (signer: RemoteActor) (node: JsonNode) : Task<JsonObject option> =
    task {
        let! candidate =
            match node with
            | :? JsonObject as o ->
                match str o "id" with
                | Some id when isOnSignerHost signer id -> Task.FromResult(Some o)
                | Some id -> fetchNote env id
                | None -> Task.FromResult None
            | :? JsonValue ->
                match idOf node with
                | Some id -> fetchNote env id
                | None -> Task.FromResult None
            | _ -> Task.FromResult None

        return candidate |> Option.filter (fun note -> isNoteType note && idOf note["attributedTo"] = Some signer.Id)
    }

// ---- reading the note ------------------------------------------------------------------------------------

/// The ids of a reference or a list of references
let iris (node: JsonNode) : string list =
    match node with
    | :? JsonArray as items -> [ for item in items do match idOf item with Some i -> yield i | None -> () ]
    | null -> []
    | single -> idOf single |> Option.toList

let time (node: JsonNode) (name: string) : DateTimeOffset option =
    match str node name with
    | Some text ->
        match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
        | true, t -> Some t
        | _ -> None
    | None -> None

let flag (node: JsonNode) (name: string) : bool =
    match node[name] with
    | :? JsonValue as v when v.GetValueKind() = JsonValueKind.True -> true
    | _ -> false

let private isHttp (allowHttp: bool) (text: string) =
    match Uri.TryCreate(text, UriKind.Absolute) with
    | true, u -> u.Scheme = Uri.UriSchemeHttps || (allowHttp && u.Scheme = Uri.UriSchemeHttp)
    | _ -> false

/// A URL given as a string, a Link (`href`) or a list of them: the first usable one
let urlOf (allowHttp: bool) (node: JsonNode) : string option =
    let candidates =
        match node with
        | :? JsonArray as items -> [ for item in items -> item ]
        | null -> []
        | single -> [ single ]

    candidates
    |> List.tryPick (fun candidate ->
        match candidate with
        | :? JsonValue -> idOf candidate
        | :? JsonObject as o -> str o "href" |> Option.orElse (str o "url")
        | _ -> None
        |> Option.filter (isHttp allowHttp))

let attachments (allowHttp: bool) (note: JsonObject) : Events.ReplyAttachment list =
    match note["attachment"] with
    | :? JsonArray as items ->
        [ for item in items do
              match item with
              | :? JsonObject as o ->
                  match urlOf allowHttp o["url"] with
                  | Some url ->
                      yield
                          { Type = defaultArg (str o "type") "Document"
                            MediaType = str o "mediaType"
                            Url = url
                            Name = str o "name" |> Option.filter (fun n -> n <> "") }
                  | None -> ()
              | _ -> () ]
    | _ -> []

/// The language of the content: the first `contentMap` key
let language (note: JsonObject) : string option =
    match note["contentMap"] with
    | :? JsonObject as map -> map |> Seq.tryHead |> Option.map (fun entry -> entry.Key)
    | _ -> None

/// The fields a reply and a feed post read from a note the same way: sanitized content, the content warning as text,
/// URLs and attachments (plain `http` only when private addresses are allowed, as in tests)
type NoteFields =
    { NoteId: string
      NoteUrl: string option
      NoteType: string
      Content: string
      Summary: string option
      Sensitive: bool
      Language: string option
      Attachments: Events.ReplyAttachment list }

/// Reads the shared fields of a note that has an `id` and a `type` (`noteOf` only returns such notes)
let fieldsOf (allowHttp: bool) (note: JsonObject) : NoteFields =
    { NoteId = (str note "id").Value
      NoteUrl = urlOf allowHttp note["url"]
      NoteType = (str note "type").Value
      Content = Sanitizer.sanitizeHtml (defaultArg (str note "content") "")
      Summary = str note "summary" |> Option.map Sanitizer.toText |> Option.filter (fun s -> s <> "")
      Sensitive = flag note "sensitive"
      Language = language note
      Attachments = attachments allowHttp note }

/// The ids a note is addressed to in `to` or `cc`; a field the note lacks is read from the activity
let private addressed (activity: JsonNode) (note: JsonObject) (name: string) : string list =
    if isNull note[name] then iris activity[name] else iris note[name]

/// Everyone the note is addressed to (`to` and `cc`)
let addressedTo (activity: JsonNode) (note: JsonObject) : string list =
    addressed activity note "to" @ addressed activity note "cc"

/// How the note was addressed, seen from the signer's followers collection
let audienceOf (signer: RemoteActor) (activity: JsonNode) (note: JsonObject) : Audience.Received =
    Audience.fromAddressing signer.FollowersUrl (addressed activity note "to") (addressed activity note "cc")

/// The app's own extension data on a note (FR-046): the `namespaces` the app declared, matched by the full namespace
/// against the terms the activity and the note declare, whatever prefix they chose
let extensionData (namespaces: Set<string>) (activityContext: JsonNode) (note: JsonObject) : JsonNode option =
    let terms = ActivityStreams.extensionTerms activityContext @ ActivityStreams.extensionTerms note["@context"]

    terms
    |> List.tryPick (fun (prefix, ns) ->
        if namespaces.Contains ns then
            let data = JsonObject()

            for entry in note do
                if entry.Key.StartsWith(prefix + ":", StringComparison.Ordinal) then
                    data[entry.Key.Substring(prefix.Length + 1)] <- if isNull entry.Value then null else entry.Value.DeepClone()

            if data.Count = 0 then
                None
            else
                let node = JsonObject()
                node["namespace"] <- JsonValue.Create ns
                node["prefix"] <- JsonValue.Create prefix
                node["data"] <- data
                Some(node :> JsonNode)
        else
            None)
