using Microsoft.AspNetCore.StaticFiles;
using System.Diagnostics.CodeAnalysis;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Wraps <see cref="FileExtensionContentTypeProvider"/> with two adjustments:
/// <list type="bullet">
/// <item>A curated set of extensions that are XML or plain-text source files (project files,
///   build scripts, config files, etc.) are always served as <c>text/plain; charset=utf-8</c>,
///   overriding whatever the inner provider or browser sniffing would produce.</item>
/// <item>Any remaining <c>text/*</c> type that does not already declare a charset gets
///   <c>; charset=utf-8</c> appended, preventing browsers from falling back to Windows-1252.</item>
/// </list>
/// </summary>
internal sealed class Utf8TextContentTypeProvider : IContentTypeProvider
{
    // Extensions that must always render as raw UTF-8 text in the browser, regardless of
    // what the inner provider or browser content-sniffing would otherwise produce.
    private static readonly HashSet<string> s_plainText =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Standard XML formats
            ".xml", ".xsd", ".xsl", ".xslt", ".svg", ".plist", ".wsdl", ".rdf",
            // .NET / MSBuild project and build-system files
            ".csproj", ".vbproj", ".fsproj", ".vcxproj", ".filters",
            ".props", ".targets", ".tasks",
            // Package / resource files
            ".nuspec", ".resx",
            // Configuration files
            ".config",
        };

    private readonly FileExtensionContentTypeProvider _inner;

    internal Utf8TextContentTypeProvider(FileExtensionContentTypeProvider inner) => _inner = inner;

    public bool TryGetContentType(string subpath, [MaybeNullWhen(false)] out string contentType)
    {
        // Check our explicit override list first using Path.GetExtension directly,
        // bypassing any quirks in FileExtensionContentTypeProvider's mapping table.
        var ext = Path.GetExtension(subpath);
        if (s_plainText.Contains(ext))
        {
            contentType = "text/plain; charset=utf-8";
            return true;
        }

        if (!_inner.TryGetContentType(subpath, out contentType))
            return false;

        // Remap any remaining XML application types (application/xml, image/svg+xml, etc.)
        // the inner provider knows about that we didn't explicitly list above.
        if (contentType.EndsWith("/xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("+xml", StringComparison.OrdinalIgnoreCase))
        {
            contentType = "text/plain; charset=utf-8";
            return true;
        }

        // Append charset=utf-8 to all other text/* types that don't already declare one.
        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            && !contentType.Contains("charset", StringComparison.OrdinalIgnoreCase))
            contentType += "; charset=utf-8";

        return true;
    }
}
