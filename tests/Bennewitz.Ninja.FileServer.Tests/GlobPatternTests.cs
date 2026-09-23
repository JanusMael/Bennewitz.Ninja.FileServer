using Bennewitz.Ninja.FileServer.Tests.Infrastructure;

namespace Bennewitz.Ninja.FileServer.Tests;

/// <summary>
/// What a configured pattern may be, shared by <see cref="FileServerMountOptions.UnlistedPatterns"/>
/// and <see cref="FileServerMountOptions.ExposedSensitivePatterns"/>: normalised to the
/// root-relative, <c>/</c>-separated form both are matched in, and refused at registration when
/// it reaches outside the mount.
/// </summary>
public sealed class GlobPatternTests
{
    public static TheoryData<string> Options =>
        [nameof(FileServerMountOptions.UnlistedPatterns), nameof(FileServerMountOptions.ExposedSensitivePatterns)];

    [Theory]
    [InlineData("  *.key  ", "*.key")]
    [InlineData("sub\\*.key", "sub/*.key")]
    [InlineData("/secret.txt", "secret.txt")]
    [InlineData("a..b.txt", "a..b.txt")]
    [InlineData("**/.env", "**/.env")]
    public void Normalise_ProducesTheRootRelativeSlashForm(string input, string expected)
    {
        Assert.Equal([expected], GlobPatterns.Normalise([input], "UnlistedPatterns"));
    }

    [Fact]
    public void Normalise_DropsEmptiesAndNeverModifiesTheCallersList()
    {
        var callers = new[] { "sub\\a.txt", "  ", "" };

        var normalised = GlobPatterns.Normalise(callers, "UnlistedPatterns");

        Assert.Equal(["sub/a.txt"], normalised);
        Assert.Equal(new[] { "sub\\a.txt", "  ", "" }, callers);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("sub/../x")]
    [InlineData("..\\x")]
    [InlineData("//server/share/x")]
    [InlineData("\\\\server\\share\\x")]
    public void Normalise_PatternEscapingTheRoot_IsRefusedNamingTheOption(string pattern)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            GlobPatterns.Normalise([pattern], "ExposedSensitivePatterns"));

        Assert.Contains("ExposedSensitivePatterns", error.Message, StringComparison.Ordinal);
    }

    [WindowsFact]
    public void Normalise_DriveQualifiedPattern_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => GlobPatterns.Normalise(["C:\\x"], "UnlistedPatterns"));
        Assert.Throws<ArgumentException>(() => GlobPatterns.Normalise(["C:/x"], "UnlistedPatterns"));
    }

    [Theory]
    [MemberData(nameof(Options))]
    public async Task MapFileServer_PatternEscapingTheRoot_FailsAtStartup(string option)
    {
        using var root = new TempDirectory();

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            FileServerTestHost.StartExpectingFailureAsync(endpoints =>
                endpoints.MapFileServer("/docs", o =>
                {
                    o.RootPath = root.Path;
                    if (option == nameof(FileServerMountOptions.UnlistedPatterns))
                        o.UnlistedPatterns = ["sub/../x"];
                    else
                        o.ExposedSensitivePatterns = ["sub/../x"];
                })));

        Assert.Contains(option, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MapFileServer_DoesNotModifyTheCallersPatternLists()
    {
        using var root = new TempDirectory();

        var unlisted = new[] { "\\a.txt" };
        var exposed = new[] { " .well-known\\** " };

        await using var host = await FileServerTestHost.StartAsync(endpoints =>
            endpoints.MapFileServer("/docs", o =>
            {
                o.RootPath = root.Path;
                o.UnlistedPatterns = unlisted;
                o.ExposedSensitivePatterns = exposed;
            }));

        Assert.Equal(new[] { "\\a.txt" }, unlisted);
        Assert.Equal(new[] { " .well-known\\** " }, exposed);
    }
}
