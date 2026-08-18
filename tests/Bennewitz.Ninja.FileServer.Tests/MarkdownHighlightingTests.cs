using Markdig;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// Code-block tokenising. The classes emitted matter as much as the fact of highlighting: they
/// have to be GitHub's, because those are what the vendored stylesheet colours and what the
/// colour-scheme toggle re-points when a scheme is pinned.
/// </summary>
public sealed class MarkdownHighlightingTests
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Use<SyntaxHighlightingExtension>()
        .Build();

    private static string Render(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    [Fact]
    public void FencedCSharp_IsTokenisedWithGitHubClasses()
    {
        var html = Render("""
            ```csharp
            // a comment
            var x = "text";
            ```
            """);

        Assert.Contains("class=\"pl-c\"", html, StringComparison.Ordinal);   // comment
        Assert.Contains("class=\"pl-k\"", html, StringComparison.Ordinal);   // keyword
        Assert.Contains("class=\"pl-s\"", html, StringComparison.Ordinal);   // string
    }

    [Fact]
    public void FencedCSharp_DoesNotEmitColorCodesOwnClassNames()
    {
        var html = Render("""
            ```csharp
            // a comment
            var x = "text";
            ```
            """);

        // Bare English words would collide with a host's stylesheet, which is the one thing
        // every class in this component is prefixed to avoid.
        Assert.DoesNotContain("class=\"comment\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"keyword\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"string\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("class=\"csharp\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FencedBlock_KeepsTheElementShapeMarkdigProduces()
    {
        var html = Render("""
            ```csharp
            var x = 1;
            ```
            """);

        // github-markdown.css styles `pre` and scopes token colours under `.markdown-body`;
        // a different shape would trade the code block's styling for its tokens.
        Assert.Contains("<pre><code class=\"language-csharp\">", html, StringComparison.Ordinal);
        Assert.Contains("</code></pre>", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("cs")]
    [InlineData("csharp")]
    [InlineData("C#")]
    [InlineData("CSharp")]
    public void FenceLanguageAliases_AllResolve(string fence)
    {
        var html = Render($"```{fence}\nvar x = 1;\n```");

        Assert.Contains("class=\"pl-k\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xml", "<root a=\"1\"/>")]
    [InlineData("javascript", "const x = 1;")]
    [InlineData("powershell", "$x = Get-Item")]
    [InlineData("sql", "SELECT 1 FROM t;")]
    [InlineData("css", ".a { color: red; }")]
    public void OtherSupportedLanguages_AreTokenised(string fence, string code)
    {
        var html = Render($"```{fence}\n{code}\n```");

        Assert.Contains("<span class=\"pl-", html, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownLanguage_RendersPlainlyRatherThanFailing()
    {
        var html = Render("""
            ```brainfuck
            ++++[>++++<-]
            ```
            """);

        Assert.Contains("language-brainfuck", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void FenceWithoutALanguage_RendersPlainly()
    {
        var html = Render("""
            ```
            plain text
            ```
            """);

        Assert.Contains("<pre><code>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<span class=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void IndentedCodeBlock_StillRendersThroughTheOriginalRenderer()
    {
        var html = Render("    indented code\n");

        Assert.Contains("<pre><code>indented code", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightedCode_IsStillEscaped()
    {
        var html = Render("""
            ```csharp
            var evil = "<script>alert(1)</script>";
            ```
            """);

        // Tokenising must not become an injection route: the tag is text in a code block.
        Assert.DoesNotContain("<script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void HighlightedCode_PreservesEveryCharacterOfTheSource()
    {
        const string code = "var greeting = \"héllo — wörld\"; // ünicode";

        var html = Render($"```csharp\n{code}\n```");

        // Strip the markup and the text must come back unchanged, accents and dashes included.
        var text = System.Net.WebUtility.HtmlDecode(
            System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", string.Empty)).Trim();

        Assert.Equal(code, text);
    }

    [Fact]
    public void EmptyFence_RendersPlainlyWithoutThrowing()
    {
        var html = Render("```csharp\n```");

        Assert.Contains("language-csharp", html, StringComparison.Ordinal);
    }
}
