using Microsoft.Extensions.FileSystemGlobbing;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Decides whether a path is <em>sensitive</em> — any segment below the mount root dot-prefixed,
/// or naming an entry with the Hidden or System attribute — and therefore refused on direct
/// request and left out of listings, unless its whole root-relative path matches one of the
/// mount's <see cref="FileServerMountOptions.ExposedSensitivePatterns"/>. The one rule behind
/// both, so they cannot disagree.
/// </summary>
/// <remarks>
/// This is the counterpart of <see cref="Microsoft.Extensions.FileProviders.Physical.ExclusionFilters.Sensitive"/>,
/// which the mount's provider no longer applies: the provider's filter only ever governed
/// listings, while downloads by physical path bypassed it and served <c>.env</c> and
/// <c>.git/config</c> to anyone who typed the URL.
/// <para>
/// A path is judged in its canonical form, never as the raw request string. Raw forms are how a
/// sensitive entry is reached under a name that does not look sensitive: <c>sub%5C.env</c>
/// decodes to a single segment on Windows, <c>sub/%2e%2e/.env</c> is not normalised by every
/// server, <c>.env::$DATA</c> names the file's data stream, and an 8.3 alias such as
/// <c>ENVIRO~1</c> names <c>.environment</c> without its dot. Every one of these fails closed.
/// </para>
/// <para>
/// Only segments below the root are judged: the root's own name and attributes are the host's
/// choice. Links are not resolved, so an ordinary link to a sensitive target is judged by its
/// own name — exactly as the listing shows it.
/// </para>
/// </remarks>
internal sealed class SensitivePathPolicy
{
    private const FileAttributes SensitiveAttributes = FileAttributes.Hidden | FileAttributes.System;

    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    private static readonly EnumerationOptions ExactNameLookup = new()
    {
        AttributesToSkip = 0,
        MatchType = MatchType.Simple,
        MatchCasing = MatchCasing.CaseInsensitive,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    private readonly string _root;
    private readonly Matcher? _exposed;

    /// <param name="resolvedRoot">The mount root, already passed through <see cref="FileServerPath.ResolveFinal"/>.</param>
    /// <param name="exposedPatterns">
    /// Normalised <see cref="FileServerMountOptions.ExposedSensitivePatterns"/>. Matched
    /// ordinally: exposure widens what is served, and a case-insensitive match would expose a
    /// differently-cased directory on a case-sensitive filesystem.
    /// </param>
    internal SensitivePathPolicy(string resolvedRoot, IReadOnlyList<string>? exposedPatterns = null)
    {
        _root = resolvedRoot.TrimEnd(Separators);
        _exposed = GlobPatterns.Compile(exposedPatterns ?? [], StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether a request for <paramref name="requestPath"/> must be refused: it cannot be
    /// canonicalised within the root, or some segment of it is sensitive and the whole path is
    /// not exposed.
    /// </summary>
    internal bool IsRefused(string requestPath) =>
        !TryCanonicalise(requestPath, out var segments)
        || (HasSensitiveSegment(segments) && !IsExposed(string.Join('/', segments)));

    /// <summary>
    /// The listing context for a directory: its canonical root-relative path, and whether that
    /// path already contains a sensitive segment. Computed once per listing, so each entry adds
    /// only its own segment.
    /// </summary>
    /// <returns><c>false</c> when the path cannot be canonicalised; list nothing.</returns>
    internal bool TryDescribeDirectory(string requestPath, out string relativePath, out bool isSensitive)
    {
        if (!TryCanonicalise(requestPath, out var segments))
        {
            relativePath = string.Empty;
            isSensitive = true;
            return false;
        }

        relativePath = string.Join('/', segments);
        isSensitive = HasSensitiveSegment(segments);
        return true;
    }

    /// <summary>
    /// Whether an entry of a listed directory must be left out: it, or the directory, is
    /// sensitive, and its whole path is not exposed.
    /// </summary>
    internal bool IsEntryRefused(bool directoryIsSensitive, string entryRelativePath, string physicalPath, string name) =>
        (directoryIsSensitive || IsSensitiveEntry(physicalPath, name)) && !IsExposed(entryRelativePath);

    private bool IsExposed(string relativePath) =>
        _exposed is not null && _exposed.Match(relativePath).HasMatches;

    private bool TryCanonicalise(string requestPath, out string[] segments)
    {
        segments = [];

        var fragment = requestPath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(fragment))
            return false;

        string full;
        try
        {
            // Lexical only: collapses "." and "..", and on Windows treats "\" as a separator and
            // drops the trailing dots and spaces the filesystem would ignore anyway.
            full = Path.GetFullPath(Path.Combine(_root, fragment));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (!FileServerPath.IsWithin(_root, full))
            return false;

        segments = full.Length == _root.Length
            ? []
            : full[(_root.Length + 1)..].Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        if (!OperatingSystem.IsWindows())
            return true;

        // ':' reaches alternate data streams; the rest are invalid in a name and include the
        // wildcards the alias check below would otherwise interpret.
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var segment in segments)
        {
            if (segment.IndexOfAny(invalid) >= 0)
                return false;
        }

        return true;
    }

    private bool HasSensitiveSegment(string[] segments)
    {
        var current = _root;

        foreach (var segment in segments)
        {
            var parent = current;
            current = Path.Combine(current, segment);

            if (IsSensitiveEntry(current, segment))
                return true;

            if (OperatingSystem.IsWindows() && IsShortNameAlias(parent, segment))
                return true;
        }

        return false;
    }

    private static bool IsSensitiveEntry(string fullPath, string name)
    {
        if (name.StartsWith('.'))
            return true;

        if (!File.Exists(fullPath) && !Directory.Exists(fullPath))
            return false;

        try
        {
            return (File.GetAttributes(fullPath) & SensitiveAttributes) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An entry that exists but cannot be judged is refused rather than served.
            return true;
        }
    }

    /// <summary>
    /// Whether <paramref name="segment"/> reaches an existing entry only through its 8.3 alias.
    /// Every such alias contains <c>~</c>, so names without one skip the directory lookup.
    /// </summary>
    /// <remarks>
    /// Enumeration matches long names only, so an alias finds nothing while a genuine name that
    /// happens to contain <c>~</c> finds itself. An alias of an ordinary file is refused too:
    /// it is never a URL this component generates, and judging it would mean trusting a name
    /// the entry does not have.
    /// </remarks>
    private static bool IsShortNameAlias(string parent, string segment)
    {
        if (!segment.Contains('~'))
            return false;

        var full = Path.Combine(parent, segment);
        if (!File.Exists(full) && !Directory.Exists(full))
            return false;

        try
        {
            return !Directory.EnumerateFileSystemEntries(parent, segment, ExactNameLookup).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
