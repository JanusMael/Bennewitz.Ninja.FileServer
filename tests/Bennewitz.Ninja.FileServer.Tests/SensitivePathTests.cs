using System.Net;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// Sensitive paths — any segment below the mount root that is dot-prefixed, Hidden or System —
/// are refused on direct request, not merely left out of listings. Listings have always skipped
/// them; downloads did not, which served <c>.env</c> and <c>.git/config</c> to anyone who typed
/// the URL.
/// </summary>
/// <remarks>
/// Every refusal is asserted alongside a sibling that is served, so a mount that refused
/// everything could not pass, and alongside the content being absent from the body, so a
/// "404" page that echoed the file could not pass either.
/// </remarks>
public sealed class SensitivePathTests
{
    private const string Secret = "SENSITIVE-CONTENT";

    [Fact]
    public async Task DotPrefixedFile_IsRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile(".secret.txt", Secret);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/.secret.txt");
        await AssertServed(host, "/docs/visible.txt");
    }

    [Fact]
    public async Task DotPrefixedDirectory_AndItsContents_AreRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile(".private/inside.txt", Secret);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/.private");
        await AssertRefused(host, "/docs/.private/inside.txt");
        await AssertServed(host, "/docs/visible.txt");
    }

    [Fact]
    public async Task SensitiveDirectorySegment_RefusesANonSensitiveLeaf()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile(".git/config", Secret);

        await using var host = await StartAsync(root);

        // The leaf is an ordinary name. Checking only the final segment would serve it.
        await AssertRefused(host, "/docs/.git/config");
        await AssertServed(host, "/docs/visible.txt");
    }

    [Fact]
    public async Task SensitiveLeaf_BelowAnOrdinaryDirectory_IsRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("sub/visible.txt", "ok");
        root.WriteFile("sub/.env", Secret);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/sub/.env");
        await AssertServed(host, "/docs/sub/visible.txt");
    }

    [Fact]
    public async Task DotPrefixedMarkdown_IsRefusedRenderedAndRaw()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.md", "# ok");
        root.WriteFile(".notes.md", Secret);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/.notes.md");
        await AssertRefused(host, "/docs/.notes.md?raw=1");
        await AssertServed(host, "/docs/visible.md");
    }

    [Theory]
    [InlineData("/docs/sub/../.env")]
    [InlineData("/docs/sub/%2e%2e/.env")]
    public async Task DotSegmentForms_AreRefused(string url)
    {
        using var root = new TempDirectory();
        root.WriteFile("sub/visible.txt", "ok");
        root.WriteFile(".env", Secret);

        await using var host = await StartAsync(root);

        await AssertRefused(host, url);
        await AssertServed(host, "/docs/sub/visible.txt");
    }

    [WindowsFact]
    public async Task HiddenAndSystemAttributes_AreRefused_AsFilesAndAsDirectories()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        AddAttributes(root.WriteFile("hidden.txt", Secret), FileAttributes.Hidden);
        AddAttributes(root.WriteFile("system.txt", Secret), FileAttributes.System);
        root.WriteFile("hdir/inside.txt", Secret);
        AddAttributes(Path.Combine(root.Path, "hdir"), FileAttributes.Hidden);
        root.WriteFile("sdir/inside.txt", Secret);
        AddAttributes(Path.Combine(root.Path, "sdir"), FileAttributes.System);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/hidden.txt");
        await AssertRefused(host, "/docs/system.txt");
        await AssertRefused(host, "/docs/hdir/inside.txt");
        await AssertRefused(host, "/docs/sdir/inside.txt");
        await AssertServed(host, "/docs/visible.txt");
    }

    [WindowsFact]
    public async Task BackslashSeparatedForm_IsRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("sub/visible.txt", "ok");
        root.WriteFile("sub/.env", Secret);

        await using var host = await StartAsync(root);

        // Decodes to one segment, "sub\.env", with no leading dot, which Windows still resolves
        // to the dotfile.
        await AssertRefused(host, "/docs/sub%5C.env");
        await AssertServed(host, "/docs/sub/visible.txt");
    }

    [WindowsFact]
    public async Task AlternateDataStreamForms_AreRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile(".env", Secret);
        AddAttributes(root.WriteFile("hidden.txt", Secret), FileAttributes.Hidden);

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/.env::$DATA");
        await AssertRefused(host, "/docs/hidden.txt::$DATA");
        await AssertServed(host, "/docs/visible.txt");
    }

    [ShortNameFact]
    public async Task ShortNameAlias_OfADotfile_IsRefused()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        var alias = ShortNames.AliasOf(root.WriteFile(".environment", Secret));

        Assert.NotNull(alias);
        Assert.False(alias!.StartsWith('.'), $"Expected an alias without the leading dot, got '{alias}'.");

        await using var host = await StartAsync(root);

        await AssertRefused(host, "/docs/" + Uri.EscapeDataString(alias));
        await AssertServed(host, "/docs/visible.txt");
    }

    [SymlinkFact]
    public async Task OrdinaryLink_ToASensitiveTarget_IsListedAndServedUnderItsOwnName()
    {
        using var root = new TempDirectory();
        var target = root.WriteFile(".secret.txt", Secret);
        File.CreateSymbolicLink(Path.Combine(root.Path, "link.txt"), target);

        await using var host = await StartAsync(root);

        var listing = await host.Client.GetStringAsync("/docs");
        Assert.Contains("href=\"/docs/link.txt\"", listing, StringComparison.Ordinal);
        Assert.Equal(Secret, await host.Client.GetStringAsync("/docs/link.txt"));
        await AssertRefused(host, "/docs/.secret.txt");
    }

    [Fact]
    public async Task EveryFileIsListedExactlyWhenItIsServed()
    {
        using var root = new TempDirectory();

        var files = new List<string>
        {
            "normal.txt", ".dot.txt",
            "sub/normal.txt", "sub/.dot.txt",
            ".dotdir/normal.txt", ".dotdir/sub/normal.txt",
        };

        foreach (var file in files)
            root.WriteFile(file);

        if (OperatingSystem.IsWindows())
        {
            AddAttributes(root.WriteFile("hid.txt"), FileAttributes.Hidden);
            AddAttributes(root.WriteFile("sub/hid.txt"), FileAttributes.Hidden);
            root.WriteFile("hdir/normal.txt");
            AddAttributes(Path.Combine(root.Path, "hdir"), FileAttributes.Hidden);
            files.AddRange(["hid.txt", "sub/hid.txt", "hdir/normal.txt"]);
        }

        await using var host = await StartAsync(root);

        await AssertListedExactlyWhenServed(host, files);
    }

    internal static async Task AssertListedExactlyWhenServed(FileServerTestHost host, IEnumerable<string> files)
    {
        var mismatches = new List<string>();

        foreach (var file in files)
        {
            var parent = Path.GetDirectoryName(file.Replace('/', Path.DirectorySeparatorChar))!
                .Replace(Path.DirectorySeparatorChar, '/');
            var listingUrl = parent.Length == 0 ? "/docs" : $"/docs/{parent}";

            var listingResponse = await host.Client.GetAsync(listingUrl);
            var listed = listingResponse.StatusCode == HttpStatusCode.OK
                && (await listingResponse.Content.ReadAsStringAsync())
                    .Contains($"href=\"/docs/{file}\"", StringComparison.Ordinal);

            var served = (await host.Client.GetAsync($"/docs/{file}")).StatusCode == HttpStatusCode.OK;

            if (listed != served)
                mismatches.Add($"{file}: listed={listed}, served={served}");
        }

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    internal static async Task AssertRefused(FileServerTestHost host, string url)
    {
        var response = await host.Client.GetAsync(url);

        Assert.True(
            response.StatusCode == HttpStatusCode.NotFound,
            $"Expected 404 for {url}, got {(int)response.StatusCode}.");
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    internal static async Task AssertServed(FileServerTestHost host, string url)
    {
        var response = await host.Client.GetAsync(url);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected 200 for {url}, got {(int)response.StatusCode}.");
    }

    private static void AddAttributes(string path, FileAttributes attributes) =>
        File.SetAttributes(path, File.GetAttributes(path) | attributes);

    private static Task<FileServerTestHost> StartAsync(TempDirectory root) =>
        FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o => o.RootPath = root.Path));
}
