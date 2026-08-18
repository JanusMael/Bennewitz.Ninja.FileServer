using Bennewitz.Ninja.FileServer.Tests.Infrastructure;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// Containment: the single decision that keeps a request inside its mount. These are the tests
/// whose failure is a vulnerability rather than a defect, so they exercise the real filesystem.
/// </summary>
public sealed class FileServerPathTests
{
    [Fact]
    public void TryResolveWithin_PlainRelativePath_ResolvesBeneathTheRoot()
    {
        using var root = new TempDirectory();
        var expected = root.WriteFile("hello.txt");

        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, "hello.txt", out var resolved);

        Assert.True(contained);
        Assert.Equal(expected, resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void TryResolveWithin_NestedRelativePath_ResolvesBeneathTheRoot()
    {
        using var root = new TempDirectory();
        var expected = root.WriteFile("sub/deep/file.txt");

        var contained = FileServerPath.TryResolveWithin(
            root.ResolvedPath, "sub/deep/file.txt", out var resolved);

        Assert.True(contained);
        Assert.Equal(expected, resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("../../outside.txt")]
    [InlineData("sub/../../outside.txt")]
    [InlineData("sub/deep/../../../outside.txt")]
    [InlineData("./../outside.txt")]
    public void TryResolveWithin_ParentTraversal_IsRefusedAtEveryDepth(string requestPath)
    {
        using var root = new TempDirectory();
        root.CreateSubdirectory("sub/deep");

        // The escape target genuinely exists, so a refusal cannot be mistaken for "no such file".
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(root.Path)!, "outside.txt"), "secret");

        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, requestPath, out var resolved);

        Assert.False(contained);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_TraversalThatReturnsInside_IsAllowed()
    {
        using var root = new TempDirectory();
        var expected = root.WriteFile("sub/file.txt");

        // Leaving and re-entering lands inside the root, so refusing it would be over-blocking.
        var contained = FileServerPath.TryResolveWithin(
            root.ResolvedPath, "sub/../sub/file.txt", out var resolved);

        Assert.True(contained);
        Assert.Equal(expected, resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void TryResolveWithin_AbsolutePath_IsRefused()
    {
        using var root = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var target = elsewhere.WriteFile("secret.txt");

        // Path.Combine would silently discard the root and hand back the absolute path.
        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, target, out var resolved);

        Assert.False(contained);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveWithin_MissingFile_StillReportsContainment()
    {
        using var root = new TempDirectory();

        // Containment is judged before the file is touched, so a path to a file that does not
        // exist must still resolve — the handler decides 404 afterwards.
        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, "absent.txt", out var resolved);

        Assert.True(contained);
        Assert.Equal(Path.Combine(root.ResolvedPath, "absent.txt"), resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [Fact]
    public void IsWithin_RootItself_IsContained()
    {
        Assert.True(FileServerPath.IsWithin(Root("srv", "docs"), Root("srv", "docs")));
    }

    [Fact]
    public void IsWithin_RootWithTrailingSeparator_IsContained()
    {
        var root = Root("srv", "docs") + Path.DirectorySeparatorChar;

        Assert.True(FileServerPath.IsWithin(root, Root("srv", "docs")));
    }

    [Fact]
    public void IsWithin_SiblingSharingTheRootsNamePrefix_IsNotContained()
    {
        // Without the separator in the comparison, "docs-private" reads as inside "docs".
        Assert.False(FileServerPath.IsWithin(Root("srv", "docs"), Root("srv", "docs-private")));
        Assert.False(FileServerPath.IsWithin(
            Root("srv", "docs"), Path.Combine(Root("srv", "docs-private"), "secret.txt")));
    }

    [Fact]
    public void IsWithin_PathDifferingOnlyByCase_IsNotContained()
    {
        // Ordinal, unconditionally: case-insensitive comparison would admit an escape on a
        // case-sensitive filesystem, where these are two genuinely different directories.
        Assert.False(FileServerPath.IsWithin(
            Root("srv", "docs"), Path.Combine(Root("srv", "DOCS"), "file.txt")));
    }

    [Fact]
    public void IsWithin_DescendantSeveralLevelsDown_IsContained()
    {
        Assert.True(FileServerPath.IsWithin(
            Root("srv", "docs"), Path.Combine(Root("srv", "docs"), "a", "b", "c.txt")));
    }

    [SymlinkFact]
    public void ResolveFinal_LinkedFile_ResolvesToItsTarget()
    {
        using var root = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var target = elsewhere.WriteFile("secret.txt");

        var link = Path.Combine(root.Path, "link.txt");
        File.CreateSymbolicLink(link, target);

        var resolved = FileServerPath.ResolveFinal(link);

        Assert.Equal(FileServerPath.ResolveFinal(target), resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    [SymlinkFact]
    public void TryResolveWithin_LinkedLeafPointingOutside_IsRefused()
    {
        using var root = new TempDirectory();
        using var elsewhere = new TempDirectory();
        var target = elsewhere.WriteFile("secret.txt", "classified");

        File.CreateSymbolicLink(Path.Combine(root.Path, "link.txt"), target);

        // Pure string canonicalisation sees a path inside the root and would allow this.
        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, "link.txt", out _);

        Assert.False(contained);
    }

    [SymlinkFact]
    public void TryResolveWithin_LinkedIntermediateDirectory_IsRefused()
    {
        using var root = new TempDirectory();
        using var elsewhere = new TempDirectory();
        elsewhere.WriteFile("nested/secret.txt", "classified");

        // The link is not the leaf: only resolving the final segment would miss this entirely.
        Directory.CreateSymbolicLink(Path.Combine(root.Path, "escape"), elsewhere.Path);

        var contained = FileServerPath.TryResolveWithin(
            root.ResolvedPath, "escape/nested/secret.txt", out _);

        Assert.False(contained);
    }

    [SymlinkFact]
    public void TryResolveWithin_LinkPointingBackInsideTheRoot_IsAllowed()
    {
        using var root = new TempDirectory();
        var target = root.WriteFile("real/file.txt", "fine");

        File.CreateSymbolicLink(Path.Combine(root.Path, "alias.txt"), target);

        var contained = FileServerPath.TryResolveWithin(root.ResolvedPath, "alias.txt", out var resolved);

        Assert.True(contained);
        Assert.Equal(FileServerPath.ResolveFinal(target), resolved, ignoreCase: OperatingSystem.IsWindows());
    }

    /// <summary>Builds an absolute path that is valid on the host OS.</summary>
    private static string Root(params string[] segments) =>
        Path.Combine(
            OperatingSystem.IsWindows() ? @"C:\" : "/",
            Path.Combine(segments));
}
