module Megafono.Tests.Unit.SanitizerTests

open Xunit
open Megafono.Infrastructure

[<Fact>]
let ``R-14 script elements and their content are removed`` () =
    let html = Sanitizer.sanitizeHtml "<p>Hi</p><script>alert(1)</script>"
    Assert.Equal("<p>Hi</p>", html)

[<Fact>]
let ``R-14 style elements and their content are removed`` () =
    let html = Sanitizer.sanitizeHtml "<style>p { display: none }</style><p style=\"color:red\">Hi</p>"
    Assert.DoesNotContain("display", html)
    Assert.DoesNotContain("style", html)
    Assert.Contains("<p>Hi</p>", html)

[<Theory>]
[<InlineData("javascript:alert(1)")>]
[<InlineData("JaVaScRiPt:alert(1)")>]
[<InlineData("data:text/html;base64,PHNjcmlwdD4=")>]
[<InlineData("vbscript:msgbox(1)")>]
let ``R-14 dangerous link schemes lose their href`` (href: string) =
    let html = Sanitizer.sanitizeHtml $"<a href=\"{href}\">x</a>"
    Assert.DoesNotContain("href", html)
    Assert.DoesNotContain("alert", html)

[<Fact>]
let ``R-14 on* attributes are removed`` () =
    let html = Sanitizer.sanitizeHtml "<p onclick=\"x()\" onmouseover=\"y()\">Hi <b onerror=\"z()\">there</b></p>"
    Assert.DoesNotContain("onclick", html)
    Assert.DoesNotContain("onmouseover", html)
    Assert.DoesNotContain("onerror", html)
    Assert.Equal("<p>Hi <b>there</b></p>", html)

[<Fact>]
let ``R-14 allowed tags survive`` () =
    let input =
        "<p>a<br>b</p><strong>s</strong><em>e</em><b>b</b><i>i</i><ul><li>l</li></ul><ol><li>o</li></ol><blockquote>q</blockquote><code>c</code><pre>p</pre>"

    Assert.Equal(input, Sanitizer.sanitizeHtml input)

[<Fact>]
let ``R-14 other tags are dropped but their text stays`` () =
    let html = Sanitizer.sanitizeHtml "<div><h1>Title</h1><img src=\"https://x.example/a.png\"><iframe src=\"https://x.example\"></iframe>text</div>"
    Assert.Equal("Titletext", html)

[<Fact>]
let ``R-14 embedding and form elements lose their content too, not only their tags`` () =
    let html =
        Sanitizer.sanitizeHtml "<p>a</p><iframe>fallback</iframe><svg><title>t</title><script>x()</script></svg><textarea>typed</textarea><noscript>ns</noscript>"

    Assert.Equal("<p>a</p>", html)

[<Fact>]
let ``R-14 links keep https href and get the nofollow noopener noreferrer ugc rel`` () =
    let html = Sanitizer.sanitizeHtml "<a href=\"https://example.com/x\">x</a>"
    Assert.Contains("href=\"https://example.com/x\"", html)
    Assert.Contains("rel=\"nofollow noopener noreferrer ugc\"", html)

[<Fact>]
let ``R-14 a rel the remote sent is replaced, not merged`` () =
    let html = Sanitizer.sanitizeHtml "<a href=\"https://example.com\" rel=\"me\" target=\"_blank\">x</a>"
    Assert.Contains("rel=\"nofollow noopener noreferrer ugc\"", html)
    Assert.DoesNotContain("me\"", html)
    Assert.DoesNotContain("target", html)

[<Fact>]
let ``R-14 mailto and http links are allowed`` () =
    let html = Sanitizer.sanitizeHtml "<a href=\"mailto:a@example.com\">m</a><a href=\"http://example.com\">h</a>"
    Assert.Contains("href=\"mailto:a@example.com\"", html)
    Assert.Contains("href=\"http://example.com\"", html)

[<Fact>]
let ``R-14 class is kept on a and span only`` () =
    let html = Sanitizer.sanitizeHtml "<p class=\"x\"><span class=\"h-card\">s</span><a class=\"mention\" href=\"https://e.example\">a</a><b class=\"y\">b</b></p>"
    Assert.Contains("<span class=\"h-card\">", html)
    Assert.Contains("class=\"mention\"", html)
    Assert.DoesNotContain("class=\"x\"", html)
    Assert.DoesNotContain("class=\"y\"", html)

[<Fact>]
let ``R-14 other attributes are removed`` () =
    let html = Sanitizer.sanitizeHtml "<p id=\"a\" style=\"x\" title=\"t\" data-x=\"1\">Hi</p>"
    Assert.Equal("<p>Hi</p>", html)

[<Fact>]
let ``R-14 null and empty input give an empty string`` () =
    Assert.Equal("", Sanitizer.sanitizeHtml null)
    Assert.Equal("", Sanitizer.sanitizeHtml "")
    Assert.Equal("", Sanitizer.toText null)

[<Fact>]
let ``R-14 sanitizing is thread safe on the shared instance`` () =
    let results =
        [| 1..200 |]
        |> Array.Parallel.map (fun i -> Sanitizer.sanitizeHtml $"<p onclick=\"x\">{i}<script>1</script></p>")

    results |> Array.iteri (fun i html -> Assert.Equal($"<p>{i + 1}</p>", html))

[<Fact>]
let ``FR-056 toText removes markup, script content and decodes entities`` () =
    Assert.Equal("Alex & Sam", Sanitizer.toText "<p>Alex &amp; <b>Sam</b></p><script>steal()</script>")
    Assert.Equal("plain", Sanitizer.toText "  plain  ")
