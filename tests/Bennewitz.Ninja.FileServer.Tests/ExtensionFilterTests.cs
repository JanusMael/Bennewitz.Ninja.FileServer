using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.Extensions.FileProviders;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// The extension filter, at both places it is enforced: the provider that builds listings, and
/// the mount's own check that guards the download path. A filter applied in only one of the two
/// hides a file while still serving it.
/// </summary>
public sealed class ExtensionFilterTests
{
    private static readonly IReadOnlySet<string> MarkdownOnly =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" };

    private static readonly IReadOnlySet<string> Nothing =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void GetFileInfo_DisallowedExtension_ReportsNotFound()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.log");

        var provider = Filtered(root, MarkdownOnly);

        Assert.False(provider.GetFileInfo("notes.log").Exists);
    }

    [Fact]
    public void GetFileInfo_AllowedExtension_IsReturned()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md", "# hi");

        var provider = Filtered(root, MarkdownOnly);
        var file = provider.GetFileInfo("readme.md");

        Assert.True(file.Exists);
        Assert.Equal("readme.md", file.Name);
    }

    [Fact]
    public void GetFileInfo_ExtensionDifferingInCase_IsAllowed()
    {
        using var root = new TempDirectory();
        root.WriteFile("README.MD", "# hi");

        var provider = Filtered(root, MarkdownOnly);

        Assert.True(provider.GetFileInfo("README.MD").Exists);
    }

    [Fact]
    public void GetDirectoryContents_HidesDisallowedFilesButKeepsDirectories()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md");
        root.WriteFile("hello.txt");
        root.WriteFile("notes.log");
        root.CreateSubdirectory("sub");

        var names = Filtered(root, MarkdownOnly)
            .GetDirectoryContents(string.Empty)
            .Select(entry => entry.Name)
            .ToList();

        // Directories always survive the filter, or the tree cannot be navigated.
        Assert.Contains("sub", names);
        Assert.Contains("readme.md", names);
        Assert.DoesNotContain("hello.txt", names);
        Assert.DoesNotContain("notes.log", names);
    }

    [Fact]
    public void GetDirectoryContents_EmptyFilter_ReturnsEverything()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md");
        root.WriteFile("hello.txt");

        var names = Filtered(root, Nothing)
            .GetDirectoryContents(string.Empty)
            .Select(entry => entry.Name)
            .ToList();

        Assert.Contains("readme.md", names);
        Assert.Contains("hello.txt", names);
    }

    [Theory]
    [InlineData("readme.md", true)]
    [InlineData("README.MD", true)]
    [InlineData("hello.txt", false)]
    [InlineData("archive.md.zip", false)]
    [InlineData("noextension", false)]
    public void MountIsAllowed_AppliesTheFilterToTheDownloadPath(string fileName, bool expected)
    {
        using var root = new TempDirectory();
        var mount = Mount(root, MarkdownOnly);

        // Serving by physical path bypasses the file provider, so the mount re-checks here.
        Assert.Equal(expected, mount.IsAllowed(fileName));
    }

    [Theory]
    [InlineData("readme.md")]
    [InlineData("hello.txt")]
    [InlineData("noextension")]
    public void MountIsAllowed_EmptyFilter_AllowsEverything(string fileName)
    {
        using var root = new TempDirectory();
        var mount = Mount(root, Nothing);

        Assert.True(mount.IsAllowed(fileName));
    }

    [Fact]
    public void MountIsAllowed_EmptyStringInFilter_MatchesExtensionlessFiles()
    {
        using var root = new TempDirectory();
        var mount = Mount(root, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { string.Empty });

        Assert.True(mount.IsAllowed("LICENSE"));
        Assert.False(mount.IsAllowed("readme.md"));
    }

    [Theory]
    [InlineData("md")]
    [InlineData(".md")]
    [InlineData("MD")]
    [InlineData(" .md ")]
    public void Normalise_AcceptsEverySpellingOfTheSameExtension(string configured)
    {
        var normalised = FileServerMountOptions.NormaliseExtensions([configured]);

        Assert.Contains(".md", normalised);
        Assert.Single(normalised);
    }

    [Fact]
    public void Normalise_MixedInput_ProducesDottedCaseInsensitiveSet()
    {
        var normalised = FileServerMountOptions.NormaliseExtensions(["pdf", ".CSV", " txt ", ""]);

        Assert.Equal(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pdf", ".csv", ".txt", "" },
            normalised);

        // The empty entry means "files with no extension" and must never acquire a dot, or the
        // one way of listing extensionless files stops working.
        Assert.Contains("", normalised);
        Assert.DoesNotContain(".", normalised);
    }

    [Fact]
    public void Normalise_IsCaseInsensitiveAfterNormalising()
    {
        var normalised = FileServerMountOptions.NormaliseExtensions(["PDF"]);

        Assert.Contains(".pdf", normalised);
        Assert.Contains(".PDF", normalised);
    }

    [Fact]
    public void Normalise_NullOrEmptyInput_YieldsEmptySetMeaningEverythingIsServed()
    {
        Assert.Empty(FileServerMountOptions.NormaliseExtensions(null));
        Assert.Empty(FileServerMountOptions.NormaliseExtensions([]));
    }

    private static AllowedExtensionsFileProvider Filtered(TempDirectory root, IReadOnlySet<string> allowed) =>
        new(new PhysicalFileProvider(root.ResolvedPath), allowed);

    private static FileServerMount Mount(TempDirectory root, IReadOnlySet<string> allowed) =>
        new("/files", new FileServerMountOptions
        {
            RootPath = root.ResolvedPath,
            AllowedExtensions = allowed
        });
}
