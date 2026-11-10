using System.Reflection;
using System.Text.RegularExpressions;
using Bennewitz.Ninja.AssemblyQuality;
using Bennewitz.Ninja.AssemblyQuality.Rules;

namespace Bennewitz.Ninja.FileServer.Tests.Architecture;

/// <summary>
/// AssemblyQuality's rules over the library's compiled assembly, read the way it ships: the shape a
/// consumer binds against.
/// </summary>
/// <remarks>
/// Each rule is held to AssemblyQuality's checks: it skipped nothing, it found nothing, and, where
/// the library gives it a candidate, it inspected something. Findings alone cannot fail a rule that
/// never looked. BNAQ1001 counts only public methods that take a token, and the library has none.
/// BNAQ1003, the layering rule, is left out: the CLI is the only other project, and it consumes the
/// library's public API alone, which <c>InternalsVisibleTo</c> and the CLI's own rule already pin.
/// Adapted from <c>templates/bbpkg</c> in Bennewitz.Ninja.Templates.
/// </remarks>
public sealed class AssemblyQualityTests
{
    private static readonly Assembly Shipped = typeof(FileServerMountOptions).Assembly;

    /// <summary>
    /// BNAQ1005: every grant compiled into the library names an assembly the repository's two grant
    /// files allow: the generated list of its own assemblies, and the hand-written External file's
    /// names.
    /// </summary>
    /// <remarks>
    /// Read from the compiled assembly, so a grant from anywhere else (a stray attribute, a project
    /// item, a generator) fails here even when the conventions check cannot see it. BNAQ1005 loads
    /// nothing, so it never fills Skipped, and that assertion is left out.
    /// </remarks>
    [Fact]
    public void BNAQ1005_every_grant_names_an_allowed_assembly()
    {
        string[] allowed =
        [
            .. GrantedNames("AssemblyInfo.InternalsVisibleTo.cs"),
            .. GrantedNames("AssemblyInfo.InternalsVisibleTo.External.cs"),
        ];

        AssemblyRuleResult result = new FriendGrantRule(allowed).Analyze(AssemblyScanContext.Of(Shipped));

        Assert.True(result.Inspected > 0, "BNAQ1005 inspected no grant, so it proved nothing.");
        Assert.True(result.Findings.Count == 0, string.Join("\n", result.Findings));
    }

    /// <summary>BNAQ1001: no public method takes a <c>CancellationToken</c> with a default.</summary>
    [Fact]
    public void BNAQ1001_no_public_method_takes_a_defaulted_cancellation_token()
    {
        AssemblyRuleResult result = new CancellationTokenRule().Analyze(AssemblyScanContext.Of(Shipped));

        Assert.True(result.Skipped.Count == 0, string.Join("\n", result.Skipped));
        Assert.True(result.Findings.Count == 0, string.Join("\n", result.Findings));
    }

    /// <summary>
    /// BNAQ1002: no type from a leak-prone namespace appears in the public surface, where every
    /// consumer would come to depend on it. Markdig and ColorCode are named beside the rule's own
    /// set: the library renders Markdown with them, and a public signature that named one of their
    /// types would bind every consumer to that version of the renderer.
    /// </summary>
    /// <remarks>
    /// The library references both directly, so the rule has slots where a leak is possible and
    /// must inspect them; without the two names it would inspect nothing and prove nothing.
    /// </remarks>
    [Fact]
    public void BNAQ1002_no_leak_prone_type_appears_in_the_public_surface()
    {
        AssemblyRuleResult result = new SurfaceLeakRule(["Markdig", "ColorCode"]).Analyze(AssemblyScanContext.Of(Shipped));

        Assert.True(result.Inspected > 0, "BNAQ1002 inspected nothing, so it proved nothing.");
        Assert.True(result.Skipped.Count == 0, string.Join("\n", result.Skipped));
        Assert.True(result.Findings.Count == 0, string.Join("\n", result.Findings));
    }

    /// <summary>
    /// BNAQ1004: no namespace segment shadows a referenced root. A segment named <c>System</c> inside
    /// this assembly would make <c>System.IO</c> resolve to it in every consumer, failing with CS0234.
    /// </summary>
    [Fact]
    public void BNAQ1004_no_namespace_segment_shadows_a_referenced_root()
    {
        AssemblyRuleResult result = new NamespaceShadowRule().Analyze(AssemblyScanContext.Of(Shipped));

        Assert.True(result.Inspected > 0, "BNAQ1004 inspected nothing, so it proved nothing.");
        Assert.True(result.Skipped.Count == 0, string.Join("\n", result.Skipped));
        Assert.True(result.Findings.Count == 0, string.Join("\n", result.Findings));
    }

    /// <summary>The names a grant file grants to, comment lines left out.</summary>
    private static IEnumerable<string> GrantedNames(string file) =>
        File.ReadLines(Path.Combine(RepoRoot(), file))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(line => Regex.Matches(line, "InternalsVisibleTo\\(\"([^\"]+)\"\\)").Select(m => m.Groups[1].Value));

    /// <summary>
    /// Walks up from the test assembly to the directory holding the solution, so the tests do not
    /// depend on the working directory a runner happens to choose.
    /// </summary>
    private static string RepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !directory.EnumerateFiles().Any(IsSolution))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;

        static bool IsSolution(FileInfo file) =>
            file.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            || file.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
    }
}
