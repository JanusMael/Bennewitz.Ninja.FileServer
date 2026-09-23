using Microsoft.Extensions.FileSystemGlobbing;

namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Normalises, validates and compiles the glob lists a mount is configured with
/// (<see cref="FileServerMountOptions.UnlistedPatterns"/> and
/// <see cref="FileServerMountOptions.ExposedSensitivePatterns"/>), so both options share one
/// definition of what a pattern may be.
/// </summary>
/// <remarks>
/// Patterns are anchored at the mount root and matched against <c>/</c>-separated paths
/// relative to it: <c>*.key</c> matches <c>a.key</c> but not <c>sub/a.key</c>.
/// </remarks>
internal static class GlobPatterns
{
    /// <summary>
    /// Returns <paramref name="patterns"/> trimmed, with empties dropped, <c>\</c> converted to
    /// <c>/</c> and one leading <c>/</c> removed. Throws when a pattern is still rooted or has a
    /// <c>..</c> segment. The caller's list is never modified.
    /// </summary>
    /// <exception cref="ArgumentException">A pattern escapes the mount root.</exception>
    internal static IReadOnlyList<string> Normalise(IEnumerable<string>? patterns, string optionName)
    {
        if (patterns is null)
            return [];

        var normalised = new List<string>();

        foreach (var raw in patterns)
        {
            var pattern = raw.Trim().Replace('\\', '/');
            if (pattern.Length == 0)
                continue;

            // One slash only: "/x" means the root-relative "x", while "//server/x" is a UNC
            // path and must still be rejected below rather than quietly turned into "server/x".
            if (pattern.StartsWith('/'))
                pattern = pattern[1..];

            if (pattern.StartsWith('/')
                || Path.IsPathRooted(pattern)
                || pattern.Split('/').Any(segment => segment == ".."))
            {
                throw new ArgumentException(
                    $"{optionName} entry '{raw}' is not relative to the mount root. Patterns are " +
                    "matched against paths inside the served directory; remove any drive, UNC " +
                    "prefix or '..' segment.",
                    "configure");
            }

            normalised.Add(pattern);
        }

        return normalised;
    }

    /// <summary>
    /// Compiles <paramref name="patterns"/>, or returns <c>null</c> when there are none so
    /// callers can skip matching entirely.
    /// </summary>
    internal static Matcher? Compile(IReadOnlyList<string> patterns, StringComparison comparison)
    {
        if (patterns.Count == 0)
            return null;

        var matcher = new Matcher(comparison);
        foreach (var pattern in patterns)
            matcher.AddInclude(pattern);

        return matcher;
    }
}
