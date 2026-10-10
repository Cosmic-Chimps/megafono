/// Remote HTML is sanitized to an allowlist before it reaches the app (research R-14, FR-056)
module Megafono.Infrastructure.Sanitizer

open AngleSharp.Html.Parser
open Ganss.Xss

let private allowedTags = [ "p"; "br"; "a"; "span"; "strong"; "em"; "b"; "i"; "ul"; "ol"; "li"; "blockquote"; "code"; "pre" ]
// Removed together with everything inside them: their content is code or data, never prose
let private dropWithContent =
    set [ "script"; "style"; "noscript"; "template"; "iframe"; "object"; "embed"; "applet"; "svg"; "math"; "head"; "title"; "textarea"; "select"; "option"; "noframes"; "xmp" ]

/// Wrapper tags that are not allowed lose the tag but keep their text; the ones above lose their content too
let private keepProseOnly (sanitizer: HtmlSanitizer) =
    sanitizer.KeepChildNodes <- true

    sanitizer.RemovingTag.Add(fun args ->
        if dropWithContent.Contains args.Tag.LocalName then
            args.Tag.InnerHtml <- "")

let private linkRel = "nofollow noopener noreferrer ugc"

let private createHtmlSanitizer () =
    let options = HtmlSanitizerOptions()
    for tag in allowedTags do options.AllowedTags.Add tag |> ignore
    for attribute in [ "href"; "rel"; "class" ] do options.AllowedAttributes.Add attribute |> ignore
    for scheme in [ "http"; "https"; "mailto" ] do options.AllowedSchemes.Add scheme |> ignore
    // Cleared defaults include the list of attributes that hold URLs: without this the scheme check never runs on href
    options.UriAttributes.Add "href" |> ignore
    let sanitizer = HtmlSanitizer options
    keepProseOnly sanitizer

    sanitizer.PostProcessNode.Add(fun args ->
        match args.Node with
        | :? AngleSharp.Dom.IElement as element ->
            // The sanitizer's own `rel` handling is not what we want: every link gets exactly ours
            if element.LocalName = "a" then
                element.SetAttribute("rel", linkRel)
            elif element.LocalName <> "span" then
                element.RemoveAttribute "class" |> ignore
        | _ -> ())

    sanitizer

// One shared instance; its configuration is never changed after this point
let private htmlSanitizer = createHtmlSanitizer ()

// No tags at all: used to drop markup and script/style content before reading the text
let private textSanitizer =
    let options = HtmlSanitizerOptions()
    options.AllowedTags.Clear()
    options.AllowedAttributes.Clear()
    let sanitizer = HtmlSanitizer options
    keepProseOnly sanitizer
    sanitizer

/// Sanitized HTML for `content` and `summary`: allowlisted tags, `href`/`rel`/`class` only, http, https and mailto links
let sanitizeHtml (html: string) : string =
    if System.String.IsNullOrEmpty html then "" else htmlSanitizer.Sanitize html

/// Plain text for `name`: tags removed, entities decoded, surrounding whitespace trimmed
let toText (html: string) : string =
    if System.String.IsNullOrEmpty html then
        ""
    else
        let stripped = textSanitizer.Sanitize html
        let document = HtmlParser().ParseDocument stripped
        document.Body.TextContent.Trim()
