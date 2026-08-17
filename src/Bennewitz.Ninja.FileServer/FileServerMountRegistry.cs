namespace Bennewitz.Ninja.FileServer;

/// <summary>
/// Tracks every mount registered in the application so conflicting registrations fail loudly
/// during pipeline construction instead of misbehaving at request time. Also serves as the
/// marker proving <c>AddFileServer()</c> was called.
/// </summary>
internal sealed class FileServerMountRegistry
{
    private readonly List<FileServerMount> _mounts = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Validates a prospective mount against those already registered and records it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The prefix is already claimed, or the root overlaps an existing mount's root.
    /// </exception>
    internal void Register(FileServerMount mount)
    {
        lock (_gate)
        {
            foreach (var existing in _mounts)
            {
                if (existing.Prefix.Equals(mount.Prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"A file server is already mounted at '{mount.Prefix}'. Two mounts on the " +
                        "same prefix produce ambiguous routing; give each mount a distinct prefix.");
                }

                // Overlapping roots are an authorization bypass, not merely a redundancy:
                // authorization is applied per route, but files live at paths. If one mount's
                // root contains another's, the mount with the weaker policy can serve the other's
                // files, and the stricter policy protects nothing.
                if (FileServerPath.IsWithin(existing.ResolvedRoot, mount.ResolvedRoot)
                    || FileServerPath.IsWithin(mount.ResolvedRoot, existing.ResolvedRoot))
                {
                    throw new InvalidOperationException(
                        $"The root for mount '{mount.Prefix}' ('{mount.ResolvedRoot}') overlaps the " +
                        $"root for mount '{existing.Prefix}' ('{existing.ResolvedRoot}'). " +
                        "Overlapping roots defeat per-route authorization, because the mount with " +
                        "the weakest policy can serve the other's files. Use disjoint directories, " +
                        "or restrict a single mount with AllowedExtensions.");
                }
            }

            _mounts.Add(mount);
        }
    }
}
