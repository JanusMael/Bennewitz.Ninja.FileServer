namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// A <see cref="FactAttribute"/> that skips when this machine will not let the test process
/// create symbolic links — the normal case on Windows without Developer Mode or elevation.
/// </summary>
/// <remarks>
/// Skipping is decided by trying it once rather than by inspecting the OS or privileges:
/// what matters is whether creation actually succeeds here, and that depends on Developer
/// Mode, elevation, group policy, and the filesystem all at once. Reporting these as skipped
/// keeps the reason visible in the run, where a silently absent test would not be.
/// </remarks>
public sealed class SymlinkFactAttribute : FactAttribute
{
    public SymlinkFactAttribute()
    {
        if (!SymlinkSupport.Available)
            Skip = SymlinkSupport.SkipReason;
    }
}

internal static class SymlinkSupport
{
    internal const string SkipReason =
        "Creating symbolic links is not permitted for this process " +
        "(Windows requires Developer Mode or elevation).";

    private static readonly Lazy<bool> Probe = new(TryCreateLink);

    internal static bool Available => Probe.Value;

    private static bool TryCreateLink()
    {
        var root = Path.Combine(Path.GetTempPath(), $"bnfs-symlink-probe-{Guid.NewGuid():N}");

        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Combine(root, "link"), target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
