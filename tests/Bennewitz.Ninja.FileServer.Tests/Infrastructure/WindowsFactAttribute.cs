namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that skips everywhere but Windows, for behaviour that only
/// Windows filesystem semantics can produce — the Hidden and System attributes as something
/// separate from a leading dot, backslash as a separator, alternate data streams.
/// </summary>
/// <remarks>
/// A skip rather than an early return inside the test, for the same reason as
/// <see cref="SymlinkFactAttribute"/>: the run should say the case was not exercised here, not
/// report it as passing.
/// </remarks>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        if (!OperatingSystem.IsWindows())
            Skip = "Exercises Windows-only filesystem semantics.";
    }
}
