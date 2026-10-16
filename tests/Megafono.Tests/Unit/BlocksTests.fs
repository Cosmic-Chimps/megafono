module Megafono.Tests.Unit.BlocksTests

open Xunit
open Megafono.Infrastructure

// ---- normalizeDomain (R-42) ------------------------------------------------------------------------------

[<Theory>]
[<InlineData("spam.example", "spam.example")>]
[<InlineData("SPAM.Example", "spam.example")>]
[<InlineData("  spam.example  ", "spam.example")>]
[<InlineData("spam.example.", "spam.example")>]
[<InlineData("a.b.spam.example", "a.b.spam.example")>]
[<InlineData("localhost", "localhost")>]
[<InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")>]
let ``004 R-42 a host name is stored lowercase, without its trailing dot`` (input: string, expected: string) =
    Assert.Equal(Some expected, Blocks.normalizeDomain input)

[<Fact>]
let ``004 R-42 an internationalized name is stored as punycode`` () =
    Assert.Equal(Some "xn--bcher-kva.example", Blocks.normalizeDomain "bücher.example")
    Assert.Equal(Some "xn--bcher-kva.example", Blocks.normalizeDomain "BÜCHER.example")

[<Theory>]
[<InlineData("spam.example:8443")>]
[<InlineData("https://spam.example")>]
[<InlineData("spam.example/path")>]
[<InlineData("spam.example?x=1")>]
[<InlineData("spam.example#frag")>]
[<InlineData("bob@spam.example")>]
[<InlineData("spam example")>]
[<InlineData("spam.example\\x")>]
[<InlineData("")>]
[<InlineData("   ")>]
[<InlineData(".")>]
[<InlineData("a..spam.example")>]
[<InlineData(".spam.example")>]
[<InlineData("-spam.example")>]
[<InlineData("spam-.example")>]
[<InlineData("sp_am.example")>]
let ``004 R-42 anything that is not a bare host name is refused`` (input: string) =
    Assert.Equal(None, Blocks.normalizeDomain input)

[<Fact>]
let ``004 R-42 a null is refused, a label over 63 characters is refused, 63 is accepted`` () =
    Assert.Equal(None, Blocks.normalizeDomain null)
    let ok = System.String('a', 63) + ".example"
    let tooLong = System.String('a', 64) + ".example"
    Assert.Equal(Some ok, Blocks.normalizeDomain ok)
    Assert.Equal(None, Blocks.normalizeDomain tooLong)

// ---- hostName and domainMatches --------------------------------------------------------------------------

[<Theory>]
[<InlineData("spam.example", "spam.example")>]
[<InlineData("Spam.Example:8443", "spam.example")>]
[<InlineData("localhost:5000", "localhost")>]
let ``004 R-42 hostName drops the port and lowercases`` (authority: string, expected: string) =
    Assert.Equal(expected, Blocks.hostName authority)

[<Theory>]
[<InlineData("spam.example", true)>]
[<InlineData("a.spam.example", true)>]
[<InlineData("a.b.spam.example", true)>]
[<InlineData("a.spam.example:8443", true)>]
[<InlineData("SPAM.example", true)>]
[<InlineData("notspam.example", false)>]
[<InlineData("spam.example.org", false)>]
[<InlineData("example", false)>]
[<InlineData("other.example", false)>]
let ``004 FR-005 domainMatches covers the domain and its subdomains, never a lookalike`` (host: string, expected: bool) =
    Assert.Equal(expected, Blocks.domainMatches "spam.example" host)
