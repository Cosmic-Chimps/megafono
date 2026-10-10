/// The sample app's UI: plain server-rendered HTML, no JavaScript
module SampleApp.Pages

open System.Net
open System.Text.Json
open SampleApp.Users

let private enc (s: string) = WebUtility.HtmlEncode s

let private layout (title: string) (head: string) (body: string) =
    $"<!doctype html><html><head><meta charset=\"utf-8\"><title>{enc title}</title>{head}</head><body><h1>{enc title}</h1>{body}</body></html>"

let index () =
    let items = all |> List.map (fun u -> $"<li><a href=\"/u/{u.Handle}\">@{u.Handle}</a> (<a href=\"/u/{u.Handle}/manage\">manage</a>)</li>")
    layout "Sample app" "" ("<ul>" + String.concat "" items + "</ul>")

/// `actorUrl` goes into the `rel=alternate` link (FR-005)
let profile (u: User) (actorUrl: string) =
    let head = $"<link rel=\"alternate\" type=\"application/activity+json\" href=\"{enc actorUrl}\">"
    layout u.Name head $"<p>@{u.Handle}</p>{u.Summary}"

let private people (json: string) (buttons: string -> string) =
    use doc = JsonDocument.Parse json
    let rows =
        [ for item in doc.RootElement.GetProperty("items").EnumerateArray() ->
            let handle = item.GetProperty("actor").GetProperty("handle").GetString()
            let followId = item.GetProperty("followId").GetString()
            $"<li>{enc handle} {buttons followId}</li>" ]
    "<ul>" + String.concat "" rows + "</ul>"

/// The management page: pending requests with approve and reject, followers, a publish form, received events.
/// `followers` and `pending` are the bodies of the internal API's followers list; `events` are raw webhook bodies.
let manage (u: User) (followers: string) (pending: string) (events: string list) =
    let button (verb: string) (id: string) = $"<form method=\"post\" action=\"/u/{u.Handle}/follow-requests/{id}/{verb}\" style=\"display:inline\"><button>{verb}</button></form>"
    let evs = events |> List.map (fun e -> $"<li><code>{enc e}</code></li>")

    layout $"Manage @{u.Handle}" ""
        $"""<h2>Pending follow requests</h2>{people pending (fun id -> button "approve" id + button "reject" id)}
<h2>Followers</h2>{people followers (fun _ -> "")}
<h2>Publish</h2>
<form method="post" action="/u/{u.Handle}/publish">
<select name="audience"><option>public</option><option>unlisted</option><option selected>followers</option></select>
<input name="text" required> <button>Publish</button></form>
<h2>Events received</h2><ul>{String.concat "" evs}</ul>"""
