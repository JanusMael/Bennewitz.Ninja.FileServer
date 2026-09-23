using System.Net;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// <see cref="FileServerMountOptions.ExposedSensitivePatterns"/>: the scoped opt-out from
/// refusing sensitive paths. A match exposes exactly the paths it names — judged on the whole
/// canonical path, case-sensitively — and nothing else that happens to be sensitive.
/// </summary>
public sealed class ExposedSensitivePatternTests
{
    private const string Secret = "SENSITIVE-CONTENT";

    [Fact]
    public async Task ExposedPattern_ServesWhatItNames_AndNothingElse()
    {
        using var root = WellKnownFixture();

        await using var host = await StartAsync(root, o => o.ExposedSensitivePatterns = [".well-known/**"]);

        Assert.Equal("CONTACT", await host.Client.GetStringAsync("/docs/.well-known/security.txt"));
        Assert.Equal("TOKEN", await host.Client.GetStringAsync("/docs/.well-known/acme-challenge/tok"));

        await SensitivePathTests.AssertRefused(host, "/docs/.git/config");
        await SensitivePathTests.AssertRefused(host, "/docs/.env");
        await SensitivePathTests.AssertServed(host, "/docs/visible.txt");
    }

    [Fact]
    public async Task ContentsPattern_LeavesTheDirectoryItselfRefusedAndUnlisted()
    {
        using var root = WellKnownFixture();

        await using var host = await StartAsync(root, o => o.ExposedSensitivePatterns = [".well-known/**"]);

        await SensitivePathTests.AssertRefused(host, "/docs/.well-known");

        var listing = await ListingOf(host, "/docs");
        Assert.Contains("href=\"/docs/visible.txt\"", listing, StringComparison.Ordinal);
        Assert.DoesNotContain(".well-known", listing, StringComparison.Ordinal);

        // A subdirectory the pattern does cover lists its entries.
        var challenges = await ListingOf(host, "/docs/.well-known/acme-challenge");
        Assert.Contains("href=\"/docs/.well-known/acme-challenge/tok\"", challenges, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExposingTheDirectoryToo_MakesItListableAndListed()
    {
        using var root = WellKnownFixture();

        await using var host = await StartAsync(root, o =>
            o.ExposedSensitivePatterns = [".well-known", ".well-known/**"]);

        var parent = await ListingOf(host, "/docs");
        Assert.Contains("href=\"/docs/.well-known\"", parent, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", parent, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", parent, StringComparison.Ordinal);

        var own = await ListingOf(host, "/docs/.well-known");
        Assert.Contains("href=\"/docs/.well-known/security.txt\"", own, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExposedPath_CanStillBeUnlisted()
    {
        using var root = WellKnownFixture();

        await using var host = await StartAsync(root, o =>
        {
            o.ExposedSensitivePatterns = [".well-known", ".well-known/**"];
            o.UnlistedPatterns = [".well-known/security.txt"];
        });

        var own = await ListingOf(host, "/docs/.well-known");
        Assert.Contains("href=\"/docs/.well-known/acme-challenge\"", own, StringComparison.Ordinal);
        Assert.DoesNotContain("security.txt", own, StringComparison.Ordinal);

        Assert.Equal("CONTACT", await host.Client.GetStringAsync("/docs/.well-known/security.txt"));
    }

    [Fact]
    public async Task AllowedExtensions_StillRefusesAnExposedFile()
    {
        using var root = WellKnownFixture();
        root.WriteFile("readme.md", "# ok");

        await using var host = await StartAsync(root, o =>
        {
            o.AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".md" };
            o.ExposedSensitivePatterns = [".well-known/**"];
        });

        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/docs/.well-known/security.txt")).StatusCode);
        await SensitivePathTests.AssertServed(host, "/docs/readme.md");
    }

    [Theory]
    [InlineData("/docs/.well-known/../.git/config")]
    [InlineData("/docs/.well-known/%2e%2e/.git/config")]
    public async Task DotSegmentsOutOfAnExposedDirectory_AreJudgedOnTheCanonicalPath(string url)
    {
        using var root = WellKnownFixture();

        await using var host = await StartAsync(root, o => o.ExposedSensitivePatterns = [".well-known/**"]);

        // The raw string starts with the exposed prefix; the path it names is .git/config.
        await SensitivePathTests.AssertRefused(host, url);
        await SensitivePathTests.AssertServed(host, "/docs/.well-known/security.txt");
    }

    [Fact]
    public async Task Exposure_IsCaseSensitive()
    {
        using var root = WellKnownFixture();

        // On Linux this is a second, different directory the pattern must not reach. Where the
        // filesystem ignores case it is the same directory, and a request in the wrong case is
        // refused rather than served: ordinal matching can only deny.
        if (OperatingSystem.IsLinux())
            root.WriteFile(".WELL-KNOWN/security.txt", Secret);

        await using var host = await StartAsync(root, o => o.ExposedSensitivePatterns = [".well-known/**"]);

        await SensitivePathTests.AssertRefused(host, "/docs/.WELL-KNOWN/security.txt");
        await SensitivePathTests.AssertServed(host, "/docs/.well-known/security.txt");
    }

    [WindowsFact]
    public async Task ExposedPattern_ServesAHiddenAttributeFile()
    {
        using var root = new TempDirectory();
        root.WriteFile("visible.txt");
        var hidden = root.WriteFile("hid.txt", "HIDDEN-BUT-EXPOSED");
        File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);
        var other = root.WriteFile("other.txt", Secret);
        File.SetAttributes(other, File.GetAttributes(other) | FileAttributes.Hidden);

        await using var host = await StartAsync(root, o => o.ExposedSensitivePatterns = ["hid.txt"]);

        Assert.Equal("HIDDEN-BUT-EXPOSED", await host.Client.GetStringAsync("/docs/hid.txt"));
        Assert.Contains("href=\"/docs/hid.txt\"", await ListingOf(host, "/docs"), StringComparison.Ordinal);
        await SensitivePathTests.AssertRefused(host, "/docs/other.txt");
    }

    [Fact]
    public async Task EveryFileIsListedExactlyWhenItIsServed_WithExposure()
    {
        using var root = WellKnownFixture();
        root.WriteFile("sub/.dot.txt");
        root.WriteFile(".well-known/.nested.txt");

        // The directory is exposed as well as its contents; with contents alone it is refused
        // and cannot list them (decision 6e), which this helper would read as a mismatch.
        await using var host = await StartAsync(root, o =>
            o.ExposedSensitivePatterns = [".well-known", ".well-known/**"]);

        await SensitivePathTests.AssertListedExactlyWhenServed(host,
        [
            "visible.txt", ".env", ".git/config", "sub/.dot.txt",
            ".well-known/security.txt", ".well-known/acme-challenge/tok", ".well-known/.nested.txt",
        ]);
    }

    private static TempDirectory WellKnownFixture()
    {
        var root = new TempDirectory();
        root.WriteFile("visible.txt", "ok");
        root.WriteFile(".env", Secret);
        root.WriteFile(".git/config", Secret);
        root.WriteFile(".well-known/security.txt", "CONTACT");
        root.WriteFile(".well-known/acme-challenge/tok", "TOKEN");
        return root;
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
