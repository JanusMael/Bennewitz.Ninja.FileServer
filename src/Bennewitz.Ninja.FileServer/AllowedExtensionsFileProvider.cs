using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Wraps an <see cref="IFileProvider"/> and hides any file whose extension is not in the
/// configured allowed-extensions set. Directories are always visible (navigation requires
/// them). When the set is empty every file passes through unchanged.
/// </summary>
internal sealed class AllowedExtensionsFileProvider : IFileProvider
{
    private readonly IFileProvider _inner;
    private readonly IReadOnlySet<string> _allowed;

    internal AllowedExtensionsFileProvider(IFileProvider inner, IReadOnlySet<string> allowedExtensions)
    {
        _inner   = inner;
        _allowed = allowedExtensions;
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
        if (!contents.Exists || _allowed.Count == 0)
            return contents;
        return new FilteredDirectoryContents(
            contents.Where(f => f.IsDirectory || IsAllowed(f.Name)));
    }

    public IChangeToken Watch(string filter) => _inner.Watch(filter);

    private bool IsAllowed(string fileName) =>
        _allowed.Count == 0 || _allowed.Contains(Path.GetExtension(fileName));

    private sealed class FilteredDirectoryContents : IDirectoryContents
    {
        private readonly IEnumerable<IFileInfo> _entries;

        internal FilteredDirectoryContents(IEnumerable<IFileInfo> entries) => _entries = entries;

        public bool Exists => true;

        public IEnumerator<IFileInfo> GetEnumerator() => _entries.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
