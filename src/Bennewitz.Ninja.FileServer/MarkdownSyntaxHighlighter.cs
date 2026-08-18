using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using ColorCode;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Tokenises fenced code blocks in rendered Markdown.
/// </summary>
/// <remarks>
/// Markdig labels a fence (<c>language-csharp</c>) but does not tokenise it, so code blocks
/// rendered monotone while the vendored GitHub stylesheet carried a complete unused token
/// palette. This bridges the two.
/// <para>
/// ColorCode does the tokenising; its class names are then translated to GitHub's
/// (<c>pl-k</c>, <c>pl-s</c>, <c>pl-c</c>…). Two reasons not to emit ColorCode's own names:
/// they are bare English words — <c>keyword</c>, <c>string</c>, <c>comment</c> — which is
/// exactly the collision with a host's stylesheet that every other class here is prefixed to
/// avoid; and GitHub's names already resolve to the palette the stylesheet ships, so the
/// colour-scheme toggle governs code blocks with no further CSS.
/// </para>
/// </remarks>
internal static partial class MarkdownSyntaxHighlighter
{
    private static readonly HtmlClassFormatter Formatter = new();

    /// <summary>
    /// Fence names in the wild versus the ids ColorCode registers. Anything absent here is
    /// looked up as written, and anything ColorCode does not know renders plain.
    /// </summary>
    private static readonly Dictionary<string, string> LanguageAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["cs"] = "c#",
            ["csharp"] = "c#",
            ["dotnet"] = "c#",
            ["js"] = "javascript",
            ["jsx"] = "javascript",
            ["mjs"] = "javascript",
            ["node"] = "javascript",
            ["ts"] = "typescript",
            ["tsx"] = "typescript",
            ["ps"] = "powershell",
            ["ps1"] = "powershell",
            ["pwsh"] = "powershell",
            ["py"] = "python",
            ["fs"] = "f#",
            ["fsharp"] = "f#",
            ["vb"] = "vb.net",
            ["vbnet"] = "vb.net",
            ["c++"] = "cpp",
            ["cxx"] = "cpp",
            ["hpp"] = "cpp",
            ["c"] = "cpp",
            ["h"] = "cpp",
            ["htm"] = "html",
            ["xhtml"] = "xml",
            ["csproj"] = "xml",
            ["props"] = "xml",
            ["targets"] = "xml",
            ["xaml"] = "xml",
            ["axaml"] = "xml",
            ["svg"] = "xml",
            ["md"] = "markdown",
            ["markdown"] = "markdown",
            ["postgres"] = "sql",
            ["postgresql"] = "sql",
            ["tsql"] = "sql",
            ["mysql"] = "sql",
        };

    /// <summary>
    /// ColorCode's emitted class names mapped to GitHub's. A scope missing from this table keeps
    /// its text and loses its class, so an unmapped token renders in the body colour rather than
    /// leaking a generic class name into the host's page.
    /// </summary>
    private static readonly Dictionary<string, string> TokenClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // comments
            ["comment"] = "pl-c",
            ["htmlComment"] = "pl-c",
            ["xmlComment"] = "pl-c",
            ["xmlDocComment"] = "pl-c",

            // keywords
            ["keyword"] = "pl-k",
            ["controlKeyword"] = "pl-k",
            ["preprocessorKeyword"] = "pl-k",
            ["pseudoKeyword"] = "pl-k",

            // strings
            ["string"] = "pl-s",
            ["stringCSharpVerbatim"] = "pl-s",
            ["markdownCode"] = "pl-s",
            ["xmlCDataSection"] = "pl-s",
            ["xmlAttributeValue"] = "pl-s",
            ["htmlAttributeValue"] = "pl-s",
            ["cssPropertyValue"] = "pl-s",
            ["jsonString"] = "pl-s",
            ["stringEscape"] = "pl-cce",
            ["xmlAttributeQuotes"] = "pl-pds",

            // constants and literals
            ["number"] = "pl-c1",
            ["predefined"] = "pl-c1",
            ["intrinsic"] = "pl-c1",
            ["builtinValue"] = "pl-c1",
            ["htmlEntity"] = "pl-c1",
            ["cssPropertyName"] = "pl-c1",
            ["powershellParameter"] = "pl-c1",
            ["jsonNumber"] = "pl-c1",
            ["jsonConst"] = "pl-c1",

            // callables
            ["builtinFunction"] = "pl-en",
            ["constructor"] = "pl-en",
            ["sqlSystemFunction"] = "pl-en",
            ["powershellCommand"] = "pl-en",

            // named things
            ["className"] = "pl-e",
            ["type"] = "pl-e",
            ["nameSpace"] = "pl-e",
            ["attribute"] = "pl-e",
            ["powershellType"] = "pl-e",
            ["powershellAttribute"] = "pl-e",

            // markup element names
            ["xmlName"] = "pl-ent",
            ["htmlElementName"] = "pl-ent",
            ["cssSelector"] = "pl-ent",
            ["xmlDocTag"] = "pl-ent",
            ["jsonKey"] = "pl-ent",

            // identifiers
            ["xmlAttribute"] = "pl-smi",
            ["htmlAttributeName"] = "pl-smi",
            ["powershellVariable"] = "pl-smi",
            ["typeVariable"] = "pl-smi",

            // markdown
            ["markdownHeader"] = "pl-mh",
            ["markdownListItem"] = "pl-ml",
            ["markdownBold"] = "pl-mb",
            ["markdownEmph"] = "pl-mi",
        };

    [GeneratedRegex("class=\"(?<classes>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex ClassAttribute { get; }

    /// <summary>
    /// Tokenises <paramref name="code"/> as <paramref name="fenceLanguage"/>, producing the inner
    /// HTML of a <c>&lt;code&gt;</c> element with the text already escaped.
    /// </summary>
    /// <returns>
    /// <c>false</c> when the language is unknown or the tokeniser produced something unexpected,
    /// in which case the caller renders the block plainly.
    /// </returns>
    internal static bool TryHighlight(
        string code,
        string? fenceLanguage,
        [NotNullWhen(true)] out string? html)
    {
        html = null;

        if (string.IsNullOrWhiteSpace(fenceLanguage) || string.IsNullOrEmpty(code))
            return false;

        var language = ResolveLanguage(fenceLanguage);
        if (language is null)
            return false;

        string formatted;
        try
        {
            formatted = Formatter.GetHtmlString(code, language);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A tokeniser failing is not a reason to fail the request: the file is still readable
            // without colour, which is what the caller falls back to.
            return false;
        }

        if (!TryExtractBody(formatted, out var body))
            return false;

        html = TranslateClasses(body);
        return true;
    }

    private static ILanguage? ResolveLanguage(string fenceLanguage)
    {
        var id = fenceLanguage.Trim();

        if (LanguageAliases.TryGetValue(id, out var mapped))
            id = mapped;

        try
        {
            return Languages.FindById(id);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Takes the content between ColorCode's wrapper elements, which are
    /// <c>&lt;div class="lang"&gt;&lt;pre&gt;…&lt;/pre&gt;&lt;/div&gt;</c>. Returning false for
    /// anything else keeps a future change in that markup from leaking ColorCode's own class
    /// names into a host's page.
    /// </summary>
    private static bool TryExtractBody(string formatted, [NotNullWhen(true)] out string? body)
    {
        body = null;

        const string open = "<pre>";
        const string close = "</pre>";

        var start = formatted.IndexOf(open, StringComparison.Ordinal);
        var end = formatted.LastIndexOf(close, StringComparison.Ordinal);

        if (start < 0 || end <= start)
            return false;

        body = formatted[(start + open.Length)..end].TrimStart('\r', '\n');
        return true;
    }

    private static string TranslateClasses(string body) =>
        ClassAttribute.Replace(body, match =>
        {
            var translated = match.Groups["classes"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(cls => TokenClasses.GetValueOrDefault(cls))
                .Where(cls => cls is not null)
                .Distinct(StringComparer.Ordinal)
                .ToArray();

            // No class at all rather than an unmapped one: the span survives, the text is intact,
            // and nothing generic reaches the page.
            return translated.Length == 0
                ? string.Empty
                : $"class=\"{string.Join(' ', translated)}\"";
        });
}
