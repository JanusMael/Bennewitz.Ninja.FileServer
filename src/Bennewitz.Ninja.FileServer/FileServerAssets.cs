using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// The component's own CSS and JavaScript, embedded in this assembly and served from an
/// endpoint under each mount.
/// </summary>
/// <remarks>
/// Deliberately not shipped as RCL static web assets. Serving them ourselves means the
/// component needs nothing from the host's static-file pipeline, cannot be broken by a host
/// that never calls <c>UseStaticFiles</c>, keeps working in a single-file publish where no
/// loose files exist on disk, and has no <c>_content/</c> path to get wrong under a
/// <c>PathBase</c>.
/// <para>
/// Lookup goes through <see cref="ManifestEmbeddedFileProvider"/> rather than raw resource
/// names: the manifest preserves real directory structure, whereas resource names flatten
/// paths into dots and cannot be reversed unambiguously for a file such as
/// <c>bootstrap.min.css</c>.
/// </para>
/// </remarks>
internal static class FileServerAssets
{
    /// <summary>Route segment reserved beneath every mount for the component's own assets.</summary>
    internal const string RouteSegment = "_fs";

    private static readonly IFileProvider Provider =
        new ManifestEmbeddedFileProvider(typeof(FileServerAssets).Assembly, "wwwroot");

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    /// <summary>
    /// Cache-busting token, stable for a given build and different across builds. The module
    /// version id changes whenever the assembly is recompiled, which is exactly the granularity
    /// wanted: assets may be cached indefinitely and are re-fetched after an upgrade.
    /// </summary>
    internal static string Version { get; } =
        typeof(FileServerAssets).Assembly.ManifestModule.ModuleVersionId.ToString("N")[..12];

    /// <summary>
    /// Opens an embedded asset by its path within <c>wwwroot</c>, e.g. <c>css/site.css</c>.
    /// </summary>
    internal static bool TryOpen(string relativePath, out Stream content, out string contentType)
    {
        content = Stream.Null;
        contentType = "application/octet-stream";

        // The asset route is a fixed set of files we ship; refuse anything that tries to
        // navigate rather than name one.
        if (relativePath.Contains("..", StringComparison.Ordinal)
            || relativePath.StartsWith('/')
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        var file = Provider.GetFileInfo(relativePath);
        if (!file.Exists || file.IsDirectory)
            return false;

        if (ContentTypes.TryGetContentType(relativePath, out var resolved))
            contentType = resolved;

        content = file.CreateReadStream();
        return true;
    }
}
