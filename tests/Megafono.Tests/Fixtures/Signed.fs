/// Sending requests to Megáfono signed by a `StubRemote`, and the JSON bodies of follow traffic
module Megafono.Tests.Fixtures.Signed

open System
open System.Net.Http
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks

/// A POST with the stub's signature headers (`sign` may alter them, e.g. to make the date stale)
let private build (url: string) (body: byte[]) (headers: (string * string) list) : HttpRequestMessage =
    let request = new HttpRequestMessage(HttpMethod.Post, url)
    let content = new ByteArrayContent(body)
    content.Headers.TryAddWithoutValidation("Content-Type", "application/activity+json") |> ignore
    request.Content <- content

    for name, value in headers do
        request.Headers.TryAddWithoutValidation(name, value) |> ignore

    request

/// Signed as `name` on `stub`, to `url`, with the real key and the current time
let post (client: HttpClient) (stub: StubRemote.StubRemote) (name: string) (url: string) (body: byte[]) : Task<HttpResponseMessage> =
    task {
        use request = build url body (stub.Sign(name, "POST", url, Some body))
        return! client.SendAsync request
    }

let postText (client: HttpClient) (stub: StubRemote.StubRemote) (name: string) (url: string) (body: string) =
    post client stub name url (Encoding.UTF8.GetBytes body)

/// Sends exactly these headers (for requests that are broken on purpose)
let postRaw (client: HttpClient) (url: string) (body: byte[]) (headers: (string * string) list) : Task<HttpResponseMessage> =
    task {
        use request = build url body headers
        return! client.SendAsync request
    }

let private context = "\"@context\":\"https://www.w3.org/ns/activitystreams\""

/// A Follow by `stub`'s `name` of `targetActorId`; `suffix` makes the activity id unique
let followBody (stub: StubRemote.StubRemote) (name: string) (suffix: string) (targetActorId: string) : string =
    $"""{{{context},"id":"{stub.ActorId name}/follows/{suffix}","type":"Follow","actor":"{stub.ActorId name}","object":"{targetActorId}"}}"""

let followId (stub: StubRemote.StubRemote) (name: string) (suffix: string) = $"{stub.ActorId name}/follows/{suffix}"

/// Undo of a Follow, with the Follow embedded
let undoEmbedded (stub: StubRemote.StubRemote) (name: string) (suffix: string) (targetActorId: string) : string =
    let id = followId stub name suffix

    $"""{{{context},"id":"{stub.ActorId name}/undo/{suffix}","type":"Undo","actor":"{stub.ActorId name}","object":{{"id":"{id}","type":"Follow","actor":"{stub.ActorId name}","object":"{targetActorId}"}}}}"""

/// Undo of a Follow by its bare id
let undoBare (stub: StubRemote.StubRemote) (name: string) (suffix: string) : string =
    $"""{{{context},"id":"{stub.ActorId name}/undo/{suffix}","type":"Undo","actor":"{stub.ActorId name}","object":"{followId stub name suffix}"}}"""

let ourActor (userId: string) = $"https://megafono.test/ap/actors/{userId}"

// ---- replies (feature 002) ---------------------------------------------------------------------------------

let publicIri = "https://www.w3.org/ns/activitystreams#Public"

let noteId (stub: StubRemote.StubRemote) (name: string) (suffix: string) = $"{stub.ActorId name}/statuses/{suffix}"

/// A public reply of `name` to `inReplyTo`, the way Mastodon addresses it: `to` Public, `cc` the author's followers
/// and the replied-to actor. Tests change what they need on the returned object.
let note (stub: StubRemote.StubRemote) (name: string) (suffix: string) (inReplyTo: string) : JsonObject =
    let strings (items: string list) = JsonArray(items |> List.map (fun i -> JsonValue.Create i :> JsonNode) |> Array.ofList)
    let o = JsonObject()
    o["id"] <- JsonValue.Create(noteId stub name suffix)
    o["type"] <- JsonValue.Create "Note"
    o["attributedTo"] <- JsonValue.Create(stub.ActorId name)
    o["inReplyTo"] <- JsonValue.Create inReplyTo
    o["content"] <- JsonValue.Create "<p>that ending!</p>"
    o["published"] <- JsonValue.Create "2026-10-12T17:59:58Z"
    o["url"] <- JsonValue.Create $"{stub.BaseUrl}/@{name}/{suffix}"
    o["to"] <- strings [ publicIri ]
    o["cc"] <- strings [ $"{stub.ActorId name}/followers" ]
    o["sensitive"] <- JsonValue.Create false
    o

let setAddressing (note: JsonObject) (toList: string list) (ccList: string list) =
    let strings (items: string list) = JsonArray(items |> List.map (fun i -> JsonValue.Create i :> JsonNode) |> Array.ofList)
    note["to"] <- strings toList
    note["cc"] <- strings ccList

let private activityOf (stub: StubRemote.StubRemote) (name: string) (id: string) (kind: string) (note: JsonObject) (extraContext: JsonNode) : string =
    let o = JsonObject()
    o["@context"] <- (if isNull extraContext then JsonValue.Create "https://www.w3.org/ns/activitystreams" :> JsonNode else extraContext)
    o["id"] <- JsonValue.Create id
    o["type"] <- JsonValue.Create kind
    o["actor"] <- JsonValue.Create(stub.ActorId name)
    o["to"] <- note["to"].DeepClone()
    o["cc"] <- note["cc"].DeepClone()
    o["object"] <- note.DeepClone()
    o.ToJsonString()

/// `Create` of `note`, with the Note embedded; its id is the Note's id plus `/activity`
let createNote (stub: StubRemote.StubRemote) (name: string) (suffix: string) (note: JsonObject) : string =
    activityOf stub name $"{noteId stub name suffix}/activity" "Create" note null

/// The same with extra `@context` (an app namespace term)
let createNoteWithContext (stub: StubRemote.StubRemote) (name: string) (suffix: string) (note: JsonObject) (context: JsonNode) : string =
    activityOf stub name $"{noteId stub name suffix}/activity" "Create" note context

/// `Update` of `note`; `edit` makes the activity id unique per edit
let updateNote (stub: StubRemote.StubRemote) (name: string) (suffix: string) (edit: string) (note: JsonObject) : string =
    activityOf stub name $"{noteId stub name suffix}#updates/{edit}" "Update" note null

/// `Delete` of a reply by its bare id, or with a Tombstone object (Mastodon's form)
let deleteNote (stub: StubRemote.StubRemote) (name: string) (suffix: string) (tombstone: bool) : string =
    let o = JsonObject()
    o["@context"] <- JsonValue.Create "https://www.w3.org/ns/activitystreams"
    o["id"] <- JsonValue.Create $"{noteId stub name suffix}#delete"
    o["type"] <- JsonValue.Create "Delete"
    o["actor"] <- JsonValue.Create(stub.ActorId name)
    o["to"] <- JsonArray(JsonValue.Create publicIri)

    if tombstone then
        let t = JsonObject()
        t["id"] <- JsonValue.Create(noteId stub name suffix)
        t["type"] <- JsonValue.Create "Tombstone"
        o["object"] <- t
    else
        o["object"] <- JsonValue.Create(noteId stub name suffix)

    o.ToJsonString()
