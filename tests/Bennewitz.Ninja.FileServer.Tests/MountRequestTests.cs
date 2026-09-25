using System.Net;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// End-to-end request behaviour of a mount: what it serves, what it refuses, and the options
/// that change either. These go through a real pipeline because that is where the filter and
/// containment rules have to hold — a unit test of the same rules cannot prove the handler
/// consults them.
/// </summary>
public sealed class MountRequestTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Mount_ServesAFileWithItsContentTypeAndCacheHeader()
    {
        using var root = new TempDirectory();
        root.WriteFile("hello.txt", "hi");

        await using var host = await StartAsync(root);

        var response = await host.Client.GetAsync("/docs/hello.txt", Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Mount_ListsDirectoriesFirstAndThenFiles()
    {
        using var root = new TempDirectory();
        root.WriteFile("alpha.txt");
        root.CreateSubdirectory("zeta");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs", Cancel);

        // "zeta" sorts after "alpha" alphabetically, so its position proves directories lead.
        Assert.True(
            html.IndexOf("zeta", StringComparison.Ordinal) < html.IndexOf("alpha.txt", StringComparison.Ordinal),
            "Directories should be listed before files.");
    }

    [Fact]
    public async Task Mount_RendersMarkdownAsHtmlAndServesTheSourceOnRaw()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading\n\ntext");

        await using var host = await StartAsync(root);

        var rendered = await host.Client.GetAsync("/docs/notes.md", Cancel);
        var renderedBody = await rendered.Content.ReadAsStringAsync(Cancel);

        Assert.Equal("text/html", rendered.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<h1", renderedBody, StringComparison.Ordinal);

        var raw = await host.Client.GetAsync("/docs/notes.md?raw", Cancel);

        Assert.Equal("text/markdown", raw.Content.Headers.ContentType?.MediaType);
        Assert.Equal("# Heading\n\ntext", await raw.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Mount_RenderMarkdownDisabled_ServesTheSourceInstead()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.RenderMarkdown = false;
            }));

        var response = await host.Client.GetAsync("/docs/notes.md", Cancel);

        Assert.Equal("text/markdown", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("# Heading", await response.Content.ReadAsStringAsync(Cancel));
    }

    [Fact]
    public async Task Mount_DirectoryBrowsingDisabled_HidesListingsButStillServesFiles()
    {
        using var root = new TempDirectory();
        root.WriteFile("hello.txt", "hi");
        root.CreateSubdirectory("sub");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.EnableDirectoryBrowsing = false;
            }));

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs", Cancel)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/sub", Cancel)).StatusCode);
        Assert.Equal("hi", await host.Client.GetStringAsync("/docs/hello.txt", Cancel));
    }

    [Theory]
    [InlineData("/docs/../outside.txt")]
    [InlineData("/docs/sub/../../outside.txt")]
    [InlineData("/docs/%2e%2e/outside.txt")]
    [InlineData("/docs/..%2foutside.txt")]
    public async Task Mount_TraversalAttempt_IsRefused(string url)
    {
        using var root = new TempDirectory();
        root.CreateSubdirectory("sub");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(root.Path)!, "outside.txt"), "secret");

        await using var host = await StartAsync(root);

        var response = await host.Client.GetAsync(url, Cancel);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("secret", await response.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_AllowedExtensions_HidesAndRefusesFilteredFiles()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md", "# shown");
        root.WriteFile("hello.txt", "hidden");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" };
            }));

        var listing = await host.Client.GetStringAsync("/docs", Cancel);

        Assert.Contains("readme.md", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("hello.txt", listing, StringComparison.Ordinal);

        // Hidden from the listing is not enough: the direct URL must be refused as well, or the
        // filter is only a display convention.
        var direct = await host.Client.GetAsync("/docs/hello.txt", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);
        Assert.DoesNotContain("hidden", await direct.Content.ReadAsStringAsync(Cancel), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_AllowedExtensionsWithoutLeadingDot_BehavesLikeTheDottedForm()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md", "# shown");
        root.WriteFile("hello.txt", "hidden");

        // No leading dot. Path.GetExtension always yields the dotted form, so before the filter
        // was normalised this matched nothing: the mount registered cleanly, listed nothing, and
        // 404'd every file including the ones it was configured to allow.
        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "md" };
            }));

        var listing = await host.Client.GetStringAsync("/docs", Cancel);

        Assert.Contains("readme.md", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("hello.txt", listing, StringComparison.Ordinal);

        var allowed = await host.Client.GetAsync("/docs/readme.md", Cancel);
        var refused = await host.Client.GetAsync("/docs/hello.txt", Cancel);

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }

    [Fact]
    public async Task Mount_AllowedExtensions_DoesNotMutateTheCallersSet()
    {
        using var root = new TempDirectory();
        root.WriteFile("readme.md", "# shown");

        var callersSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "md" };

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.AllowedExtensions = callersSet;
            }));

        _ = await host.Client.GetStringAsync("/docs", Cancel);

        // Normalising by replacing the set, not by editing it: the caller may hold this instance
        // for its own purposes, and a mount is not entitled to rewrite it.
        Assert.Equal(new[] { "md" }, callersSet);
    }

    [Fact]
    public async Task Mount_MissingFile_IsNotFound()
    {
        using var root = new TempDirectory();

        await using var host = await StartAsync(root);

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/absent.txt", Cancel)).StatusCode);
    }

    [Fact]
    public async Task Mount_IncludeDefaultStylesDisabled_EmitsNoStylesheetOrToggle()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.IncludeDefaultStyles = false;
            }));

        var html = await host.Client.GetStringAsync("/docs/notes.md", Cancel);

        Assert.DoesNotContain("fileserver.css", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-bnfs-theme-toggle", html, StringComparison.Ordinal);

        // The markup itself is untouched, so a host can style it.
        Assert.Contains("bnfs-root", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_DefaultStyles_EmitTheStylesheetAndTheToggleTogether()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs/notes.md", Cancel);

        Assert.Contains("fileserver.css", html, StringComparison.Ordinal);
        Assert.Contains("data-bnfs-theme-toggle", html, StringComparison.Ordinal);
        Assert.Contains("fileserver.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_DefaultStyles_EmitsTheToggleAndItsScript()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs", Cancel);

        // The listing carries the same control as a document, so a pinned scheme survives
        // navigating between the two rather than reverting on every listing.
        Assert.Contains("data-bnfs-theme-toggle", html, StringComparison.Ordinal);
        Assert.Contains("fileserver.js", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_AtMountRoot_StillEmitsTheToggleWithNoUpLink()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs", Cancel);

        // The actions row exists only when it has something in it, and at the root the Up link
        // is absent — the toggle must not disappear with it.
        Assert.Contains("data-bnfs-theme-toggle", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Up</a>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listing_IncludeDefaultStylesDisabled_EmitsNoToggle()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.IncludeDefaultStyles = false;
            }));

        var html = await host.Client.GetStringAsync("/docs", Cancel);

        Assert.DoesNotContain("data-bnfs-theme-toggle", html, StringComparison.Ordinal);
        Assert.DoesNotContain("fileserver.js", html, StringComparison.Ordinal);
        Assert.Contains("bnfs-table", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_CustomCacheControl_IsSentWithFiles()
    {
        using var root = new TempDirectory();
        root.WriteFile("hello.txt", "hi");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.CacheControl = "public, max-age=60";
            }));

        var response = await host.Client.GetAsync("/docs/hello.txt", Cancel);

        Assert.Equal("public, max-age=60", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("../../../secret.txt")]
    [InlineData("..%2f..%2fsecret.txt")]
    [InlineData("css/../../../secret.txt")]
    public async Task AssetEndpoint_PathThatNavigatesRatherThanNames_IsRefused(string assetPath)
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs/notes.md", Cancel);
        var version = ExtractVersion(html);

        var response = await host.Client.GetAsync($"/docs/_fs/{version}/{assetPath}", Cancel);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AssetEndpoint_ServesEmbeddedAssetsWithImmutableCaching()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# Heading");

        await using var host = await StartAsync(root);

        var html = await host.Client.GetStringAsync("/docs/notes.md", Cancel);
        var version = ExtractVersion(html);

        foreach (var asset in new[] { "css/fileserver.css", "css/github-markdown.min.css", "js/fileserver.js" })
        {
            var response = await host.Client.GetAsync($"/docs/_fs/{version}/{asset}", Cancel);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync(Cancel));

            // Content-addressed by the version segment, so it is safe to cache indefinitely —
            // and saying so is the difference between one fetch and one per navigation.
            var cacheControl = response.Headers.CacheControl;
            Assert.NotNull(cacheControl);
            Assert.True(cacheControl!.Public);
            Assert.Equal(TimeSpan.FromDays(365), cacheControl.MaxAge);
        }
    }

    [Fact]
    public async Task Mount_DotPrefixedFile_IsOmittedFromListing()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        root.WriteFile(".secret.txt");

        await using var host = await StartAsync(root);

        var response = await host.Client.GetAsync("/docs", Cancel);
        var listing = await response.Content.ReadAsStringAsync(Cancel);

        // The sibling proves the listing rendered; without it an empty or failed page would pass.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("visible.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".secret.txt", listing, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mount_DotPrefixedDirectory_IsOmittedFromListing()
    {
        using var root = new TempDirectory();
        root.CreateSubdirectory("public-dir");
        root.WriteFile(".private/inside.txt");

        await using var host = await StartAsync(root);

        var response = await host.Client.GetAsync("/docs", Cancel);
        var listing = await response.Content.ReadAsStringAsync(Cancel);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("public-dir", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".private", listing, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task Mount_HiddenAttributeFile_IsOmittedFromListing()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        var concealed = root.WriteFile("concealed.txt");
        File.SetAttributes(concealed, File.GetAttributes(concealed) | FileAttributes.Hidden);

        await using var host = await StartAsync(root);

        var response = await host.Client.GetAsync("/docs");
        var listing = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("visible.txt", listing, StringComparison.Ordinal);
        Assert.DoesNotContain("concealed.txt", listing, StringComparison.Ordinal);
    }

    private static Task<FileServerTestHost> StartAsync(TempDirectory root) =>
        FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o => o.RootPath = root.Path));

    private static string ExtractVersion(string html)
    {
        var marker = "/docs/_fs/";
        var start = html.IndexOf(marker, StringComparison.Ordinal);

        Assert.True(start >= 0, "The page did not reference the asset endpoint.");

        start += marker.Length;
        var end = html.IndexOf('/', start);

        return html[start..end];
    }
}
