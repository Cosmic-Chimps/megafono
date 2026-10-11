/// Sending requests to Megáfono signed by a `StubRemote`, and the JSON bodies of follow traffic
module Megafono.Tests.Fixtures.Signed

open System
open System.Net.Http
open System.Text
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
