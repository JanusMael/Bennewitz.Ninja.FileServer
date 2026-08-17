using Markdig;
using Microsoft.AspNetCore.Html;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bennewitz.Ninja.FileServer.Pages;

/// <summary>
/// Page model for rendering a Markdown file as HTML. Accessed at <c>/view?path=…</c>.
/// </summary>
public class MarkdownModel : PageModel
{
    private readonly MarkdownPipeline _pipeline;

    /// <summary>Initialises the model with the shared, thread-safe Markdig pipeline.</summary>
    public MarkdownModel(MarkdownPipeline pipeline) => _pipeline = pipeline;

    /// <summary>Rendered HTML produced from the source Markdown file.</summary>
    public HtmlString RenderedContent { get; private set; } = HtmlString.Empty;

    /// <summary>File name portion of the requested path (used as the page title).</summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>URL of the raw file served by the file-server middleware (appends <c>?raw=1</c>).</summary>
    public string RawUrl { get; private set; } = string.Empty;

    /// <summary>Path segments used to render the breadcrumb navigation.</summary>
    public string[] BreadcrumbParts { get; private set; } = [];

    /// <summary>
    /// Validates the requested path, reads the Markdown file, renders it to HTML,
    /// and populates the view model properties.
    /// </summary>
    /// <param name="path">Relative path within the served root (e.g. <c>docs/readme.md</c>).</param>
    public IActionResult OnGet([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return NotFound();

        // Normalise separators then resolve to an absolute path to detect traversal attempts.
        var root     = Path.GetFullPath(Settings.ServedFilesRoot);
        var fullPath = Path.GetFullPath(
            Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

        // Resolved path must sit inside the root directory.
        if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!fullPath.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return NotFound();

        if (!System.IO.File.Exists(fullPath))
            return NotFound();

        // Honour AllowedExtensions — if a filter is active and .md is excluded, deny.
        if (Settings.AllowedExtensions.Count > 0
            && !Settings.AllowedExtensions.Contains(".md"))
            return NotFound();

        var markdown = System.IO.File.ReadAllText(fullPath);
        RenderedContent = new HtmlString(Markdown.ToHtml(markdown, _pipeline));
        FileName = Path.GetFileName(fullPath);

        BreadcrumbParts = path.TrimStart('/').Split('/');

        // Re-encode each segment — path was URL-decoded by ASP.NET Core when received as a query value.
        var encodedPath = string.Join("/", BreadcrumbParts.Select(Uri.EscapeDataString));
        RawUrl = $"/{Settings.ServedFilesRoute}/{encodedPath}?raw=1";

        ViewData["Title"] = FileName;
        return Page();
    }
}
