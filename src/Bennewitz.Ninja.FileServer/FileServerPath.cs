namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Path containment: the single place that decides whether a request may reach a given file.
/// Every handler routes through here rather than re-deriving the rules, because each new route
/// otherwise re-opens the same class of escape.
/// </summary>
/// <remarks>
/// Two rules, both learned from defects in the original implementation:
/// <list type="number">
/// <item><description>
///   <see cref="Path.GetFullPath(string)"/> is pure string canonicalisation and never touches
///   the filesystem, so a symlink inside the root escapes it undetected. Links are resolved —
///   every segment, not just the leaf, since an intermediate directory link escapes just as
///   effectively — before any comparison happens.
/// </description></item>
/// <item><description>
///   Comparison is ordinal, unconditionally. Case-insensitive comparison can <em>admit</em> an
///   escape on a case-sensitive filesystem, where <c>/srv/Docs</c> is a genuinely different
///   directory from <c>/srv/docs</c>; ordinal can only ever deny a legitimate request, and that
///   is unreachable in practice because every URL served is one this component generated using
///   the root's own casing.
/// </description></item>
/// </list>
/// </remarks>
internal static class FileServerPath
{
    /// <summary>
    /// Canonicalises <paramref name="path"/> and resolves every symlink along it to its final
    /// target. Segments that do not exist are kept verbatim — a path to a missing file still
    /// resolves its existing ancestors, so containment can be judged before touching the file.
    /// </summary>
    internal static string ResolveFinal(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full);

        if (string.IsNullOrEmpty(root))
            return full;

        var current = root;

        foreach (var segment in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            var target = TryResolveLink(current);
            if (target is not null)
            {
                // A relative link target resolves against the directory holding the link.
                current = Path.IsPathRooted(target)
                    ? Path.GetFullPath(target)
                    : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? root, target));
            }
        }

        return current;
    }

    /// <summary>
    /// Resolves <paramref name="relativePath"/> beneath <paramref name="resolvedRoot"/> and
    /// reports whether the result is genuinely contained by it.
    /// </summary>
    /// <param name="resolvedRoot">
    /// A root already passed through <see cref="ResolveFinal"/>. Resolving it per request would
    /// be wasted work; mounts resolve their root once at registration.
    /// </param>
    /// <param name="relativePath">Untrusted path fragment from the request.</param>
    /// <param name="fullPath">The resolved absolute path, valid only when this returns true.</param>
    internal static bool TryResolveWithin(
        string resolvedRoot,
        string relativePath,
        out string fullPath)
    {
        fullPath = string.Empty;

        // Reject absolute paths and rooted fragments outright rather than letting Path.Combine
        // silently discard the root.
        if (Path.IsPathRooted(relativePath))
            return false;

        var candidate = ResolveFinal(
            Path.Combine(resolvedRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));

        if (!IsWithin(resolvedRoot, candidate))
            return false;

        fullPath = candidate;
        return true;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself or sits beneath it.
    /// Both are expected to be resolved already.
    /// </summary>
    internal static bool IsWithin(string root, string candidate)
    {
        var normalisedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (candidate.Equals(normalisedRoot, StringComparison.Ordinal))
            return true;

        // The separator matters: without it "/srv/docs-private" would count as inside "/srv/docs".
        return candidate.StartsWith(
            normalisedRoot + Path.DirectorySeparatorChar,
            StringComparison.Ordinal);
    }

    private static string? TryResolveLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path)
                ? new DirectoryInfo(path)
                : new FileInfo(path);

            return info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
        }
        catch (IOException)
        {
            // Broken or cyclic link — treat as unresolvable and let containment judge the
            // unresolved path, which is the conservative outcome.
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
