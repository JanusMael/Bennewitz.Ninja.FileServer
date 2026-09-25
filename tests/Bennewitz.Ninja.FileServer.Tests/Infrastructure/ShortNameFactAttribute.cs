using System.Runtime.InteropServices;

namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that skips unless the temp volume generates 8.3 short names —
/// on by default for a Windows system volume, off by default elsewhere and configurable per
/// volume, so the only reliable test is to create a file and ask for its alias.
/// </summary>
public sealed class ShortNameFactAttribute : FactAttribute
{
    public ShortNameFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!ShortNames.Available)
            Skip = "The temp volume does not generate 8.3 short names (or this is not Windows).";
    }
}

internal static class ShortNames
{
    private static readonly Lazy<bool> Probe = new(TryProbe);

    internal static bool Available => Probe.Value;

    /// <summary>
    /// The 8.3 alias of an existing file's final segment, or <c>null</c> when it has none.
    /// </summary>
    internal static string? AliasOf(string fullPath)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var buffer = new char[1024];
        var length = GetShortPathNameW(fullPath, buffer, (uint)buffer.Length);
        if (length == 0 || length >= buffer.Length)
            return null;

        var alias = Path.GetFileName(new string(buffer, 0, (int)length));
        return alias.Equals(Path.GetFileName(fullPath), StringComparison.Ordinal) ? null : alias;
    }

    private static bool TryProbe()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        using var root = new TempDirectory("bnfs-shortname-probe");
        return AliasOf(root.WriteFile(".long-probe-name")) is not null;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, char[] shortPath, uint bufferLength);
}
