using System.Net;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// <see cref="FileServerMountOptions.UnlistedPatterns"/>: matching entries are left out of
/// listings and still served at their exact URL. Unlisting changes what is advertised, never
/// what is served — in either direction.
/// </summary>
public sealed class UnlistedPatternTests
{
    private static readonly IReadOnlySet<string> Everything =
        new HashSet<string>(0, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void RootAnchoredPattern_MatchesOnlyAtTheRoot()
    {
        using var root = new TempDirectory();
        root.WriteFile("a.key");
        root.WriteFile("keep.txt");
        root.WriteFile("sub/a.key");
        root.WriteFile("sub/keep.txt");

        var listing = Listing(root, "*.key");

        Assert.Equal(["keep.txt", "sub"], Names(listing, string.Empty));
        Assert.Equal(["a.key", "keep.txt"], Names(listing, "sub"));
    }

    [Fact]
    public void DoubleStarPattern_MatchesAtAnyDepth()
    {
        using var root = new TempDirectory();
        root.WriteFile("a.key");
        root.WriteFile("keep.txt");
        root.WriteFile("sub/deeper/a.key");
        root.WriteFile("sub/deeper/keep.txt");

        var listing = Listing(root, "**/*.key");

        Assert.Equal(["keep.txt", "sub"], Names(listing, string.Empty));
        Assert.Equal(["keep.txt"], Names(listing, "sub/deeper"));
    }

    [Fact]
    public void DirectoryRelativePattern_MatchesOnlyInsideThatDirectory()
    {
        using var root = new TempDirectory();
        root.WriteFile("a.md");
        root.WriteFile("drafts/a.md");
        root.WriteFile("drafts/keep.txt");

        var listing = Listing(root, "drafts/*.md");

        Assert.Equal(["a.md", "drafts"], Names(listing, string.Empty));
        Assert.Equal(["keep.txt"], Names(listing, "drafts"));
    }

    [Fact]
    public void BareDirectoryName_HidesTheDirectory_WhileItsContentsPatternDoesNot()
    {
        using var root = new TempDirectory();
        root.WriteFile("keep.txt");
        root.WriteFile("private/inside.txt");

        Assert.Equal(["keep.txt"], Names(Listing(root, "private"), string.Empty));

        var contentsOnly = Listing(root, "private/**");
        Assert.Equal(["keep.txt", "private"], Names(contentsOnly, string.Empty));
        Assert.Empty(Names(contentsOnly, "private"));
    }

    [Fact]
    public void Matching_IgnoresCase()
    {
        using var root = new TempDirectory();
        root.WriteFile("Drafts/A.MD");
        root.WriteFile("Drafts/keep.txt");

        Assert.Equal(["keep.txt"], Names(Listing(root, "drafts/*.md"), "Drafts"));
    }

    [Fact]
    public async Task UnlistedFile_IsMissingFromTheListingAndServedByUrl()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile("report.pdf", "REPORT-CONTENT");

        await using var host = await StartAsync(root, o => o.UnlistedPatterns = ["report.pdf"]);

        var listing = await ListingOf(host, "/docs");
        Assert.Contains("href=\"/docs/visible.txt\"", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("report.pdf", listing, StringComparison.Ordinal);

        Assert.Equal("REPORT-CONTENT", await host.Client.GetStringAsync("/docs/report.pdf"));
    }

    [Fact]
    public async Task UnlistedDirectory_IsMissingFromItsParent_ButItsOwnUrlListsItsContents()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        root.WriteFile("private/inside.txt", "INSIDE");

        await using var host = await StartAsync(root, o => o.UnlistedPatterns = ["private"]);

        var parent = await ListingOf(host, "/docs");
        Assert.Contains("href=\"/docs/visible.txt\"", parent, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"/docs/private\"", parent, StringComparison.Ordinal);

        var own = await ListingOf(host, "/docs/private");
        Assert.Contains("href=\"/docs/private/inside.txt\"", own, StringComparison.Ordinal);
        Assert.Equal("INSIDE", await host.Client.GetStringAsync("/docs/private/inside.txt"));
    }

    [Fact]
    public async Task AllowedExtensions_StillRefusesAnUnlistedFile()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md", "# ok");
        root.WriteFile("secret.txt", "SENSITIVE-CONTENT");

        await using var host = await StartAsync(root, o =>
        {
            o.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" };
            o.UnlistedPatterns = ["secret.txt"];
        });

        await SensitivePathTests.AssertRefused(host, "/docs/secret.txt");
        await SensitivePathTests.AssertServed(host, "/docs/readme.md");
    }

    [Fact]
    public async Task UnlistedMarkdown_StillRendersAndServesRaw()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root, o => o.UnlistedPatterns = ["*.md"]);

        var listing = await ListingOf(host, "/docs");
        Assert.Contains("href=\"/docs/visible.txt\"", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("notes.md", listing, StringComparison.Ordinal);

        var rendered = await host.Client.GetAsync("/docs/notes.md");
        Assert.Equal("text/html", rendered.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1", await rendered.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        Assert.Equal("# Heading", await host.Client.GetStringAsync("/docs/notes.md?raw=1"));
    }

    [Fact]
    public async Task BrowsingDisabled_WithPatterns_StillServesFilesAndRefusesSensitiveOnes()
    {
        using var root = new TempDirectory();
        root.WriteFile("report.pdf", "REPORT-CONTENT");
        root.WriteFile(".env", "SENSITIVE-CONTENT");

        await using var host = await StartAsync(root, o =>
        {
            o.EnableDirectoryBrowsing = false;
            o.UnlistedPatterns = ["report.pdf"];
        });

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs")).StatusCode);
        Assert.Equal("REPORT-CONTENT", await host.Client.GetStringAsync("/docs/report.pdf"));
        await SensitivePathTests.AssertRefused(host, "/docs/.env");
    }

    [Fact]
    public async Task UnlistedPattern_CannotRescueASensitivePath()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        root.WriteFile(".env", "SENSITIVE-CONTENT");

        await using var host = await StartAsync(root, o => o.UnlistedPatterns = [".env"]);

        await SensitivePathTests.AssertRefused(host, "/docs/.env");
        await SensitivePathTests.AssertServed(host, "/docs/visible.txt");
    }

    private static ListingFileProvider Listing(TempDirectory root, params string[] unlisted) =>
        new(
            new PhysicalFileProvider(root.ResolvedPath, ExclusionFilters.None),
            Everything,
            new SensitivePathPolicy(root.ResolvedPath),
            GlobPatterns.Normalise(unlisted, nameof(FileServerMountOptions.UnlistedPatterns)));

    private static List<string> Names(IFileProvider provider, string subpath)
    {
        var contents = provider.GetDirectoryContents(subpath);

        // A directory that does not list at all must not read as "listed nothing".
        Assert.True(contents.Exists, $"'{subpath}' did not list.");

        return contents.Select(entry => entry.Name).OrderBy(name => name, StringComparer.Ordinal).ToList();
    }

    private static async Task<string> ListingOf(FileServerTestHost host, string url)
    {
        var response = await host.Client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync();
    }

    private static Task<FileServerTestHost> StartAsync(TempDirectory root, Action<FileServerMountOptions> configure) =>
        FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                configure(o);
            }));
}
