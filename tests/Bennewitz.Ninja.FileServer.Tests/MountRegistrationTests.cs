using System.Net;
using Bennewitz.Ninja.FileServer.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// Registration-time behaviour: how a prefix is normalised, and which combinations are refused
/// while the pipeline is being built rather than at request time.
/// </summary>
public sealed class MountRegistrationTests
{
    [Theory]
    [InlineData("docs")]
    [InlineData("/docs")]
    [InlineData("/docs/")]
    [InlineData("docs/")]
    [InlineData("  /docs  ")]
    public async Task MapFileServer_NormalisesThePrefix(string prefix)
    {
        using var root = new TempDirectory();
        root.WriteFile("hello.txt", "hi");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer(prefix, o => o.RootPath = root.Path));

        var response = await host.Client.GetAsync("/docs/hello.txt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hi", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MapFileServer_ServesTheMountRootWithAndWithoutATrailingSlash()
    {
        using var root = new TempDirectory();
        root.WriteFile("hello.txt");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o => o.RootPath = root.Path));

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/docs/")).StatusCode);
    }

    [Fact]
    public async Task MapFileServer_DuplicatePrefix_IsRefused()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
            {
                endpoints.MapFileServer("/docs", o => o.RootPath = first.Path);
                endpoints.MapFileServer("/docs", o => o.RootPath = second.Path);
            }));

        Assert.Contains("already mounted", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapFileServer_PrefixDifferingOnlyByCase_IsRefused()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();

        // URLs match case-insensitively, so these two would route ambiguously.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
            {
                endpoints.MapFileServer("/docs", o => o.RootPath = first.Path);
                endpoints.MapFileServer("/DOCS", o => o.RootPath = second.Path);
            }));
    }

    [Fact]
    public async Task MapFileServer_RootNestedInsideAnExistingRoot_IsRefused()
    {
        using var outer = new TempDirectory();
        var inner = outer.CreateSubdirectory("inner");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
            {
                endpoints.MapFileServer("/outer", o => o.RootPath = outer.Path);
                endpoints.MapFileServer("/inner", o => o.RootPath = inner);
            }));

        Assert.Contains("overlaps", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapFileServer_ExistingRootNestedInsideANewRoot_IsRefused()
    {
        using var outer = new TempDirectory();
        var inner = outer.CreateSubdirectory("inner");

        // The reverse registration order must fail identically: whichever mount carries the
        // weaker policy can serve the files the other one protects.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
            {
                endpoints.MapFileServer("/inner", o => o.RootPath = inner);
                endpoints.MapFileServer("/outer", o => o.RootPath = outer.Path);
            }));
    }

    [Fact]
    public async Task MapFileServer_SameRootTwice_IsRefused()
    {
        using var root = new TempDirectory();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
            {
                endpoints.MapFileServer("/one", o => o.RootPath = root.Path);
                endpoints.MapFileServer("/two", o => o.RootPath = root.Path);
            }));
    }

    [Fact]
    public async Task MapFileServer_DisjointRoots_AreBothServedInIsolation()
    {
        using var first = new TempDirectory();
        using var second = new TempDirectory();
        first.WriteFile("one.txt", "first");
        second.WriteFile("two.txt", "second");

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
        {
            endpoints.MapFileServer("/first", o => o.RootPath = first.Path);
            endpoints.MapFileServer("/second", o => o.RootPath = second.Path);
        });

        Assert.Equal("first", await host.Client.GetStringAsync("/first/one.txt"));
        Assert.Equal("second", await host.Client.GetStringAsync("/second/two.txt"));

        // Neither mount can reach the other's files.
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.GetAsync("/first/two.txt")).StatusCode);
    }

    [Fact]
    public async Task MapFileServer_SiblingRootSharingANamePrefix_IsAllowed()
    {
        using var parent = new TempDirectory();
        var docs = parent.CreateSubdirectory("docs");
        var docsPrivate = parent.CreateSubdirectory("docs-private");

        // "docs-private" is not inside "docs" — refusing this pair would be over-blocking.
        await using var host = await FileServerTestHost.StartAsync(endpoints =>
        {
            endpoints.MapFileServer("/docs", o => o.RootPath = docs);
            endpoints.MapFileServer("/private", o => o.RootPath = docsPrivate);
        });

        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/docs")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client.GetAsync("/private")).StatusCode);
    }

    [Fact]
    public async Task MapFileServer_WithoutAddFileServer_ExplainsWhatIsMissing()
    {
        using var root = new TempDirectory();

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();

            var app = builder.Build();
            app.MapFileServer("/docs", o => o.RootPath = root.Path);
        });

        Assert.Contains("AddFileServer", error.Message, StringComparison.Ordinal);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task MapFileServer_WithoutARootPath_IsRefused()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
                endpoints.MapFileServer("/docs")));

        Assert.Contains("RootPath", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapFileServer_RelativeRootPath_IsRefused()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
                endpoints.MapFileServer("/docs", o => o.RootPath = "relative/path")));

        Assert.Contains("absolute", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MapFileServer_RootThatDoesNotExist_IsRefused()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"bnfs-missing-{Guid.NewGuid():N}");

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
                endpoints.MapFileServer("/docs", o => o.RootPath = missing)));
    }

    [Fact]
    public async Task MapFileServer_BlankPrefix_IsRefused()
    {
        using var root = new TempDirectory();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
                endpoints.MapFileServer("   ", o => o.RootPath = root.Path)));
    }
}
