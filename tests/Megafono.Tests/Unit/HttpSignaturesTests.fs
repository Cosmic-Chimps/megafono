module Megafono.Tests.Unit.HttpSignaturesTests

open System
open System.Security.Cryptography
open System.Text
open Xunit
open Megafono.Infrastructure
open Megafono.Infrastructure.HttpSignatures
open Megafono.Tests.TestSupport

// TODO(T085): add verification of real Mastodon and GoToSocial request samples once the interop runs capture them
// into tests/Megafono.Tests/Fixtures/payloads/.

let private key = lazy (RSA.Create 2048)
let private otherKey = lazy (RSA.Create 2048)
let private keyId = "https://remote.example/users/alex#main-key"
let private now = DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero)
let private body = Encoding.UTF8.GetBytes """{"type":"Follow"}"""

let private ok (result: Result<'a, VerifyError>) =
    match result with
    | Ok value -> value
    | Error e -> failwith (describe e)

let private verifyAt (time: DateTimeOffset) (request: SignedRequest) =
    draftCavage.Verify (FakeTime time) key.Value request

let private verify = verifyAt now

/// A request exactly as the sender's HTTP client would put it on the wire
let private signedRequest (httpMethod: string) (url: string) (payload: byte[] option) (at: DateTimeOffset) : SignedRequest =
    let uri = Uri url

    { Method = httpMethod
      PathAndQuery = uri.PathAndQuery
      Headers = ("Host", uri.Authority) :: sign keyId key.Value at httpMethod uri payload
      Body = defaultArg payload [||] }

let private post () = signedRequest "POST" "https://local.example/inbox" (Some body) now
let private get () = signedRequest "GET" "https://local.example/ap/actors/u1?x=1" None now

let private without (name: string) (request: SignedRequest) =
    { request with Headers = request.Headers |> List.filter (fun (n, _) -> not (String.Equals(n, name, StringComparison.OrdinalIgnoreCase))) }

let private withHeader (name: string) (value: string) (request: SignedRequest) =
    { without name request with Headers = (name, value) :: (without name request).Headers }

/// Signs by hand with whatever algorithm label and header list a test needs
let private handSigned (algorithm: string) (declared: string list) (request: SignedRequest) =
    let text: string = ok (signingString declared request)
    let signature = Convert.ToBase64String(key.Value.SignData(Encoding.UTF8.GetBytes text, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
    let value = $"keyId=\"{keyId}\",algorithm=\"{algorithm}\",headers=\"{String.Join(' ', declared)}\",signature=\"{signature}\""
    withHeader "Signature" value request

let private isError (expected: VerifyError) (result: Result<unit, VerifyError>) = Assert.Equal(Error expected, result)

// ---- parsing ---------------------------------------------------------------------------------------------

[<Fact>]
let ``R-01 parses the usual Mastodon header`` () =
    let header =
        "keyId=\"https://m.example/users/x#main-key\",algorithm=\"rsa-sha256\",headers=\"(request-target) host date digest content-type\",signature=\"YWJj\""

    let parsed = ok (parseSignatureHeader header)
    Assert.Equal("https://m.example/users/x#main-key", parsed.KeyId)
    Assert.Equal(Some "rsa-sha256", parsed.Algorithm)
    Assert.Equal<string list>([ "(request-target)"; "host"; "date"; "digest"; "content-type" ], parsed.Headers)
    Assert.Equal<byte[]>("abc"B, parsed.Signature)

[<Fact>]
let ``R-01 commas and escaped quotes inside quotes are data`` () =
    let parsed =
        ok (parseSignatureHeader "keyId=\"https://a.example/k?a=1,b=2\\\"x\",signature=\"YWJj\",headers=\"date\"")

    Assert.Equal("https://a.example/k?a=1,b=2\"x", parsed.KeyId)

[<Fact>]
let ``R-01 tolerates spaces, unquoted values and parameter order`` () =
    let parsed = ok (parseSignatureHeader "signature=\"YWJj\" ,  headers=\"(request-target) host date\" , algorithm=hs2019, keyId=\"k\"")
    Assert.Equal("k", parsed.KeyId)
    Assert.Equal(Some "hs2019", parsed.Algorithm)
    Assert.Equal<string list>([ "(request-target)"; "host"; "date" ], parsed.Headers)

[<Fact>]
let ``R-01 parameter names and header names are case-insensitive`` () =
    let parsed = ok (parseSignatureHeader "KeyId=\"k\",Algorithm=\"RSA-SHA256\",Headers=\"(Request-Target) Host DATE\",Signature=\"YWJj\"")
    Assert.Equal(Some "rsa-sha256", parsed.Algorithm)
    Assert.Equal<string list>([ "(request-target)"; "host"; "date" ], parsed.Headers)

[<Theory>]
[<InlineData("")>]
[<InlineData("garbage")>]
[<InlineData("keyId=\"k\"")>] // no signature
[<InlineData("signature=\"YWJj\"")>] // no keyId
[<InlineData("keyId=\"\",signature=\"YWJj\"")>]
[<InlineData("keyId=\"k\",signature=\"not base64!\"")>]
[<InlineData("keyId=\"k\",signature=\"YWJj")>] // unterminated quote
[<InlineData("keyId=\"k\",keyId=\"j\",signature=\"YWJj\"")>] // duplicate
[<InlineData("keyId=\"k\" signature=\"YWJj\"")>] // missing comma
let ``R-01 malformed headers are refused`` (header: string) =
    match parseSignatureHeader header with
    | Error(Malformed _) -> ()
    | other -> failwith $"expected Malformed, got {other}"

[<Fact>]
let ``R-01 an unknown algorithm is refused and a missing one tolerated`` () =
    Assert.Equal(Error(UnsupportedAlgorithm "rsa-sha1"), parseSignatureHeader "keyId=\"k\",algorithm=\"rsa-sha1\",signature=\"YWJj\"" |> Result.map ignore)
    Assert.True((parseSignatureHeader "keyId=\"k\",signature=\"YWJj\"").IsOk)

// ---- signing string --------------------------------------------------------------------------------------

[<Fact>]
let ``R-01 the signing string follows the declared order, with path and query`` () =
    let request =
        { Method = "POST"
          PathAndQuery = "/inbox?a=b"
          Headers = [ "Host", "local.example"; "Date", "Sat, 10 Oct 2026 12:00:00 GMT"; "Digest", "SHA-256=abc"; "Content-Type", "x" ]
          Body = [||] }

    Assert.Equal(
        Ok "(request-target): post /inbox?a=b\nhost: local.example\ndate: Sat, 10 Oct 2026 12:00:00 GMT\ndigest: SHA-256=abc",
        signingString [ "(request-target)"; "host"; "date"; "digest" ] request
    )

    Assert.Equal(
        Ok "digest: SHA-256=abc\n(request-target): post /inbox?a=b\nhost: local.example",
        signingString [ "digest"; "(request-target)"; "host" ] request
    )

[<Fact>]
let ``R-01 a declared header that is absent, or a pseudo-header we do not support, is an error`` () =
    let request = { Method = "GET"; PathAndQuery = "/"; Headers = [ "Host", "h" ]; Body = [||] }
    Assert.Equal(Error(HeaderMissing "date"), signingString [ "host"; "date" ] request)

    match signingString [ "(created)" ] request with
    | Error(Malformed _) -> ()
    | other -> failwith $"unexpected {other}"

// ---- round trips -----------------------------------------------------------------------------------------

[<Fact>]
let ``R-01 a signed POST verifies`` () = Assert.Equal(Ok(), verify (post ()))

[<Fact>]
let ``R-01 a signed GET verifies, with the query string in the target`` () = Assert.Equal(Ok(), verify (get ()))

[<Fact>]
let ``R-01 outbound POST signs request-target host date digest with rsa-sha256`` () =
    let headers = (post ()).Headers |> dict
    Assert.Contains("algorithm=\"rsa-sha256\"", headers["Signature"])
    Assert.Contains("headers=\"(request-target) host date digest\"", headers["Signature"])
    Assert.Contains(keyId, headers["Signature"])
    Assert.StartsWith("SHA-256=", headers["Digest"])
    Assert.EndsWith(" GMT", headers["Date"])

[<Fact>]
let ``R-01 outbound GET has no digest and signs request-target host date`` () =
    let headers = (get ()).Headers |> dict
    Assert.False(headers.ContainsKey "Digest")
    Assert.Contains("headers=\"(request-target) host date\"", headers["Signature"])

[<Fact>]
let ``R-01 the keyId can be extracted before verifying`` () =
    Assert.Equal(Ok keyId, draftCavage.ExtractKeyId(post ()))
    Assert.Equal(Error MissingSignature, draftCavage.ExtractKeyId(without "Signature" (post ())))

[<Fact>]
let ``R-01 hs2019 with an RSA key verifies like rsa-sha256`` () =
    let request = post () |> handSigned "hs2019" [ "(request-target)"; "host"; "date"; "digest" ]
    Assert.Equal(Ok(), verify request)

[<Fact>]
let ``R-01 extra signed headers such as content-type are covered`` () =
    let request =
        post ()
        |> withHeader "Content-Type" "application/activity+json"
        |> handSigned "rsa-sha256" [ "(request-target)"; "host"; "date"; "digest"; "content-type" ]

    Assert.Equal(Ok(), verify request)
    isError BadSignature (verify (withHeader "Content-Type" "text/plain" request))

[<Fact>]
let ``R-01 headers are looked up case-insensitively`` () =
    let request = post ()
    let lowered = { request with Headers = request.Headers |> List.map (fun (n, v) -> n.ToLowerInvariant(), v) }
    Assert.Equal(Ok(), verify lowered)

// ---- required headers ------------------------------------------------------------------------------------

[<Fact>]
let ``R-01 a POST whose signature does not cover the digest is refused`` () =
    let request = post () |> handSigned "rsa-sha256" [ "(request-target)"; "host"; "date" ]
    isError (HeaderNotSigned "digest") (verify request)

[<Fact>]
let ``R-01 a signature that does not cover host is refused`` () =
    let request = post () |> handSigned "rsa-sha256" [ "(request-target)"; "date"; "digest" ]
    isError (HeaderNotSigned "host") (verify request)

[<Fact>]
let ``R-01 a signature that does not cover date is refused`` () =
    let request = post () |> handSigned "rsa-sha256" [ "(request-target)"; "host"; "digest" ]
    isError (HeaderNotSigned "date") (verify request)

[<Fact>]
let ``R-01 a signature that does not cover request-target is refused`` () =
    let request = post () |> handSigned "rsa-sha256" [ "host"; "date"; "digest" ]
    isError (HeaderNotSigned "(request-target)") (verify request)

[<Fact>]
let ``R-01 a GET does not need a digest`` () =
    let request = get () |> handSigned "rsa-sha256" [ "(request-target)"; "host"; "date" ]
    Assert.Equal(Ok(), verify request)

[<Fact>]
let ``R-01 a signed header that was stripped from the request fails`` () =
    isError (HeaderMissing "date") (verify (without "Date" (get ())))
    isError DigestMissing (verify (without "Digest" (post ())))

[<Fact>]
let ``R-01 a request without a Signature header is refused`` () =
    isError MissingSignature (verify (without "Signature" (post ())))

// ---- date window -----------------------------------------------------------------------------------------

[<Fact>]
let ``FR-013 the date window is plus or minus 12 hours, edges included`` () =
    let request = signedRequest "GET" "https://local.example/x" None now
    let twelve = TimeSpan.FromHours 12.0
    let second = TimeSpan.FromSeconds 1.0

    Assert.Equal(Ok(), verifyAt (now + twelve) request)
    Assert.Equal(Ok(), verifyAt (now - twelve) request)
    isError DateOutOfWindow (verifyAt (now + twelve + second) request)
    isError DateOutOfWindow (verifyAt (now - twelve - second) request)

[<Fact>]
let ``FR-013 an unparsable date is out of the window`` () =
    // The date is part of the signed text, so sign the garbage by hand
    let request = get () |> withHeader "Date" "yesterday-ish" |> handSigned "rsa-sha256" [ "(request-target)"; "host"; "date" ]
    isError DateOutOfWindow (verify request)

// ---- digest ----------------------------------------------------------------------------------------------

[<Fact>]
let ``FR-012 a body that differs from the digest is refused`` () =
    let request = post ()
    isError DigestMismatch (verify { request with Body = Encoding.UTF8.GetBytes """{"type":"Delete"}""" })

[<Fact>]
let ``FR-012 verifyDigest accepts the prefix in any case and ignores other algorithms in the list`` () =
    let request = { Method = "POST"; PathAndQuery = "/"; Headers = []; Body = body }
    let value = digestValue body

    Assert.Equal(Ok(), verifyDigest { request with Headers = [ "Digest", value.Replace("SHA-256", "sha-256") ] })
    Assert.Equal(Ok(), verifyDigest { request with Headers = [ "Digest", "SHA-512=AAAA, " + value ] })
    isError DigestMismatch (verifyDigest { request with Headers = [ "Digest", "SHA-512=AAAA" ] })
    isError DigestMismatch (verifyDigest { request with Headers = [ "Digest", "SHA-256=!!!notbase64" ] })
    isError DigestMissing (verifyDigest request)

[<Fact>]
let ``FR-012 an empty body has a digest too`` () =
    let request = { Method = "POST"; PathAndQuery = "/"; Headers = [ "Digest", digestValue [||] ]; Body = [||] }
    Assert.Equal(Ok(), verifyDigest request)

// ---- keys and tampering ----------------------------------------------------------------------------------

[<Fact>]
let ``R-01 a different public key does not verify`` () =
    isError BadSignature (draftCavage.Verify (FakeTime now) otherKey.Value (post ()))

[<Fact>]
let ``R-01 a changed path or query breaks the signature`` () =
    let request = get ()
    isError BadSignature (verify { request with PathAndQuery = "/ap/actors/u2?x=1" })
    isError BadSignature (verify { request with PathAndQuery = "/ap/actors/u1?x=2" })

[<Fact>]
let ``R-01 a changed host header breaks the signature`` () =
    isError BadSignature (verify (withHeader "Host" "evil.example" (get ())))

[<Fact>]
let ``R-01 a corrupted signature is refused, not thrown`` () =
    let request = get ()
    let broken = withHeader "Signature" "keyId=\"k\",algorithm=\"rsa-sha256\",headers=\"(request-target) host date\",signature=\"AAAA\"" request
    isError BadSignature (verify broken)
