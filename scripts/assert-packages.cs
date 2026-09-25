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
// Usage:  dotnet run scripts/assert-packages.cs -- <packages-directory>

using System.IO.Compression;
using System.Xml.Linq;

string packagesDirectory = args.Length > 0 ? args[0] : Path.Combine("packages", "Release");
string repoRoot = Directory.GetCurrentDirectory();

HashSet<string> declared =
[
    .. ReadList(Path.Combine(repoRoot, "packages.push")),
    .. ReadList(Path.Combine(repoRoot, "packages.local")),
];

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

HashSet<string> packed = [];

foreach (string package in produced)
{
    string? id = IdOf(package);

    if (id is null)
    {
        return Fail($"'{Path.GetFileName(package)}' has no readable .nuspec id.");
    }

    packed.Add(id);
}

string[] unexpected = [.. packed.Except(declared).Order()];
string[] missing = [.. declared.Except(packed).Order()];

if (unexpected.Length > 0)
{
    return Fail(
        "Packed but declared nowhere: " + string.Join(", ", unexpected) +
        ". Add it to packages.push or packages.local before this reaches a push — a published id cannot be withdrawn.");
}

if (missing.Length > 0)
{
    return Fail(
        "Declared but not packed: " + string.Join(", ", missing) +
        ". The push names it explicitly, so the release would fail on a file that does not exist.");
}

Console.WriteLine($"Packages check out: {packed.Count} packed, all declared.");
foreach (string id in packed.Order())
{
    Console.WriteLine($"  {id}");
}

return 0;

static string[] ReadList(string path) =>
    File.Exists(path)
        ? [.. File.ReadLines(path).Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#'))]
        : [];

static string? IdOf(string packagePath)
{
    using ZipArchive archive = ZipFile.OpenRead(packagePath);

    ZipArchiveEntry? nuspec = archive.Entries.FirstOrDefault(entry =>
        entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) &&
        !entry.FullName.Contains('/'));

    if (nuspec is null)
    {
        return null;
    }

    using Stream stream = nuspec.Open();
    XDocument document = XDocument.Load(stream);

    // The nuspec namespace varies by schema version, so match on local name.
    return document.Descendants()
        .FirstOrDefault(element => element.Name.LocalName == "id")
        ?.Value.Trim();
}

static int Fail(string message)
{
    Console.Error.WriteLine("::error::" + message);
    return 1;
}
