using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Markdig extension that replaces the built-in code-block renderer with one that tokenises
/// fenced blocks carrying a language.
/// </summary>
internal sealed class SyntaxHighlightingExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer)
    {
        if (renderer is not HtmlRenderer htmlRenderer)
            return;

        var existing = htmlRenderer.ObjectRenderers.FindExact<CodeBlockRenderer>();
        if (existing is not null)
            htmlRenderer.ObjectRenderers.Remove(existing);

        // The original is kept as the fallback rather than reimplemented: indented blocks,
        // fences with no language, and languages the tokeniser does not know all still need to
        // render exactly as Markdig would have rendered them.
        htmlRenderer.ObjectRenderers.Add(new HighlightedCodeBlockRenderer(existing));
    }
}

/// <summary>
/// Renders a fenced code block as <c>&lt;pre&gt;&lt;code class="language-x"&gt;</c> with
/// tokenised content, and delegates anything it cannot tokenise to the renderer it replaced.
/// </summary>
internal sealed class HighlightedCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
{
    private readonly CodeBlockRenderer _fallback;

    internal HighlightedCodeBlockRenderer(CodeBlockRenderer? fallback) =>
        _fallback = fallback ?? new CodeBlockRenderer();

    protected override void Write(HtmlRenderer renderer, CodeBlock codeBlock)
    {
        if (codeBlock is not FencedCodeBlock fenced
            || fenced.Info is not { Length: > 0 } language
            || !MarkdownSyntaxHighlighter.TryHighlight(GetText(fenced), language, out var html))
        {
            _fallback.Write(renderer, codeBlock);
            return;
        }

        // The element shape matches what Markdig emits, because github-markdown.css styles
        // `pre` and scopes its token colours beneath `.markdown-body` — changing the shape would
        // lose the code block's own styling to gain the tokens.
        renderer.Write("<pre><code class=\"language-");
        renderer.WriteEscape(language);
        renderer.Write("\">");
        renderer.Write(html);
        renderer.Write("</code></pre>");
        renderer.EnsureLine();
    }

    /// <summary>
    /// Reassembles the block's source text. Markdig keeps a fenced block as slices over the
    /// original document rather than as a string.
    /// </summary>
    private static string GetText(LeafBlock block)
    {
        if (block.Lines.Lines is not { } lines)
            return string.Empty;

        var text = new System.Text.StringBuilder();

        for (var i = 0; i < block.Lines.Count; i++)
            text.Append(lines[i].Slice.AsSpan()).Append('\n');

        return text.ToString();
    }
}
