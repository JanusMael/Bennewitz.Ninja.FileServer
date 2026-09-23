using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Primitives;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Wraps an <see cref="IFileProvider"/> and decides what a directory listing shows. An entry is
/// left out when the <see cref="SensitivePathPolicy"/> refuses it, when it is a file whose
/// extension is not in the allowed set, or when its path matches an unlisted pattern.
/// Directories are exempt from the extension filter (navigation requires them), never from the
/// policy or the patterns.
/// </summary>
/// <remarks>
/// Only listings go through here. The download path serves by physical path and applies the
/// policy and the extension check itself; unlisted patterns deliberately do not reach it, since
/// unlisting changes what is advertised, never what is served.
/// </remarks>
internal sealed class ListingFileProvider : IFileProvider
{
    private readonly IFileProvider _inner;
    private readonly IReadOnlySet<string> _allowed;
    private readonly SensitivePathPolicy _policy;
    private readonly Matcher? _unlisted;

    /// <param name="inner">The physical provider, constructed with <c>ExclusionFilters.None</c>.</param>
    /// <param name="allowedExtensions">Normalised allowed extensions; empty allows every extension.</param>
    /// <param name="policy">The mount's sensitive-path policy.</param>
    /// <param name="unlistedPatterns">
    /// Normalised unlisted patterns, matched case-insensitively: they only ever hide, so a
    /// broader match can never serve anything.
    /// </param>
    internal ListingFileProvider(
        IFileProvider inner,
        IReadOnlySet<string> allowedExtensions,
        SensitivePathPolicy policy,
        IReadOnlyList<string>? unlistedPatterns = null)
    {
        _inner    = inner;
        _allowed  = allowedExtensions;
        _policy   = policy;
        _unlisted = GlobPatterns.Compile(unlistedPatterns ?? [], StringComparison.OrdinalIgnoreCase);
    }

    public IFileInfo GetFileInfo(string subpath)
    {
        var info = _inner.GetFileInfo(subpath);
        if (info.Exists && !info.IsDirectory && !IsAllowed(info.Name))
            return new NotFoundFileInfo(subpath);
        return info;
    }

    public IDirectoryContents GetDirectoryContents(string subpath)
    {
        var contents = _inner.GetDirectoryContents(subpath);
        if (!contents.Exists)
            return contents;

        if (!_policy.TryDescribeDirectory(subpath, out var directory, out var directoryIsSensitive))
            return NotFoundDirectoryContents.Singleton;

        return new FilteredDirectoryContents(contents.Where(entry =>
        {
            // Root entries are matched as a bare name: a leading "/" would match no pattern.
            var relative = directory.Length == 0 ? entry.Name : $"{directory}/{entry.Name}";

            return !_policy.IsEntryRefused(directoryIsSensitive, relative, entry.PhysicalPath ?? string.Empty, entry.Name)
                && (entry.IsDirectory || IsAllowed(entry.Name))
                && !IsUnlisted(relative);
        }));
    }

    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    private bool IsAllowed(string fileName) =>
        _allowed.Count == 0 || _allowed.Contains(Path.GetExtension(fileName));

    private bool IsUnlisted(string relativePath) =>
        _unlisted is not null && _unlisted.Match(relativePath).HasMatches;

    private sealed class FilteredDirectoryContents : IDirectoryContents
    {
        private readonly IEnumerable<IFileInfo> _entries;

        internal FilteredDirectoryContents(IEnumerable<IFileInfo> entries) => _entries = entries;

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator() => _entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
