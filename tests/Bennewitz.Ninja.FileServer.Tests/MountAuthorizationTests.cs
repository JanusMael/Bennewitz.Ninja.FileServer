using System.Net;
using System.Text.RegularExpressions;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// The reason files are served from endpoints rather than static-file middleware: a convention
/// applied to the route group returned by <c>MapFileServer</c> has to cover downloads, not just
/// the listings that make them discoverable.
/// </summary>
public sealed class MountAuthorizationTests
{
    [Fact]
    public async Task RequireAuthorization_UnauthenticatedListing_IsChallenged()
    {
        using var root = new TempDirectory();
        root.WriteFile("secret.txt", "classified");

        await using var host = await StartProtectedAsync(root);

        var response = await host.Client.GetAsync("/private");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequireAuthorization_UnauthenticatedFileDownload_IsChallenged()
    {
        using var root = new TempDirectory();
        root.WriteFile("secret.txt", "classified");

        await using var host = await StartProtectedAsync(root);

        // The listing being protected is worth little if the file behind it is not. Static-file
        // middleware produces no endpoint, so this is the request that would have stayed open.
        var response = await host.Client.GetAsync("/private/secret.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain("classified", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RequireAuthorization_UnauthenticatedNestedDownload_IsChallenged()
    {
        using var root = new TempDirectory();
        root.WriteFile("nested/deep/secret.txt", "classified");

        await using var host = await StartProtectedAsync(root);

        var response = await host.Client.GetAsync("/private/nested/deep/secret.txt");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task RequireAuthorization_UnauthenticatedMarkdownRender_IsChallenged()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# classified");

        await using var host = await StartProtectedAsync(root);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/private/notes.md")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/private/notes.md?raw")).StatusCode);
    }

    [Fact]
    public async Task RequireAuthorization_AuthenticatedRequest_ServesTheFile()
    {
        using var root = new TempDirectory();
        root.WriteFile("secret.txt", "classified");

        await using var host = await StartProtectedAsync(root);

        var response = await host.Client.SendAsync(host.AuthenticatedGet("/private/secret.txt"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("classified", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task RequireAuthorization_AssetEndpoint_StaysAnonymous()
    {
        using var root = new TempDirectory();
        root.WriteFile("notes.md", "# hello");

        await using var host = await StartProtectedAsync(root);

        // The asset endpoint is mapped outside the group precisely so a challenge lands on a
        // styled page: the stylesheet has to load for a visitor who is not signed in yet.
        var assetUrl = await AssetUrlAsync(host, "/private/notes.md", "css/fileserver.css");

        var response = await host.Client.GetAsync(assetUrl);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("bnfs-root", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnprotectedMount_IsServedAnonymously()
    {
        using var root = new TempDirectory();
        root.WriteFile("public.txt", "open");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/public", o => o.RootPath = root.Path));

        Assert.Equal("open", await host.Client.GetStringAsync("/public/public.txt"));
    }

    [Fact]
    public async Task ProtectedAndOpenMounts_AreIndependent()
    {
        using var open = new TempDirectory();
        using var closed = new TempDirectory();
        open.WriteFile("open.txt", "open");
        closed.WriteFile("closed.txt", "closed");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
        {
            endpoints.MapFileServer("/open", o => o.RootPath = open.Path);
            endpoints.MapFileServer("/closed", o => o.RootPath = closed.Path).RequireAuthorization();
        });

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/open/open.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/closed/closed.txt")).StatusCode);
    }

    private static Task<FileServerTestHost> StartProtectedAsync(TempDirectory root) =>
        FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/private", o => o.RootPath = root.Path).RequireAuthorization());

    /// <summary>
    /// Reads an asset URL out of a rendered page rather than composing one, so the test cannot
    /// pass against a URL shape the component no longer emits.
    /// </summary>
    private static async Task<string> AssetUrlAsync(FileServerTestHost host, string pageUrl, string asset)
    {
        var page = await host.Client.SendAsync(host.AuthenticatedGet(pageUrl));
        var html = await page.Content.ReadAsStringAsync();

        var match = Regex.Match(html, $@"[""'](?<url>[^""']*{Regex.Escape(asset)})[""']");

        Assert.True(match.Success, $"No link to {asset} was found in the page at {pageUrl}.");

        return match.Groups["url"].Value;
    }
}
