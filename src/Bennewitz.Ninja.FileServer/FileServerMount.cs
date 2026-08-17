using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// A single registered mount: its route prefix, its resolved root, and the configuration that
/// governs it. Attached to the mount's endpoints as metadata, so handlers recover it from the
/// matched endpoint rather than from shared state — which is what lets one host run any number
/// of independent mounts.
/// </summary>
internal sealed class FileServerMount
{
    internal FileServerMount(string prefix, FileServerMountOptions options)
    {
        Prefix = prefix;
        Options = options;

        // Resolved once here rather than per request: the root does not move, and resolving
        // links on every request would be wasted work.
        ResolvedRoot = FileServerPath.ResolveFinal(options.RootPath);

        FileProvider = new AllowedExtensionsFileProvider(
            new PhysicalFileProvider(ResolvedRoot),
            options.AllowedExtensions);
    }

    internal string Prefix { get; }

    internal FileServerMountOptions Options { get; }

    /// <summary>The mount root with all symlinks resolved. Containment compares against this.</summary>
    internal string ResolvedRoot { get; }

    internal IFileProvider FileProvider { get; }

    /// <summary>
    /// Whether a file may be served, by extension. Both the listing and the download path call
    /// this — the download path cannot rely on <see cref="AllowedExtensionsFileProvider"/>,
    /// because serving a file by physical path bypasses the provider entirely.
    /// </summary>
    internal bool IsAllowed(string fileName) =>
        Options.AllowedExtensions.Count == 0
        || Options.AllowedExtensions.Contains(Path.GetExtension(fileName));

    /// <summary>
    /// Resolves an untrusted request path within this mount, or returns false if it escapes.
    /// </summary>
    internal bool TryResolve(string relativePath, out string fullPath) =>
        FileServerPath.TryResolveWithin(ResolvedRoot, relativePath, out fullPath);

    /// <summary>
    /// Builds an absolute URL for a path inside this mount, honouring the host's
    /// <see cref="HttpRequest.PathBase"/> so the component works when mounted under a prefix.
    /// </summary>
    internal string Url(HttpRequest request, string relativePath = "")
    {
        var encoded = string.Join('/',
            relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));

        var basePath = request.PathBase.HasValue ? request.PathBase.Value : string.Empty;

        return encoded.Length == 0
            ? $"{basePath}{Prefix}"
            : $"{basePath}{Prefix}/{encoded}";
    }
}
