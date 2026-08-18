namespace Bennewitz.Ninja.FileServer.Tests.Infrastructure;

/// <summary>
/// A disposable directory under the system temp path, for tests that need real files.
/// Containment is a filesystem property, so the tests that cover it work on real directories
/// rather than an abstraction that would answer differently from the thing being protected.
/// </summary>
internal sealed class TempDirectory : IDisposable
{
    internal TempDirectory(string prefix = "bnfs")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"{prefix}-{Guid.NewGuid():N}");

        Directory.CreateDirectory(Path);

        // The temp path itself may sit behind a link (on macOS /tmp is one, and Windows temp
        // paths can be redirected), so the resolved form is what mounts compare against.
        ResolvedPath = FileServerPath.ResolveFinal(Path);
    }

    /// <summary>The directory as created.</summary>
    internal string Path { get; }

    /// <summary>The directory with every link along it resolved, as a mount would hold it.</summary>
    internal string ResolvedPath { get; }

    /// <summary>Creates a file and any directories leading to it. Returns its full path.</summary>
    internal string WriteFile(string relativePath, string contents = "x")
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, contents);
        return full;
    }

    /// <summary>Creates a subdirectory and returns its full path.</summary>
    internal string CreateSubdirectory(string relativePath)
    {
        var full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A test that leaves a handle open must not fail the run over cleanup.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
