#!/usr/bin/env dotnet
// Post-pack guard: the packages actually produced must be exactly the ones declared.
//
// PackagingTests checks the same thing from the PROJECT FILES, before anything is packed. This
// checks it from the PACKED OUTPUT, which is the only place a surprise can still appear — an SDK
// default, a transitive target, or a project that packs something its csproj does not obviously
// say it does. Both guards, because they fail on different mistakes.
//
// ⛔ Ids are read from the <id> element inside each .nuspec, NEVER parsed from the filename.
// "<id>.<version>.nupkg" is not decidable lexically: nothing distinguishes an id ending in
// "Widget" from one ending in "Widget.2026". A guard that splits on dots is wrong in exactly the
// cases it exists to catch.
//
// ⭐ A FAMILY'S RELEASE packs only that family's projects, so with --family the declaration is that
// family's entry in .github/repository.json's "families", and nothing else may have been packed: an
// id of another family, or one of packages.local, is as unexpected there as an undeclared one. Each of
// its packages must also carry the family's README, docs/<family>.md, byte for byte, as the README its
// .nuspec names: what nuget.org will show, read from what pack produced.
//
// ⭐ EVERY PUBLISHED README IS A NUGET.ORG PAGE (plans/00012). The README each package of
// packages.push carries, whatever its file name and with or without --family, may not link
// relatively: on nuget.org a relative link resolves against nuget.org and goes nowhere. A package of
// packages.local never reaches nuget.org, so its README is not read. Links is a copy of
// repo-conventions.cs's, held identical by the template's tests: each script is copied into a
// repository on its own, so neither can reference the other.
//
// Usage:  dotnet run scripts/assert-packages.cs -- <packages-directory> [--family <family>]

using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

string? packagesDirectory = null;
string? family = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--family" && i + 1 < args.Length)
    {
        family = args[++i].Trim().ToLowerInvariant();
    }
    else if (packagesDirectory is null && !args[i].StartsWith("--", StringComparison.Ordinal))
    {
        packagesDirectory = args[i];
    }
    else
    {
        return Fail($"Unknown or incomplete argument '{args[i]}'. Usage: assert-packages <packages-directory> [--family <family>]");
    }
}
packagesDirectory ??= Path.Combine("packages", "Release");
string repoRoot = Directory.GetCurrentDirectory();

HashSet<string> declared;
string declaredWhere;
if (family is null)
{
    declared =
    [
        .. ReadList(Path.Combine(repoRoot, "packages.push")),
        .. ReadList(Path.Combine(repoRoot, "packages.local")),
    ];
    declaredWhere = "packages.push or packages.local";
}
else
{
    string[]? ids = FamilyIds(Path.Combine(repoRoot, ".github", "repository.json"), family, out string? problem);
    if (ids is null)
    {
        return Fail(problem!);
    }
    declared = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);
    declaredWhere = $"the {family} family in .github/repository.json";
}

if (!Directory.Exists(packagesDirectory))
{
    return Fail($"No packages directory at '{packagesDirectory}'. Nothing was packed, so nothing can be checked.");
}

// .snupkg is a symbol package alongside its .nupkg, not a package of its own.
string[] produced = [.. Directory
    .EnumerateFiles(packagesDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
    .Where(path => !path.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase))];

if (produced.Length == 0)
{
    return Fail($"No .nupkg files in '{packagesDirectory}'. A release that publishes nothing is a failure, not a no-op.");
}

HashSet<string> packed = new(declared.Comparer);
List<(string Id, string? Name, byte[]? Readme)> readmes = [];

foreach (string package in produced)
{
    (string? id, string? name, byte[]? readme) = Read(package);

    if (id is null)
    {
        return Fail($"'{Path.GetFileName(package)}' has no readable .nuspec id.");
    }

    packed.Add(id);
    readmes.Add((id, name, readme));
}

string[] unexpected = [.. packed.Except(declared, declared.Comparer).Order()];
string[] missing = [.. declared.Except(packed, declared.Comparer).Order()];

if (unexpected.Length > 0)
{
    return Fail(family is null
        ? "Packed but declared nowhere: " + string.Join(", ", unexpected) +
          ". Add it to packages.push or packages.local before this reaches a push — a published id cannot be withdrawn."
        : $"Packed but not in {declaredWhere}: " + string.Join(", ", unexpected) +
          ". A family's release packs only that family's projects.");
}

if (missing.Length > 0)
{
    return Fail(
        $"Declared in {declaredWhere} but not packed: " + string.Join(", ", missing) +
        ". The push names it explicitly, so the release would fail on a file that does not exist.");
}

if (family is not null)
{
    string readmePath = Path.Combine(repoRoot, "docs", family + ".md");
    if (!File.Exists(readmePath))
    {
        return Fail($"docs/{family}.md is missing: it is the {family} family's README, which each of its packages carries.");
    }

    byte[] expected = File.ReadAllBytes(readmePath);
    string[] wrong = [.. readmes
        .Where(r => r.Readme is null || !r.Readme.AsSpan().SequenceEqual(expected))
        .OrderBy(r => r.Id, StringComparer.Ordinal)
        .Select(r => r.Readme is null ? $"{r.Id} packs none" : $"{r.Id}'s is another file")];
    if (wrong.Length > 0)
    {
        return Fail(
            $"The {family} family's packages carry docs/{family}.md as their README.md, byte for byte: " + string.Join("; ", wrong) +
            $". Set FamilyReadme to docs/{family}.md in each one's project.");
    }
}

HashSet<string> pushed = new(ReadList(Path.Combine(repoRoot, "packages.push")), declared.Comparer);
string[] relative = [.. readmes
    .Where(r => r.Readme is not null && pushed.Contains(r.Id))
    .OrderBy(r => r.Id, StringComparer.Ordinal)
    .SelectMany(r => Links.Relative(Text(r.Readme!)).Select(link =>
        $"{r.Id}'s README, {r.Name}:{link.Line}, links to \"{link.Target}\", relatively. It is the package's page on nuget.org, where a relative link goes nowhere: link absolutely, or to an #anchor on the page."))];
if (relative.Length > 0)
{
    foreach (string message in relative)
    {
        Fail(message);
    }
    return 1;
}

Console.WriteLine($"Packages check out: {packed.Count} packed, all declared in {declaredWhere}.");
foreach (string id in packed.Order())
{
    Console.WriteLine($"  {id}");
}
if (family is not null)
{
    Console.WriteLine($"Each carries docs/{family}.md as its README.md.");
}
Console.WriteLine("Each README of packages.push links only absolutely.");

return 0;

static string[] ReadList(string path) =>
    File.Exists(path)
        ? [.. File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))]
        : [];

// The family's ids, matching its stem without regard to case, as the release resolves it.
static string[]? FamilyIds(string path, string family, out string? problem)
{
    problem = null;
    JsonObject? families;
    try
    {
        families = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?["families"] as JsonObject : null;
    }
    catch (JsonException error)
    {
        problem = ".github/repository.json is not valid JSON: " + error.Message;
        return null;
    }
    if (families is null)
    {
        problem = $"--family {family}, but .github/repository.json declares no families.";
        return null;
    }

    foreach ((string stem, JsonNode? ids) in families)
    {
        if (stem.Equals(family, StringComparison.OrdinalIgnoreCase))
        {
            string[] list = ids is JsonArray array
                ? [.. array.Select(n => n is JsonValue value && value.TryGetValue(out string? id) ? id.Trim() : "").Where(id => id.Length > 0)]
                : [];
            if (list.Length == 0)
            {
                problem = $"The {family} family in .github/repository.json lists no ids.";
                return null;
            }
            return list;
        }
    }

    problem = $"--family {family}, which .github/repository.json does not declare; it declares {string.Join(", ", families.Select(f => f.Key))}.";
    return null;
}

// The package's id, and the name and bytes of the README its .nuspec names: null when it names none,
// or the file it names is not in the package.
static (string? Id, string? Name, byte[]? Readme) Read(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);

    ZipArchiveEntry? nuspec = archive.Entries.FirstOrDefault(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
        !entry.FullName.Contains('/'));

    if (nuspec is null)
    {
        return (null, null, null);
    }

    XDocument document;
    using (Stream stream = nuspec.Open())
    {
        document = XDocument.Load(stream);
    }

    // The nuspec namespace varies by schema version, so match on local name.
    string? id = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "id")?.Value.Trim();
    string? readme = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "readme")?.Value.Trim().Replace('\\', '/');
    ZipArchiveEntry? entry = readme is { Length: > 0 } ? archive.GetEntry(readme) : null;
    if (entry is null)
    {
        return (id, null, null);
    }

    using Stream content = entry.Open();
    using MemoryStream bytes = new();
    content.CopyTo(bytes);
    return (id, readme, bytes.ToArray());
}

// The README as text, without the byte order mark a fence on its first line would otherwise follow.
static string Text(byte[] bytes)
{
    using StreamReader reader = new(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
    return reader.ReadToEnd();
}

static int Fail(string message)
{
    Console.Error.WriteLine("::error::" + message);
    return 1;
}

/// <summary>
/// The relative links in a markdown document, each with its line: inline links and images,
/// reference definitions (<c>[x]: target</c>), and HTML <c>href</c> and <c>src</c> attributes.
/// </summary>
/// <remarks>
/// ⛔ Not inside fenced code or an inline code span: a link shown there as an example is text. A
/// target with a scheme (<c>https:</c>, <c>mailto:</c>), one starting <c>//</c>, and an anchor within
/// the page (<c>#…</c>) are absolute.
/// </remarks>
static class Links
{
    private static readonly Regex Fence = new(@"^ {0,3}(`{3,}|~{3,})");
    private static readonly Regex CodeSpan = new(@"(`+).+?\1");
    private static readonly Regex Inline = new(@"\]\(\s*(<[^>]*>|[^\s)]+)");
    private static readonly Regex Definition = new(@"^ {0,3}\[[^\]]+\]:\s*(<[^>]*>|\S+)");
    private static readonly Regex Attribute = new(@"\b(?:href|src)\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'>]+))", RegexOptions.IgnoreCase);
    private static readonly Regex Absolute = new(@"^(?:[A-Za-z][A-Za-z0-9+.-]*:|//|#)");

    public static IEnumerable<(int Line, string Target)> Relative(string markdown)
    {
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        string? fence = null;
        for (int i = 0; i < lines.Length; i++)
        {
            Match opening = Fence.Match(lines[i]);
            if (fence is not null)
            {
                // A fence closes on a line of the same character, at least as long, and nothing else.
                if (opening.Success && opening.Groups[1].Value[0] == fence[0] && opening.Groups[1].Length >= fence.Length
                    && lines[i].Trim().Trim(fence[0]).Length == 0)
                {
                    fence = null;
                }
                continue;
            }
            if (opening.Success)
            {
                fence = opening.Groups[1].Value;
                continue;
            }

            string text = CodeSpan.Replace(lines[i], "");
            IEnumerable<string> targets = Inline.Matches(text).Select(m => m.Groups[1].Value)
                .Concat(Definition.Matches(text).Select(m => m.Groups[1].Value))
                .Concat(Attribute.Matches(text).Select(m => m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value));
            foreach (string target in targets.Select(t => t.Trim('<', '>').Trim()))
            {
                if (target.Length > 0 && !Absolute.IsMatch(target))
                {
                    yield return (i + 1, target);
                }
            }
        }
    }
}
