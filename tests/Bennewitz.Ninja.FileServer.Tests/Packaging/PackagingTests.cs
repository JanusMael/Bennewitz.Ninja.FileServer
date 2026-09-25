using System.Xml.Linq;

namespace Bennewitz.Ninja.FileServer.Tests.Packaging;

/// <summary>
/// Guards what this repository publishes: that every packable project is deliberately classified,
/// and that no release step globs.
/// </summary>
/// <remarks>
/// <para>
/// ⛔ <b>A published NuGet version can never be replaced</b> — only unlisted. Everything else here
/// is recoverable; this is not. So the guard runs on every push rather than at release time, when
/// finding out is still free.
/// </para>
/// <para>
/// ⚠ <b>The argument this exists to refuse is "it is fine, every packed id is pushable."</b> That
/// is true of today's project list and of no other. <c>Bennewitz.Ninja.DiffView</c> came within one
/// project of publishing a deliberately-private package because a glob swept it up.
/// </para>
/// <para>
/// Adapted from <c>templates/bbpkg</c> in Bennewitz.Ninja.Templates. The one difference that
/// matters is in <see cref="PackableIds"/>: a project counts as packable unless it says otherwise.
/// </para>
/// </remarks>
public sealed class PackagingTests
{
    /// <summary>
    /// Every packable project appears in <c>packages.push</c> or <c>packages.local</c>. Adding one
    /// and classifying it nowhere fails here rather than at the push.
    /// </summary>
    [Fact]
    public void Every_packable_project_is_classified()
    {
        HashSet<string> packable = PackableIds();
        HashSet<string> classified = [.. Ids("packages.push"), .. Ids("packages.local")];

        Assert.NotEmpty(packable);

        string[] unclassified = [.. packable.Except(classified).Order()];
        Assert.True(
            unclassified.Length == 0,
            "Packable but in neither packages.push nor packages.local: " + string.Join(", ", unclassified) +
            ". Classify it — publishing it by accident cannot be undone.");

        string[] phantom = [.. classified.Except(packable).Order()];
        Assert.True(
            phantom.Length == 0,
            "Listed but nothing packs it: " + string.Join(", ", phantom) +
            ". A stale entry means the push names a package that will not exist.");
    }

    /// <summary>
    /// No id is in both lists, which would say "publish this" and "never publish this" at once.
    /// </summary>
    [Fact]
    public void No_id_is_both_published_and_private()
    {
        string[] both = [.. Ids("packages.push").Intersect(Ids("packages.local")).Order()];

        Assert.True(both.Length == 0, "In both packages.push and packages.local: " + string.Join(", ", both));
    }

    /// <summary>
    /// No release step globs, and both publishing steps take their list from
    /// <c>packages.push</c> rather than repeating it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⚠ <b>The ids are deliberately NOT asserted literally here.</b> A workflow that repeats the
    /// list has two homes for it, and the second one silently rots when a package is added — which
    /// is the failure this whole file exists to prevent, reintroduced by the guard meant to stop
    /// it. Driving both steps from the declaration means the list has exactly one home, and
    /// <c>assert-packages</c> closes the loop by checking the packed output against that same file.
    /// </para>
    /// <para>
    /// ⭐ <c>gh release create</c> is covered as well as <c>dotnet nuget push</c>, and it is the one
    /// more likely to rot. Its failure is quieter: a release asset can be deleted, so a glob there
    /// never announces itself the way a permanent nuget.org id does — it just ships something
    /// nobody chose. The binary archives are held to the same rule: each is named per RID.
    /// </para>
    /// </remarks>
    [Fact]
    public void The_release_workflow_globs_nothing_and_publishes_what_is_declared()
    {
        string workflow = ReleaseWorkflow();

        Assert.DoesNotContain("*.nupkg", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("*.zip", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("*.tar.gz", workflow, StringComparison.Ordinal);

        int declarationReads = workflow.Split("packages.push").Length - 1;
        Assert.True(
            declarationReads >= 3,
            "The push, the GitHub release and the preflight summary should each read packages.push; " +
            $"found {declarationReads} reference(s). A step that stops reading it stops publishing what is declared.");
    }

    /// <summary>
    /// Nothing in <c>packages.local</c> is named anywhere in the release workflow — a private
    /// package should have no path to a publishing step at all.
    /// </summary>
    [Fact]
    public void The_release_workflow_never_names_a_private_package()
    {
        string workflow = ReleaseWorkflow();

        foreach (string id in Ids("packages.local"))
        {
            Assert.DoesNotContain(id, workflow, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Both publishing steps still exist. Without this the test above passes vacuously against a
    /// workflow that has stopped publishing at all.
    /// </summary>
    [Fact]
    public void The_release_workflow_still_has_both_publishing_steps()
    {
        string workflow = ReleaseWorkflow();

        Assert.Contains("dotnet nuget push", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release create", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// The preflight exists, so the credentials are exercisable before a tag makes anything
    /// permanent.
    /// </summary>
    [Fact]
    public void The_release_workflow_has_a_credential_preflight()
    {
        string workflow = ReleaseWorkflow();

        Assert.Contains("RELEASING", workflow, StringComparison.Ordinal);
        Assert.Contains("NuGet/login", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Comment lines are dropped before searching, because the comments above the publishing steps
    /// explain the very glob these tests forbid — a whole-file search would fail on the explanation.
    /// </summary>
    private static string ReleaseWorkflow()
    {
        string path = Path.Combine(RepoRoot(), ".github", "workflows", "release.yml");
        Assert.True(File.Exists(path), "There is no release workflow, so nothing publishes — and nothing gates what would.");

        return string.Join('\n', File.ReadLines(path).Where(line => !line.TrimStart().StartsWith('#')));
    }

    /// <summary>
    /// Reads one of the two package lists, dropping blank lines and <c>#</c> comments.
    /// </summary>
    private static HashSet<string> Ids(string fileName)
    {
        string path = Path.Combine(RepoRoot(), fileName);
        Assert.True(File.Exists(path), fileName + " is missing, so nothing declares what may be published.");

        return
        [
            .. File.ReadLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
        ];
    }

    /// <summary>
    /// Package ids of every packable project, read from the project files rather than from packed
    /// output, so the guard runs before anything has been packed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ⛔ <b>A project is packable unless it says <c>IsPackable</c> is <c>false</c>.</b> The SDK's
    /// own default for a class library is <c>true</c>, so a new library that says nothing packs —
    /// and a guard that counted only an explicit <c>true</c> would never see it. Failing closed
    /// means such a project must be classified, or declared not packable, before CI passes.
    /// </para>
    /// <para>
    /// ⚠ <c>PackageId</c> falls back to <c>AssemblyName</c> and then the project file name when it
    /// is not set, which is how a project acquires an id nobody chose. Mirrored here so an
    /// unclassified project cannot hide behind an unset property.
    /// </para>
    /// </remarks>
    private static HashSet<string> PackableIds()
    {
        HashSet<string> ids = [];

        foreach (string project in Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            XDocument document = XDocument.Load(project);

            string? isPackable = document.Descendants("IsPackable").LastOrDefault()?.Value.Trim();
            if (string.Equals(isPackable, "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string? declared = document.Descendants("PackageId").LastOrDefault()?.Value.Trim();
            string? assembly = document.Descendants("AssemblyName").LastOrDefault()?.Value.Trim();

            ids.Add(Pick(declared, assembly, Path.GetFileNameWithoutExtension(project)));
        }

        return ids;

        static string Pick(string? declared, string? assembly, string fallback) =>
            !string.IsNullOrWhiteSpace(declared) ? declared
            : !string.IsNullOrWhiteSpace(assembly) ? assembly
            : fallback;
    }

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
