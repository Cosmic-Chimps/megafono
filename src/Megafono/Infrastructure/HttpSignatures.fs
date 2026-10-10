/// HTTP Signatures, draft-cavage (research R-01). Pure functions over a small request record, so the
/// rules are testable without HTTP. Verification sits behind `SignatureScheme` so RFC 9421 can join later.
module Megafono.Infrastructure.HttpSignatures

open System
open System.Globalization
open System.Security.Cryptography
open System.Text
open Megafono.Domain

/// What a signature covers: the method, the raw path plus query, the headers and the body bytes
type SignedRequest =
    { Method: string
      /// As received, query string included: GTS signs it that way
      PathAndQuery: string
      Headers: (string * string) list
      Body: byte[] }

type ParsedSignature =
    { KeyId: string
      /// Lowercase; None when the sender left it out
      Algorithm: string option
      /// Lowercase names the sender declared, in signing order
      Headers: string list
      Signature: byte[] }

type VerifyError =
    | MissingSignature
    | Malformed of string
    | UnsupportedAlgorithm of string
    /// A header that must be signed is not in the declared list
    | HeaderNotSigned of string
    /// A declared header is absent from the request
    | HeaderMissing of string
    | DateOutOfWindow
    | DigestMissing
    | DigestMismatch
    /// The one failure a key refetch (rotation) can fix
    | BadSignature

let describe (error: VerifyError) : string =
    match error with
    | MissingSignature -> "no Signature header"
    | Malformed reason -> $"malformed Signature header: {reason}"
    | UnsupportedAlgorithm name -> $"unsupported algorithm '{name}'"
    | HeaderNotSigned name -> $"header '{name}' is not covered by the signature"
    | HeaderMissing name -> $"signed header '{name}' is missing from the request"
    | DateOutOfWindow -> "Date is more than 12 hours from now"
    | DigestMissing -> "no Digest header"
    | DigestMismatch -> "Digest does not match the body"
    | BadSignature -> "signature does not verify"

let private requestTarget = "(request-target)"
let private maxClockSkew = TimeSpan.FromHours 12.0

/// All values of a header joined with ", ", looked up case-insensitively
let header (name: string) (request: SignedRequest) : string option =
    request.Headers
    |> List.filter (fun (n, _) -> String.Equals(n, name, StringComparison.OrdinalIgnoreCase))
    |> List.map (fun (_, v) -> v.Trim())
    |> function
        | [] -> None
        | values -> Some(String.Join(", ", values))

// ---- parsing ---------------------------------------------------------------------------------------------

/// Quote-aware `key="value",key2=value2` tokenizer: commas and escaped quotes inside quotes are data
let parseParams (text: string) : Result<Map<string, string>, string> =
    let n = text.Length

    let rec skipSpaces i =
        if i < n && Char.IsWhiteSpace text[i] then skipSpaces (i + 1) else i

    // Reads a quoted value starting after the opening quote; returns the value and the index after the closing quote
    let readQuoted start =
        let value = StringBuilder()

        let rec go i =
            if i >= n then None
            elif text[i] = '\\' && i + 1 < n then
                value.Append text[i + 1] |> ignore
                go (i + 2)
            elif text[i] = '"' then Some(value.ToString(), i + 1)
            else
                value.Append text[i] |> ignore
                go (i + 1)

        go start

    let rec loop i (acc: Map<string, string>) =
        let i = skipSpaces i

        if i >= n then
            Ok acc
        else
            let eq = text.IndexOf('=', i)
            let key = if eq < 0 then "" else text.Substring(i, eq - i).Trim().ToLowerInvariant()

            if key = "" || key.Contains ',' || key.Contains '"' then
                Error $"expected a parameter name at position {i}"
            elif acc.ContainsKey key then
                Error $"parameter '{key}' appears twice"
            else
                let valueStart = skipSpaces (eq + 1)

                let value =
                    if valueStart < n && text[valueStart] = '"' then
                        readQuoted (valueStart + 1) |> Option.map Ok |> Option.defaultValue (Error "unterminated quoted value")
                    else
                        let comma = text.IndexOf(',', valueStart)
                        let stop = if comma < 0 then n else comma
                        Ok(text.Substring(valueStart, stop - valueStart).Trim(), stop)

                match value with
                | Error e -> Error e
                | Ok(v, next) ->
                    let next = skipSpaces next

                    if next >= n then Ok(acc.Add(key, v))
                    elif text[next] = ',' then loop (next + 1) (acc.Add(key, v))
                    else Error $"expected ',' at position {next}"

    loop 0 Map.empty

let private acceptedAlgorithms = set [ "rsa-sha256"; "hs2019" ]

let parseSignatureHeader (value: string) : Result<ParsedSignature, VerifyError> =
    result {
        let! parameters = parseParams value |> Result.mapError Malformed

        let! keyId =
            match parameters.TryFind "keyid" with
            | Some id when id <> "" -> Ok id
            | _ -> Error(Malformed "keyId is missing")

        let! signature =
            match parameters.TryFind "signature" with
            | None -> Error(Malformed "signature is missing")
            | Some s ->
                try
                    Ok(Convert.FromBase64String s)
                with :? FormatException ->
                    Error(Malformed "signature is not base64")

        let algorithm = parameters.TryFind "algorithm" |> Option.map (fun a -> a.ToLowerInvariant())

        // A missing algorithm is tolerated (the draft defaults it); a named one must be known
        do!
            match algorithm with
            | Some a when not (acceptedAlgorithms.Contains a) -> Error(UnsupportedAlgorithm a)
            | _ -> Ok()

        // The draft's default when `headers` is absent is just `date`, which the required-header check then refuses
        let headers =
            parameters.TryFind "headers"
            |> Option.defaultValue "date"
            |> fun h -> h.ToLowerInvariant().Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        return
            { KeyId = keyId
              Algorithm = algorithm
              Headers = headers
              Signature = signature }
    }

// ---- checks ----------------------------------------------------------------------------------------------

let private isPost (request: SignedRequest) =
    String.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase)

/// `(request-target)`, `host` and `date` must be signed, and `digest` on POST
let checkRequiredHeaders (request: SignedRequest) (parsed: ParsedSignature) : Result<unit, VerifyError> =
    let required =
        [ requestTarget; "host"; "date" ] @ (if isPost request then [ "digest" ] else [])

    match required |> List.tryFind (fun name -> not (List.contains name parsed.Headers)) with
    | Some missing -> Error(HeaderNotSigned missing)
    | None -> Ok()

/// `Date` within ±12 h of now, both ends inclusive
let checkDate (time: TimeProvider) (request: SignedRequest) : Result<unit, VerifyError> =
    match header "date" request with
    | None -> Error(HeaderMissing "date")
    | Some text ->
        let styles = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal

        match DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, styles) with
        | true, date when (time.GetUtcNow() - date).Duration() <= maxClockSkew -> Ok()
        | _ -> Error DateOutOfWindow

/// The `Digest` header value for a body
let digestValue (body: byte[]) : string =
    "SHA-256=" + Convert.ToBase64String(SHA256.HashData body)

/// Checks `Digest` against the raw bytes: SHA-256 only, the prefix matched case-insensitively.
/// Call it before parsing the body (constitution Principle III).
let verifyDigest (request: SignedRequest) : Result<unit, VerifyError> =
    match header "digest" request with
    | None -> Error DigestMissing
    | Some value ->
        let prefix = "sha-256="

        let claimed =
            value.Split(',')
            |> Array.map (fun part -> part.Trim())
            |> Array.tryFind (fun part -> part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun part -> part.Substring prefix.Length)

        match claimed with
        | None -> Error DigestMismatch
        | Some encoded ->
            try
                if CryptographicOperations.FixedTimeEquals(ReadOnlySpan<byte>(Convert.FromBase64String encoded), ReadOnlySpan<byte>(SHA256.HashData request.Body)) then
                    Ok()
                else
                    Error DigestMismatch
            with :? FormatException ->
                Error DigestMismatch

// ---- signing string --------------------------------------------------------------------------------------

/// One `name: value` line per declared header, in declared order, joined with `\n` (no trailing newline).
/// `(created)` and `(expires)` are not supported: fediverse servers sign with `date`.
let signingString (headerNames: string list) (request: SignedRequest) : Result<string, VerifyError> =
    let line (name: string) =
        if name = requestTarget then
            Ok $"{requestTarget}: {request.Method.ToLowerInvariant()} {request.PathAndQuery}"
        elif name.StartsWith '(' then
            Error(Malformed $"pseudo-header {name} is not supported")
        else
            match header name request with
            | Some value -> Ok $"{name}: {value}"
            | None -> Error(HeaderMissing name)

    headerNames
    |> List.fold
        (fun acc name ->
            match acc with
            | Error e -> Error e
            | Ok lines -> line name |> Result.map (fun l -> l :: lines))
        (Ok [])
    |> Result.map (fun lines -> String.Join("\n", List.rev lines))

// ---- verification ----------------------------------------------------------------------------------------

/// A signature scheme the inbox can verify; draft-cavage today, RFC 9421 later
type SignatureScheme =
    { Name: string
      /// The keyId, so the caller can find the key before verifying
      ExtractKeyId: SignedRequest -> Result<string, VerifyError>
      /// Full verification against an RSA public key: required headers, date window, digest, signature
      Verify: TimeProvider -> RSA -> SignedRequest -> Result<unit, VerifyError> }

let private parseFrom (request: SignedRequest) =
    match header "signature" request with
    | None -> Error MissingSignature
    | Some value -> parseSignatureHeader value

let private verifyCavage (time: TimeProvider) (key: RSA) (request: SignedRequest) : Result<unit, VerifyError> =
    result {
        let! parsed = parseFrom request
        do! checkRequiredHeaders request parsed
        do! checkDate time request
        // POST must carry a digest (checked above); any other method is checked when it signed one
        do! if isPost request || List.contains "digest" parsed.Headers then verifyDigest request else Ok()
        let! text = signingString parsed.Headers request

        let valid =
            try
                key.VerifyData(Encoding.UTF8.GetBytes text, parsed.Signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            with :? CryptographicException ->
                false

        if not valid then
            return! Error BadSignature
    }

let draftCavage: SignatureScheme =
    { Name = "draft-cavage"
      ExtractKeyId = fun request -> parseFrom request |> Result.map (fun p -> p.KeyId)
      Verify = verifyCavage }

// ---- signing ---------------------------------------------------------------------------------------------

/// Headers to add to an outbound request (R-01): `Date`, `Digest` when there is a body, and `Signature`
/// over `(request-target) host date [digest]` with `rsa-sha256`. The caller sends the request to `uri`, whose
/// authority becomes the `Host` header.
let sign (keyId: string) (key: RSA) (now: DateTimeOffset) (httpMethod: string) (uri: Uri) (body: byte[] option) : (string * string) list =
    let date = now.UtcDateTime.ToString("r", CultureInfo.InvariantCulture)
    let digest = body |> Option.map digestValue

    let request =
        { Method = httpMethod
          PathAndQuery = uri.PathAndQuery
          Headers = [ "host", uri.Authority; "date", date ] @ (digest |> Option.map (fun d -> "digest", d) |> Option.toList)
          Body = defaultArg body [||] }

    let names =
        [ requestTarget; "host"; "date" ] @ (if digest.IsSome then [ "digest" ] else [])

    let text =
        match signingString names request with
        | Ok t -> t
        | Error e -> failwith (describe e) // unreachable: every header was just added

    let signature =
        key.SignData(Encoding.UTF8.GetBytes text, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        |> Convert.ToBase64String

    [ "Date", date ]
    @ (digest |> Option.map (fun d -> "Digest", d) |> Option.toList)
    @ [ "Signature", $"keyId=\"{keyId}\",algorithm=\"rsa-sha256\",headers=\"{String.Join(' ', names)}\",signature=\"{signature}\"" ]
