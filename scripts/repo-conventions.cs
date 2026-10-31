#!/usr/bin/env dotnet
// Checks, and applies, the conventions every Bennewitz.Ninja repository carries: its GitHub
// settings, the rulesets that gate `main` and the release tags, and the documentation at its root
// and in every top-level directory. The convention itself is prescribed in the template
// repository's docs/repository-conventions.md; this script is how it is enforced.
//
// ⭐ WHY A SCRIPT AND NOT THE TEMPLATE. GitHub keeps a repository's settings outside git, so neither
// "Use this template" nor `dotnet new` can carry them, and a workflow's GITHUB_TOKEN cannot write
// them: PATCH /repos needs the Administration permission, which GITHUB_TOKEN cannot be granted. What
// the token CAN do is read part of them, so CI checks that part and the maintainer applies the rest.
//
// ⭐ TWO SOURCES, NEITHER OPTIONAL.
//   The FAMILY BASELINE is in this file (Baseline, below): merge options, features, security toggles
//   and the shape of both rulesets. It is identical in every repository because this file is, and
//   `check` run from the template repository reports any copy that differs from the template's.
//   What VARIES is in .github/repository.json: description, homepage, topics, the CI jobs `main`
//   requires, the paths that are shipped content, and the top-level directories exempt from
//   documentation, each with its reason.
//
// ⛔ UNREADABLE IS NOT PASSING. A token without admin rights does not see the merge options or the
// security settings. `check` without --admin says which items were out of its reach; `check --admin`
// FAILS on any item it could not read, because a skipped item and a correct one look identical.
//
// Usage:
//   dotnet run --file scripts/repo-conventions.cs -- check                  what CI runs
//   dotnet run --file scripts/repo-conventions.cs -- check --release        release preflight: only what is prescribed
//   dotnet run --file scripts/repo-conventions.cs -- check --admin          everything, with the maintainer's gh login
//   dotnet run --file scripts/repo-conventions.cs -- apply [--dry-run]      write the settings and rulesets
//   dotnet run --file scripts/repo-conventions.cs -- grants                 write the solution's friend grants
//
//   --repo OWNER/NAME   act on another repository; its files are read through the API
//   --root DIR          read the files from DIR instead of the current directory
//   --offline           check: only what the checkout shows (documents, required checks, build
//                       properties, drift); nothing is asked of GitHub
//   --api-fixtures DIR  answer API reads from DIR/<path>.json instead of calling gh (tests)
//   --replace-branch-protection   apply: delete classic branch protection once the ruleset exists
//
// Exit codes: 0 conforms, 1 findings, 2 usage or an error that stopped the run.

using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string? verb = args.Length > 0 ? args[0] : null;
if (verb is not ("check" or "apply" or "grants"))
{
    return Usage("the first argument must be 'check', 'apply' or 'grants'.");
}

string? repoName = null;
string? root = null;
string? fixtures = null;
bool admin = false, release = false, dryRun = false, replaceProtection = false, offline = false;

for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--repo" when i + 1 < args.Length: repoName = args[++i]; break;
        case "--root" when i + 1 < args.Length: root = args[++i]; break;
        case "--api-fixtures" when i + 1 < args.Length: fixtures = args[++i]; break;
        case "--admin": admin = true; break;
        case "--release": release = true; break;
        case "--dry-run": dryRun = true; break;
        case "--replace-branch-protection": replaceProtection = true; break;
        case "--offline": offline = true; break;
        default: return Usage($"unknown or incomplete argument '{args[i]}'.");
    }
}

if (verb == "apply" && fixtures is not null && !dryRun)
{
    return Usage("--api-fixtures cannot write; pass --dry-run.");
}

if (offline && (verb == "apply" || (repoName is not null && root is null)))
{
    return Usage("--offline checks a checkout; it cannot apply, and it cannot read another repository through the API.");
}

if (verb == "grants" && repoName is not null && root is null)
{
    return Usage("grants writes into a checkout; it cannot write another repository through the API.");
}

// `grants` works on the checkout alone, as --offline does: it asks nothing of GitHub.
bool local = offline || verb == "grants";
bool remoteTree = repoName is not null && root is null;
string localRoot = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
repoName ??= Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } fromCi
    ? fromCi
    : local ? null : Gh.RepoOf(localRoot);

if (repoName is null && !local)
{
    return Usage("could not tell which repository this is; pass --repo OWNER/NAME.");
}

Api api = new(repoName ?? "", fixtures);
(int repoStatus, string repoBody) = local ? (0, "") : api.Get("");
JsonObject? live = repoStatus == 200 ? JsonNode.Parse(repoBody)?.AsObject() : null;
string branch = live?["default_branch"]?.GetValue<string>() ?? "main";

Tree tree;
try
{
    tree = remoteTree ? Tree.Remote(api, branch) : Tree.Local(localRoot);
}
catch (InvalidOperationException error)
{
    Console.Error.WriteLine("repo-conventions: " + error.Message);
    return 2;
}

Config? config = null;
List<Finding> findings = [];
string? configText = tree.Read(".github/repository.json");

if (configText is null)
{
    findings.Add(Finding.Fail("repository.json", ".github/repository.json is missing. It records the description, topics and required checks; see docs/repository-conventions.md in Bennewitz.Ninja.Templates."));
}
else
{
    try
    {
        config = Config.Parse(configText, Baseline.Pushed(tree), findings);
    }
    catch (JsonException error)
    {
        findings.Add(Finding.Fail("repository.json", ".github/repository.json is not valid JSON: " + error.Message));
    }
}

return verb switch
{
    "apply" => Apply(),
    "grants" => WriteGrants(),
    _ => Check(),
};

int Check()
{
    Documentation.Check(tree, config, findings);
    Families.CheckReadmes(tree, config, findings);
    Workflows.CheckPublishing(tree, findings);

    if (offline)
    {
        if (config is not null)
        {
            Workflows.CheckRequired(tree, config.RequiredChecks, findings);
        }
        CheckProjects(props: true);
        Drift.Check(tree, remoteTree, findings);
        return Report();
    }

    if (live is null)
    {
        findings.Add(Finding.Fail("repository", $"GET repos/{repoName} failed, so nothing on GitHub could be checked."));
        return Report();
    }

    string description = live["description"]?.GetValue<string>() ?? "";
    if (description.Trim().Length == 0)
    {
        findings.Add(Finding.Fail("description", "The GitHub description is empty. Set it in .github/repository.json and run `apply`."));
    }

    if (release)
    {
        // The release preflight checks only what is prescribed, and the friend grants are: a release
        // must not ship a grant list that differs from its projects.
        CheckProjects(props: false);
        return Report();
    }

    string[] liveTopics = [.. (live["topics"]?.AsArray() ?? []).Select(t => t!.GetValue<string>()).Order(StringComparer.Ordinal)];
    foreach (string missing in Baseline.TopicsFor(Baseline.Publishes(tree)).Except(liveTopics))
    {
        findings.Add(Finding.Fail("topics", $"GitHub's topics lack \"{missing}\", {Baseline.WhyTopic(missing)}."));
    }

    if (config is not null)
    {
        if (description != config.Description)
        {
            findings.Add(Finding.Fail("description", $"GitHub says \"{description}\"; repository.json says \"{config.Description}\". Run `apply`."));
        }

        string homepage = live["homepage"]?.GetValue<string>() ?? "";
        if (homepage != config.Homepage)
        {
            findings.Add(Finding.Fail("homepage", $"GitHub says \"{homepage}\"; repository.json says \"{config.Homepage}\". Run `apply`."));
        }

        string[] wanted = [.. config.Topics.Order(StringComparer.Ordinal)];
        if (!liveTopics.SequenceEqual(wanted))
        {
            findings.Add(Finding.Fail("topics", $"GitHub has [{string.Join(", ", liveTopics)}]; repository.json has [{string.Join(", ", wanted)}]. Run `apply`."));
        }

        Workflows.CheckRequired(tree, config.RequiredChecks, findings);
    }

    Rulesets.Check(api, config, admin, findings);

    Settings.Check(api, live, branch, admin, findings);
    CheckProjects(props: true);
    Drift.Check(tree, remoteTree, findings);
    return Report();
}

// One restore and one evaluation of every project, shared by the property rules and the friend grants.
void CheckProjects(bool props)
{
    IReadOnlyList<Evaluated>? projects = Projects.Evaluate(localRoot, tree, config, remoteTree, findings);
    if (projects is null)
    {
        return;
    }
    if (props)
    {
        Props.Check(projects, config, repoName, findings);
    }
    Families.CheckReferences(localRoot, projects, config, findings);
    Families.CheckPackages(projects, config, findings);
    Grants.Check(localRoot, tree, projects, findings);
}

// Writes AssemblyInfo.InternalsVisibleTo.cs from the projects' assembly names, and an empty External
// file when there is none. An existing External file is never touched.
int WriteGrants()
{
    int before = findings.Count(f => f.Kind == "FAIL");
    IReadOnlyList<Evaluated>? projects = Projects.Evaluate(localRoot, tree, config, remoteTree, findings);
    if (projects is null || findings.Count(f => f.Kind == "FAIL") > before)
    {
        Console.Error.WriteLine("repo-conventions: a project could not be evaluated, so no grant list was written; it would be incomplete.");
        return Report();
    }

    string[] assemblies = Grants.Assemblies(projects);
    File.WriteAllText(Path.Combine(localRoot, Grants.File), Grants.Generate(assemblies));
    Console.WriteLine($"wrote {Grants.File}: {assemblies.Length} assemblies, {string.Join(", ", assemblies)}.");

    string external = Path.Combine(localRoot, Grants.External);
    if (!File.Exists(external))
    {
        File.WriteAllText(external, Grants.EmptyExternal);
        Console.WriteLine($"wrote {Grants.External}, with no grants: add grants to other repositories there by hand.");
    }

    return Report();
}

int Apply()
{
    if (config is null)
    {
        return Report();
    }

    if (live is null)
    {
        findings.Add(Finding.Fail("repository", $"GET repos/{repoName} failed, so there is nothing to apply to."));
        return Report();
    }

    // A required check that no job reports blocks every pull request forever, and GitHub never says
    // why. Refuse to write one.
    int before = findings.Count;
    Workflows.CheckRequired(tree, config.RequiredChecks, findings);
    if (findings.Count > before)
    {
        return Report();
    }

    JsonObject patch = new()
    {
        ["description"] = config.Description,
        ["homepage"] = config.Homepage,
    };
    foreach ((string name, bool value) in Baseline.Settings)
    {
        patch[name] = value;
    }

    List<(string Method, string Path, JsonNode? Body)> requests =
    [
        ("PATCH", "", patch),
        ("PUT", "topics", new JsonObject { ["names"] = Json.Array(config.Topics) }),
        ("PUT", "vulnerability-alerts", null),
        ("PUT", "automated-security-fixes", null),
    ];

    Dictionary<string, long> existing = Rulesets.Existing(api);
    foreach (JsonObject ruleset in Baseline.Rulesets(config.RequiredChecks, config.Families is not null))
    {
        string name = ruleset["name"]!.GetValue<string>();
        requests.Add(existing.TryGetValue(name, out long id)
            ? ("PUT", $"rulesets/{id}", ruleset)
            : ("POST", "rulesets", ruleset));
    }

    bool classic = api.Get($"branches/{branch}/protection").Status == 200;
    if (classic && replaceProtection)
    {
        requests.Add(("DELETE", $"branches/{branch}/protection", null));
    }
    else if (classic)
    {
        Console.WriteLine($"note: {branch} still has classic branch protection. Once the `main` ruleset is confirmed, rerun with --replace-branch-protection so two mechanisms do not gate one branch.");
    }

    foreach ((string method, string path, JsonNode? body) in requests)
    {
        string target = $"repos/{repoName}" + (path.Length > 0 ? "/" + path : "");
        if (dryRun)
        {
            Console.WriteLine($"{method} {target}" + (body is null ? "" : " " + body.ToJsonString()));
            continue;
        }

        (bool ok, string output) = api.Send(method, path, body?.ToJsonString());
        Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {method} {target}");
        if (!ok)
        {
            findings.Add(Finding.Fail("apply", $"{method} {target} failed: {output.Trim()}"));
        }
    }

    return Report();
}

int Report()
{
    bool actions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
    int failures = 0;

    foreach (Finding finding in findings)
    {
        bool fails = finding.Kind == "FAIL" || (finding.Kind == "UNVERIFIED" && admin);
        failures += fails ? 1 : 0;
        string prefix = fails && actions ? "::error::" : "";
        Console.WriteLine($"{prefix}{finding.Kind} {finding.Area}: {finding.Message}");
    }

    string depth = verb == "grants" ? "local" : offline ? "offline" : release ? "release" : admin ? "admin" : "token";
    string subject = repoName ?? localRoot;
    Console.WriteLine(failures == 0
        ? $"repo-conventions: {subject} conforms ({verb}, {depth} depth)."
        : $"repo-conventions: {subject} has {failures} finding(s) ({verb}, {depth} depth).");
    return failures == 0 ? 0 : 1;
}

static int Usage(string problem)
{
    Console.Error.WriteLine("repo-conventions: " + problem);
    Console.Error.WriteLine("usage: repo-conventions (check [--admin | --release | --offline] | apply [--dry-run] [--replace-branch-protection] | grants) [--repo OWNER/NAME] [--root DIR] [--api-fixtures DIR]");
    return 2;
}

/// <summary>The family baseline. Changing a value here changes it for every repository.</summary>
static class Baseline
{
    public static readonly (string Name, bool Value)[] Settings =
    [
        ("allow_merge_commit", false),
        ("allow_squash_merge", true),
        ("allow_rebase_merge", true),
        ("allow_auto_merge", false),
        ("delete_branch_on_merge", true),
        ("allow_update_branch", true),
        ("has_issues", true),
        ("has_wiki", false),
        ("has_projects", false),
        ("has_discussions", false),
    ];

    /// <summary>
    /// The topics every family repository carries, and <c>nuget</c> as well when it publishes to
    /// nuget.org. An app, a site or an API that packs nothing is not a NuGet package, and a topic
    /// saying otherwise sends people looking for one.
    /// </summary>
    public static string[] TopicsFor(bool publishes) =>
        publishes ? ["csharp", "dotnet", "nuget"] : ["csharp", "dotnet"];

    public static string WhyTopic(string topic) =>
        topic == "nuget"
            ? "which every repository that publishes to nuget.org carries: packages.push names an id"
            : "which every family repository carries";

    /// <summary>Whether <c>packages.push</c> names at least one id.</summary>
    public static bool Publishes(Tree tree) => Pushed(tree).Count > 0;

    /// <summary>The ids <c>packages.push</c> names, blank lines and comments aside.</summary>
    public static IReadOnlyList<string> Pushed(Tree tree) =>
        [.. (tree.Read("packages.push") ?? "")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))];

    /// <summary>GitHub's built-in repository-admin role.</summary>
    public const int AdminRoleId = 5;

    /// <param name="families">
    /// Whether repository.json declares families (plans/00009). Their tags, <c>&lt;family&gt;/v…</c>, are
    /// protected as well; only then, because the include list is compared exactly, and a second
    /// pattern in every repository would fail each single-family one on its next sync.
    /// </param>
    public static IEnumerable<JsonObject> Rulesets(IReadOnlyList<string> requiredChecks, bool families)
    {
        // ⛔ Built with the constructor, never a collection expression or Add(JsonObject): both bind
        // to the generic Add<T>, which is not trim-safe, and a file-based app is compiled trimmed.
        JsonArray mainRules = new(
            Rule("deletion"),
            Rule("non_fast_forward"),
            new JsonObject
            {
                ["type"] = "pull_request",
                ["parameters"] = new JsonObject
                {
                    ["required_approving_review_count"] = 0,
                    ["dismiss_stale_reviews_on_push"] = false,
                    ["require_code_owner_review"] = false,
                    ["require_last_push_approval"] = false,
                    ["required_review_thread_resolution"] = true,
                    ["allowed_merge_methods"] = Json.Array(["squash", "rebase"]),
                },
            });

        if (requiredChecks.Count > 0)
        {
            mainRules.Add((JsonNode)new JsonObject
            {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject
                {
                    ["strict_required_status_checks_policy"] = true,
                    ["do_not_enforce_on_create"] = false,
                    ["required_status_checks"] = new JsonArray([.. requiredChecks.Select(c => (JsonNode)new JsonObject { ["context"] = c })]),
                },
            });
        }

        yield return Ruleset("main", "branch", ["~DEFAULT_BRANCH"], mainRules);
        // ⚠ A ruleset's `*` stops at a slash, so `refs/tags/v*` does not reach a family's tag.
        yield return Ruleset("release-tags", "tag", families ? ["refs/tags/v*", "refs/tags/*/v*"] : ["refs/tags/v*"],
            new JsonArray(Rule("creation"), Rule("update"), Rule("deletion")));
    }

    private static JsonObject Ruleset(string name, string target, string[] include, JsonArray rules) => new()
    {
        ["name"] = name,
        ["target"] = target,
        ["enforcement"] = "active",
        // ⛔ `always`, not `pull_request`: the latter lets the admin bypass only inside a pull
        // request, which blocks the maintainer's direct pushes.
        ["bypass_actors"] = new JsonArray(new JsonObject
        {
            ["actor_id"] = AdminRoleId,
            ["actor_type"] = "RepositoryRole",
            ["bypass_mode"] = "always",
        }),
        ["conditions"] = new JsonObject
        {
            ["ref_name"] = new JsonObject { ["include"] = Json.Array(include), ["exclude"] = new JsonArray() },
        },
        ["rules"] = rules,
    };

    private static JsonObject Rule(string type) => new() { ["type"] = type };
}

/// <summary>What .github/repository.json records: only what varies from one repository to the next.</summary>
sealed record Config(
    string Description,
    string Homepage,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> RequiredChecks,
    IReadOnlyList<string> Content,
    IReadOnlyDictionary<string, string> Undocumented,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PropsExempt,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Families)
{
    // A family's stem becomes part of its tags, so it is what a ref name may hold, lower-cased.
    private static readonly Regex FamilyStem = new(@"^[a-z0-9]+(?:[._-][a-z0-9]+)*$");

    public static Config Parse(string text, IReadOnlyList<string> pushed, List<Finding> findings)
    {
        bool publishes = pushed.Count > 0;
        JsonObject json = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("the document is empty.");

        string description = json["description"]?.GetValue<string>() ?? "";
        if (description.Trim().Length == 0)
        {
            findings.Add(Finding.Fail("repository.json", "\"description\" is empty. One sentence saying what the repository is and what it ships."));
        }

        string[] topics = Strings(json["topics"]);
        foreach (string missing in Baseline.TopicsFor(publishes).Except(topics))
        {
            findings.Add(Finding.Fail("repository.json", $"\"topics\" lacks \"{missing}\", {Baseline.WhyTopic(missing)}."));
        }

        Dictionary<string, string> undocumented = new(StringComparer.Ordinal);
        foreach ((string directory, JsonNode? reason) in json["undocumented"]?.AsObject() ?? [])
        {
            string why = reason?.GetValue<string>() ?? "";
            if (why.Trim().Length == 0)
            {
                findings.Add(Finding.Fail("repository.json", $"\"undocumented\" exempts \"{directory}\" without a reason."));
            }
            undocumented[directory.TrimEnd('/')] = why;
        }

        // "props": { "<project>": { "<rule>": "<reason>" } }, as "undocumented" exempts a directory.
        Dictionary<string, IReadOnlyDictionary<string, string>> propsExempt = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string project, JsonNode? rules) in json["props"]?.AsObject() ?? [])
        {
            Dictionary<string, string> exempt = new(StringComparer.Ordinal);
            foreach ((string rule, JsonNode? reason) in rules?.AsObject() ?? [])
            {
                string why = reason?.GetValue<string>() ?? "";
                if (why.Trim().Length == 0)
                {
                    findings.Add(Finding.Fail("repository.json", $"\"props\" exempts {project}'s {rule} without a reason."));
                }
                exempt[rule] = why;
            }
            propsExempt[project] = exempt;
        }

        // Retired by plans/00008: a repository trims when its projects say so, and two sources of
        // truth could disagree. A NOTE, so a repository that still carries it does not go red.
        if (json.ContainsKey("trimming"))
        {
            findings.Add(Finding.Note("repository.json", "\"trimming\" is no longer read; remove it. A repository trims when one of its projects sets a trimming property."));
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>>? families = json["families"] is { } declared ? ParseFamilies(declared, pushed, findings) : null;
        CheckBareTags(json["bareTags"], families, findings);

        return new Config(
            description,
            json["homepage"]?.GetValue<string>() ?? "",
            topics,
            Strings(json["requiredChecks"]),
            [.. Strings(json["content"]).Select(p => p.TrimEnd('/') + "/")],
            undocumented,
            propsExempt,
            families);
    }

    /// <summary>
    /// <c>"bareTags": "&lt;stem&gt;"</c> (plans/00011 decision 7): the family whose releases a grown
    /// repository's bare <c>v…</c> tags were, before it had families. The release counts them as that
    /// family's when it finds the previous tag its notes start from, so it must name a declared family.
    /// </summary>
    private static void CheckBareTags(JsonNode? node, IReadOnlyDictionary<string, IReadOnlyList<string>>? families, List<Finding> findings)
    {
        if (node is null)
        {
            return;
        }
        if (families is null)
        {
            findings.Add(Finding.Fail("families", "\"bareTags\" names the family whose releases the bare v… tags were, but there are no families: every release is a bare tag's. Remove it."));
            return;
        }
        string stem = node is JsonValue value && value.TryGetValue(out string? text) ? text.Trim().ToLowerInvariant() : "";
        if (!families.ContainsKey(stem))
        {
            findings.Add(Finding.Fail("families", $"\"bareTags\" is {node.ToJsonString()}, which is not a declared family; the families are {string.Join(", ", families.Keys)}."));
        }
    }

    /// <summary>
    /// <c>"families": { "&lt;stem&gt;": ["&lt;id&gt;", …] }</c> (plans/00009): which of the ids
    /// <c>packages.push</c> names release together, each family from its own tags. Keyed by the
    /// lower-cased stem, as its tags spell it.
    /// </summary>
    /// <remarks>
    /// ⛔ The families' ids are exactly <c>packages.push</c>'s. One that no family names would never be
    /// released, and one that <c>packages.push</c> does not name would be released but never checked.
    /// </remarks>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseFamilies(JsonNode declared, IReadOnlyList<string> pushed, List<Finding> findings)
    {
        Dictionary<string, IReadOnlyList<string>> families = new(StringComparer.Ordinal);
        if (declared is not JsonObject entries || entries.Count == 0)
        {
            findings.Add(Finding.Fail("families", "\"families\" must map each family's stem to its package ids, or be left out."));
            return families;
        }

        foreach ((string stem, JsonNode? ids) in entries)
        {
            string key = stem.ToLowerInvariant();
            string[] list = Strings(ids is JsonArray ? ids : null);
            if (!FamilyStem.IsMatch(key))
            {
                findings.Add(Finding.Fail("families", $"The family \"{stem}\" cannot be part of a tag: letters and digits, separated by single '.', '-' or '_'."));
            }
            else if (families.ContainsKey(key))
            {
                findings.Add(Finding.Fail("families", $"The family \"{stem}\" is declared twice; its tags are lower-case, so case does not tell two apart."));
            }
            else if (list.Length == 0)
            {
                findings.Add(Finding.Fail("families", $"The family \"{stem}\" lists no package ids."));
            }
            else
            {
                families[key] = list;
            }
        }

        string[] named = [.. families.Values.SelectMany(ids => ids)];
        foreach (IGrouping<string, string> twice in named.GroupBy(id => id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            findings.Add(Finding.Fail("families", $"{twice.Key} is in more than one family. Each id releases with one."));
        }
        string[] unreleased = [.. pushed.Except(named, StringComparer.OrdinalIgnoreCase)];
        if (unreleased.Length > 0)
        {
            findings.Add(Finding.Fail("families", $"packages.push names {string.Join(", ", unreleased)}, which no family does, so no release would publish it. Add each to its family."));
        }
        string[] undeclared = [.. named.Except(pushed, StringComparer.OrdinalIgnoreCase)];
        if (undeclared.Length > 0)
        {
            findings.Add(Finding.Fail("families", $"The families name {string.Join(", ", undeclared)}, which packages.push does not. packages.push says what publishes; a family only says what releases with what."));
        }
        return families;
    }

    private static string[] Strings(JsonNode? node) =>
        [.. (node?.AsArray() ?? []).Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0)];
}

static class Documentation
{
    public const string Marker = "<!-- bbpkg:";
    private const string Pointer = "@AGENTS.md";

    public static void Check(Tree tree, Config? config, List<Finding> findings)
    {
        Require(tree, "README.md", "the root README, for humans", findings);
        Require(tree, "PROGRESS.md", "the work state, updated in the same change as the work", findings);
        Require(tree, "AGENTS.md", "the root AI-facing document", findings);
        RequirePointer(tree, "CLAUDE.md", findings);

        string? copilot = tree.Read(".github/copilot-instructions.md");
        if (copilot is null || !copilot.Contains("AGENTS.md", StringComparison.Ordinal))
        {
            findings.Add(Finding.Fail("docs", ".github/copilot-instructions.md must exist and point at the root AGENTS.md."));
        }

        IReadOnlyDictionary<string, string> exempt = config?.Undocumented ?? new Dictionary<string, string>();
        foreach (string directory in tree.TopLevelDirectories())
        {
            if (exempt.ContainsKey(directory))
            {
                continue;
            }
            Require(tree, directory + "/AGENTS.md", $"the AI-facing document for {directory}/", findings);
            RequirePointer(tree, directory + "/CLAUDE.md", findings);
        }

        IReadOnlyList<string> content = config?.Content ?? [];
        foreach (string path in tree.Files.Where(p => p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            if (content.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            // A marker is a line of its own, which is how the template writes every one. Matching
            // anywhere in a line would also match prose that explains the marker syntax, such as the
            // conventions document itself.
            string[] lines = (tree.Read(path) ?? "").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith(Marker, StringComparison.Ordinal))
                {
                    findings.Add(Finding.Fail("docs", $"{path}:{i + 1} still carries a template marker: {lines[i].Trim()}"));
                }
            }
        }
    }

    private static void Require(Tree tree, string path, string what, List<Finding> findings)
    {
        if (tree.Read(path) is not { } text || text.Trim().Length == 0)
        {
            findings.Add(Finding.Fail("docs", $"{path} is missing or empty: {what}."));
        }
    }

    // Each tool finds its instructions by its own filename. The content lives once, in AGENTS.md,
    // and the tool-specific file only points at it, so two copies can never disagree.
    private static void RequirePointer(Tree tree, string path, List<Finding> findings)
    {
        string? text = tree.Read(path);
        if (text is null)
        {
            findings.Add(Finding.Fail("docs", $"{path} is missing. It is the single line `{Pointer}`."));
        }
        else if (text.Trim() != Pointer)
        {
            findings.Add(Finding.Fail("docs", $"{path} must be exactly `{Pointer}`; what it says belongs in the AGENTS.md beside it."));
        }
    }
}

static class Workflows
{
    private static readonly Regex JobsKey = new(@"^jobs:\s*$");
    private static readonly Regex TopLevelKey = new(@"^[^\s#]");
    private static readonly Regex JobId = new(@"^  ([A-Za-z0-9_-]+):\s*$");
    private static readonly Regex JobName = new(@"^    name:\s*(.+?)\s*$");
    private static readonly Regex Expression = new(@"\$\{\{.*?\}\}");

    /// <summary>
    /// Every required check must be reported by some job, or it blocks every pull request forever.
    /// </summary>
    /// <remarks>
    /// ⚠ The workflows are read line by line, not as YAML: job ids at two spaces under `jobs:`, and
    /// the `name:` four spaces in. That is the layout every family workflow uses. A name built from
    /// `${{ }}` matches any text in its place, and a matrix job also reports as "name (values)".
    /// </remarks>
    public static void CheckRequired(Tree tree, IReadOnlyList<string> required, List<Finding> findings)
    {
        List<string> reported = [];
        foreach (string path in tree.Files.Where(IsWorkflow))
        {
            foreach ((string id, string? name) in Jobs(tree.Read(path) ?? ""))
            {
                reported.Add(id);
                if (name is not null)
                {
                    reported.Add(name);
                }
            }
        }

        foreach (string check in required)
        {
            if (!reported.Any(job => Reports(job, check)))
            {
                findings.Add(Finding.Fail("required checks", $"\"{check}\" is required but no job reports it. Jobs found: {string.Join(", ", reported.Distinct())}."));
            }
        }
    }

    /// <summary>
    /// A release publishes the ids <c>packages.push</c> names, each by its exact file, never a
    /// <c>*.nupkg</c> glob: a glob publishes whatever the build happened to produce, and a nuget.org
    /// version is permanent. A FAIL at every depth, so the release preflight stops before the push.
    /// </summary>
    /// <remarks>
    /// ⚠ Read per workflow, not per step. A workflow that publishes, by <c>nuget push</c> or by a GitHub
    /// release, may not glob a package anywhere, so a glob in a folded or continued command line, or
    /// in an action's <c>files:</c>, is caught too. A workflow that publishes nothing may: CI listing
    /// <c>./artifacts/*.nupkg</c> ships nothing. Comment lines are skipped.
    /// </remarks>
    public static void CheckPublishing(Tree tree, List<Finding> findings)
    {
        foreach (string path in tree.Files.Where(IsWorkflow))
        {
            string[] lines = (tree.Read(path) ?? "").Split('\n');
            if (!lines.Any(line => !IsComment(line) && Publishes.IsMatch(line)))
            {
                continue;
            }
            for (int i = 0; i < lines.Length; i++)
            {
                if (!IsComment(lines[i]) && PackageGlob.Match(lines[i]) is { Success: true } glob)
                {
                    findings.Add(Finding.Fail("release", $"{path}:{i + 1} publishes a package glob, `{glob.Value}`. A release publishes the ids packages.push names, each by its exact file; a glob publishes whatever the build produced, and a nuget.org version is permanent."));
                }
            }
        }
    }

    private static readonly Regex Publishes = new(@"\bnuget\s+push\b|\bgh\s+release\s+(create|upload)\b|action-gh-release");
    private static readonly Regex PackageGlob = new(@"[^\s""'()]*[*?][^\s""'()]*\.s?nupkg\b");

    private static bool IsComment(string line) => line.TrimStart().StartsWith('#');

    private static bool IsWorkflow(string path) =>
        path.StartsWith(".github/workflows/", StringComparison.Ordinal) &&
        (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    private static bool Reports(string job, string check)
    {
        string pattern = string.Join(".+", Expression.Split(job).Select(Regex.Escape));
        return Regex.IsMatch(check, "^" + pattern + "( \\(.+\\))?$");
    }

    private static IEnumerable<(string Id, string? Name)> Jobs(string yaml)
    {
        bool inJobs = false;
        string? id = null;
        string? name = null;

        foreach (string raw in yaml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (JobsKey.IsMatch(line))
            {
                inJobs = true;
                continue;
            }
            if (!inJobs)
            {
                continue;
            }
            if (TopLevelKey.IsMatch(line))
            {
                break;
            }
            if (JobId.Match(line) is { Success: true } job)
            {
                if (id is not null)
                {
                    yield return (id, name);
                }
                (id, name) = (job.Groups[1].Value, null);
            }
            else if (id is not null && name is null && JobName.Match(line) is { Success: true } named)
            {
                name = named.Groups[1].Value.Trim('"', '\'');
            }
        }

        if (id is not null)
        {
            yield return (id, name);
        }
    }
}

static class Rulesets
{
    public static Dictionary<string, long> Existing(Api api)
    {
        Dictionary<string, long> byName = new(StringComparer.Ordinal);
        (int status, string body) = api.Get("rulesets");
        if (status == 200)
        {
            foreach (JsonNode? ruleset in JsonNode.Parse(body)?.AsArray() ?? [])
            {
                byName[ruleset!["name"]!.GetValue<string>()] = ruleset["id"]!.GetValue<long>();
            }
        }
        return byName;
    }

    public static void Check(Api api, Config? config, bool admin, List<Finding> findings)
    {
        Dictionary<string, long> existing = Existing(api);

        foreach (JsonObject wanted in Baseline.Rulesets(config?.RequiredChecks ?? [], config?.Families is not null))
        {
            string name = wanted["name"]!.GetValue<string>();
            if (!existing.TryGetValue(name, out long id))
            {
                findings.Add(Finding.Fail("rulesets", $"Ruleset \"{name}\" does not exist. Run `apply`."));
                continue;
            }

            (int status, string body) = api.Get($"rulesets/{id}");
            if (status != 200 || JsonNode.Parse(body) is not JsonObject actual)
            {
                findings.Add(Finding.Unverified("rulesets", $"Ruleset \"{name}\" exists but its rules could not be read."));
                continue;
            }

            Compare(name, "target", Text(wanted["target"]), Text(actual["target"]), findings);
            Compare(name, "enforcement", Text(wanted["enforcement"]), Text(actual["enforcement"]), findings);
            Compare(name, "branches or tags", Set(wanted["conditions"]?["ref_name"]?["include"]), Set(actual["conditions"]?["ref_name"]?["include"]), findings);
            Compare(name, "rules", RuleTypes(wanted), RuleTypes(actual), findings);

            if (name == "main")
            {
                JsonNode? wantedReview = RuleParameters(wanted, "pull_request");
                JsonNode? actualReview = RuleParameters(actual, "pull_request");
                Compare(name, "required approvals", Text(wantedReview?["required_approving_review_count"]), Text(actualReview?["required_approving_review_count"]), findings);
                Compare(name, "conversation resolution", Text(wantedReview?["required_review_thread_resolution"]), Text(actualReview?["required_review_thread_resolution"]), findings);

                JsonNode? wantedChecks = RuleParameters(wanted, "required_status_checks");
                JsonNode? actualChecks = RuleParameters(actual, "required_status_checks");
                Compare(name, "required checks", Contexts(wantedChecks), Contexts(actualChecks), findings);
                Compare(name, "checks must be up to date", Text(wantedChecks?["strict_required_status_checks_policy"]), Text(actualChecks?["strict_required_status_checks_policy"]), findings);
            }

            if (actual["bypass_actors"] is not JsonArray bypass)
            {
                // Only an admin is shown who may bypass. Without --admin this is out of reach, not wrong.
                findings.Add(admin
                    ? Finding.Unverified("rulesets", $"Ruleset \"{name}\": who may bypass it could not be read.")
                    : Finding.Note("rulesets", $"Ruleset \"{name}\": who may bypass it needs admin rights to read."));
                continue;
            }

            Compare(name, "bypass", Actors(wanted["bypass_actors"]), Actors(bypass), findings);
        }
    }

    private static void Compare(string ruleset, string facet, string wanted, string actual, List<Finding> findings)
    {
        if (wanted != actual)
        {
            findings.Add(Finding.Fail("rulesets", $"Ruleset \"{ruleset}\" {facet}: wanted [{wanted}], GitHub has [{actual}]. Run `apply`."));
        }
    }

    private static string Text(JsonNode? node) => node is null ? "" : node.ToJsonString().Trim('"');

    // Derived from the baseline, never spelled out here, so `check` can never expect something
    // other than what `apply` writes.
    private static string Actors(JsonNode? actors) =>
        string.Join(", ", (actors?.AsArray() ?? []).Select(a => $"{Text(a?["actor_type"])}:{Text(a?["actor_id"])}:{Text(a?["bypass_mode"])}").Order(StringComparer.Ordinal));

    private static string Set(JsonNode? node) =>
        string.Join(", ", (node?.AsArray() ?? []).Select(n => Text(n)).Order(StringComparer.Ordinal));

    private static string RuleTypes(JsonObject ruleset) =>
        string.Join(", ", (ruleset["rules"]?.AsArray() ?? []).Select(r => Text(r?["type"])).Order(StringComparer.Ordinal));

    private static JsonNode? RuleParameters(JsonObject ruleset, string type) =>
        (ruleset["rules"]?.AsArray() ?? []).FirstOrDefault(r => Text(r?["type"]) == type)?["parameters"];

    private static string Contexts(JsonNode? parameters) =>
        string.Join(", ", (parameters?["required_status_checks"]?.AsArray() ?? []).Select(c => Text(c?["context"])).Order(StringComparer.Ordinal));
}

static class Settings
{
    /// <summary>
    /// Every setting GitHub returns is checked, at either depth. Measured with a workflow's
    /// read-only GITHUB_TOKEN: the four feature toggles come back, while the six merge options,
    /// security_and_analysis, vulnerability alerts and branch protection do not. So CI checks the
    /// features, and only --admin reaches the rest.
    /// </summary>
    public static void Check(Api api, JsonObject live, string branch, bool admin, List<Finding> findings)
    {
        List<string> unread = [];
        foreach ((string name, bool wanted) in Baseline.Settings)
        {
            if (live[name] is not JsonValue value || !value.TryGetValue(out bool actual))
            {
                if (admin)
                {
                    findings.Add(Finding.Unverified("settings", $"\"{name}\" was not returned, so it could not be checked."));
                }
                else
                {
                    unread.Add(name);
                }
            }
            else if (actual != wanted)
            {
                findings.Add(Finding.Fail("settings", $"\"{name}\" is {Lower(actual)}; the family baseline is {Lower(wanted)}. Run `apply`."));
            }
        }

        if (!admin)
        {
            string settings = unread.Count == 0 ? "" : string.Join(", ", unread) + ", ";
            findings.Add(Finding.Note("settings", $"{settings}the security toggles and branch protection need admin rights to read. `check --admin` covers them."));
            return;
        }

        // 204 when alerts are on, 404 when they are off. Only an admin can ask.
        if (api.Get("vulnerability-alerts").Status != 204)
        {
            findings.Add(Finding.Fail("security", "Vulnerability alerts are off. Run `apply`."));
        }

        string? updates = live["security_and_analysis"]?["dependabot_security_updates"]?["status"]?.GetValue<string>();
        if (updates is null)
        {
            findings.Add(Finding.Unverified("security", "Dependabot security updates were not returned, so they could not be checked."));
        }
        else if (updates != "enabled")
        {
            findings.Add(Finding.Fail("security", $"Dependabot security updates are {updates}. Run `apply`."));
        }

        if (api.Get($"branches/{branch}/protection").Status == 200)
        {
            findings.Add(Finding.Fail("settings", $"{branch} still has classic branch protection beside the `main` ruleset. Run `apply --replace-branch-protection`."));
        }
    }

    private static string Lower(bool value) => value ? "true" : "false";
}

/// <summary>
/// The family's standard build properties, read from EVALUATED projects after a restore
/// (plans/00004 in Bennewitz.Ninja.Templates).
/// </summary>
/// <remarks>
/// ⛔ Evaluated, never read as text. A property can be set, overridden or imported anywhere: a csproj,
/// a nested Directory.Build.props, a package's build props. Only MSBuild's answer says what the
/// build does; a file that looks right can build wrong.
/// ⛔ After a restore. A package's build props are imported only once it is restored, so without
/// one, any property a package sets would evaluate as unset and pass.
/// ⛔ As CI evaluates. The family set IsContinuousIntegration to $(GITHUB_ACTIONS), empty on a
/// developer machine, so every evaluation runs with GITHUB_ACTIONS=true, or the check could never
/// fail on that property locally.
/// </remarks>
/// <summary>One project as MSBuild evaluated it, the way CI builds it.</summary>
sealed record Evaluated(string Path, string Name, JsonObject Properties, JsonNode? Items, string Role, JsonObject? Debug = null)
{
    public string Value(string property) => Properties[property] is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    public IEnumerable<JsonNode> Item(string type) => (Items?[type]?.AsArray() ?? []).Where(i => i is not null)!;
}

static class Projects
{
    private static readonly string[] Names =
    [
        "TargetFramework", "TargetFrameworks", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors",
        "ManagePackageVersionsCentrally", "GenerateAutoVersionedAssemblyInfo", "AssemblyCompany",
        "IsContinuousIntegration", "IsPackable", "OutputType", "IsTestProject", "PackAsTool",
        "IsRoslynComponent", "PackageType", "Authors", "PackageLicenseExpression", "RepositoryUrl",
        "PackageReadmeFile", "DebugType", .. Trimming.Properties,
        "AssemblyName", Grants.OptOut, "PackageId", "FamilyReadme", "AssemblyProduct", "ProjectAssetsFile",
    ];

    // What a Debug build emits as symbols: read from a second evaluation, since every other property
    // is read as CI builds, in Release. Two names, so msbuild answers in JSON.
    private static readonly string[] DebugNames = ["DebugType", "OutputType"];

    private static readonly string[] ItemTypes =
        ["PackageReference", "PackageVersion", "Compile", "InternalsVisibleTo", "AssemblyAttribute", "ProjectReference"];

    private static readonly Dictionary<string, string> Ci = new(StringComparer.Ordinal) { ["GITHUB_ACTIONS"] = "true" };

    /// <summary>
    /// Restores, then evaluates every project the checks cover: the solution's, or every csproj outside
    /// shipped content where there is no solution. Null when nothing could be evaluated; a project that
    /// fails alone is reported and left out.
    /// </summary>
    public static IReadOnlyList<Evaluated>? Evaluate(string root, Tree tree, Config? config, bool remoteTree, List<Finding> findings)
    {
        if (remoteTree)
        {
            findings.Add(Finding.Note("props", "Build properties are evaluated from a checkout, after a restore; `check --repo` cannot reach them."));
            findings.Add(Finding.Note("grants", "Friend grants are evaluated from a checkout, after a restore; `check --repo` cannot reach them."));
            return null;
        }

        IReadOnlyList<string> content = config?.Content ?? [];
        string? solution = tree.Files.FirstOrDefault(f => !f.Contains('/') &&
            (f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)));

        List<string> projects;
        if (solution is not null)
        {
            (int listExit, string listed) = Shell.Run("dotnet", root, null, Ci, "sln", solution, "list");
            if (listExit != 0)
            {
                findings.Add(Finding.Fail("props", $"`dotnet sln {solution} list` failed: {Tail(listed)}"));
                return null;
            }
            projects = [.. listed.Split('\n').Select(l => l.Trim()).Where(l => l.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))];
        }
        else
        {
            projects = [.. tree.Files.Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                && !content.Any(prefix => f.StartsWith(prefix, StringComparison.Ordinal)))];
        }

        if (projects.Count == 0)
        {
            findings.Add(Finding.Note("props", "No project to evaluate."));
            return [];
        }

        foreach (string target in solution is not null ? [solution] : projects)
        {
            (int exit, string output) = Shell.Run("dotnet", root, null, Ci, "restore", target, "--nologo", "-v", "quiet");
            if (exit != 0)
            {
                findings.Add(Finding.Fail("props", $"`dotnet restore {target}` failed, so no property could be evaluated: {Tail(output)}"));
                return null;
            }
        }

        List<Evaluated> evaluated = [];
        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project.Replace('\\', '/'));
            string[] arguments = ["msbuild", project, "-p:Configuration=Release", .. Names.Select(n => "-getProperty:" + n),
                .. ItemTypes.Select(t => "-getItem:" + t)];
            (int exit, string output) = Shell.Run("dotnet", root, null, Ci, arguments);
            JsonObject? result = exit == 0 ? Parse(output) : null;
            if (result?["Properties"] is not JsonObject properties)
            {
                findings.Add(Finding.Fail("props", $"{name} could not be evaluated: {Tail(output)}"));
                continue;
            }
            JsonNode? items = result["Items"];
            if (Text(properties["TargetFramework"]).Length == 0 && Text(properties["TargetFrameworks"]) is { Length: > 0 } frameworks)
            {
                items = PerFramework(root, project, name, frameworks, items, findings);
            }

            string[] debugArguments = ["msbuild", project, "-p:Configuration=Debug", .. DebugNames.Select(n => "-getProperty:" + n)];
            (int debugExit, string debugOutput) = Shell.Run("dotnet", root, null, Ci, debugArguments);
            if ((debugExit == 0 ? Parse(debugOutput) : null)?["Properties"] is not JsonObject debug)
            {
                findings.Add(Finding.Fail("props", $"{name} could not be evaluated in Debug: {Tail(debugOutput)}"));
                continue;
            }
            evaluated.Add(new Evaluated(project, name, properties, items, Role(properties), debug));
        }
        return evaluated;
    }

    // ⛔ A multi-targeted project's outer evaluation has no TargetFramework, and holds none of the
    // project's own Compile items, nor any item an ItemGroup conditions on a framework: a grant declared
    // in its source passed unseen. So the items the grant checks read come from each framework's own
    // evaluation, combined.
    private static readonly string[] PerFrameworkTypes = ["Compile", "InternalsVisibleTo", "AssemblyAttribute", "ProjectReference"];

    private static JsonNode? PerFramework(string root, string project, string name, string frameworks, JsonNode? outer, List<Finding> findings)
    {
        JsonObject merged = outer?.DeepClone() as JsonObject ?? new JsonObject();
        Dictionary<string, (JsonArray Items, HashSet<string> Seen)> combined = [];
        foreach (string type in PerFrameworkTypes)
        {
            combined[type] = (new JsonArray(), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        foreach (string framework in frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] arguments = ["msbuild", project, "-p:Configuration=Release", "-p:TargetFramework=" + framework,
                .. PerFrameworkTypes.Select(t => "-getItem:" + t)];
            (int exit, string output) = Shell.Run("dotnet", root, null, Ci, arguments);
            if ((exit == 0 ? Parse(output) : null)?["Items"] is not JsonObject inner)
            {
                findings.Add(Finding.Fail("grants", $"{name} could not be evaluated for {framework}: {Tail(output)}"));
                continue;
            }
            foreach (string type in PerFrameworkTypes)
            {
                foreach (JsonNode? item in inner[type]?.AsArray() ?? [])
                {
                    string key = Text(item?["FullPath"]) is { Length: > 0 } full ? full : Text(item?["Identity"]) + "|" + Text(item?["_Parameter1"]);
                    if (item is not null && combined[type].Seen.Add(key))
                    {
                        combined[type].Items.Add(item.DeepClone());
                    }
                }
            }
        }

        foreach (string type in PerFrameworkTypes)
        {
            merged[type] = combined[type].Items;
        }
        return merged;
    }

    /// <summary>
    /// The first role that matches, in this order. ⚠ The order matters: an xUnit v3 test project is a
    /// non-packable executable, and would read as an app if <c>app</c> were tested first.
    /// </summary>
    private static string Role(JsonObject p) =>
        Text(p["PackageType"]) == "Template" ? "template"
        : Text(p["IsRoslynComponent"]) == "true" ? "analyzer"
        : Text(p["IsTestProject"]) == "true" ? "test"
        : Text(p["PackAsTool"]) == "true" ? "tool"
        : Text(p["IsPackable"]) == "true" ? "library"
        : Text(p["OutputType"]) is "Exe" or "WinExe" ? "app"
        : "other";

    private static JsonObject? Parse(string output)
    {
        int start = output.IndexOf('{');
        if (start < 0)
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(output[start..]) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    public static string Tail(string output)
    {
        string[] lines = [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
        return lines.Length == 0 ? "no output" : string.Join(" | ", lines.TakeLast(3));
    }
}

/// <summary>
/// Whether a repository trims (plans/00008): when any of its projects, of any role, evaluates one of
/// <see cref="Properties"/> to true. An app published trimmed or natively trims its own libraries too.
/// </summary>
/// <remarks>
/// ⛔ One predicate for both sides of the enforcement: the repository's own libraries here, and the
/// <c>Bennewitz.Ninja.*</c> packages in its restore graph. Two predicates could disagree about whether
/// the same repository trims.
/// </remarks>
static class Trimming
{
    public static readonly string[] Properties = ["IsTrimmable", "EnableTrimAnalyzer", "PublishTrimmed", "PublishAot"];

    public static bool Trims(IReadOnlyList<Evaluated> projects) => Reasons(projects).Count > 0;

    /// <summary>Each project that makes the repository trim, with the properties that do; empty when it does not.</summary>
    public static IReadOnlyList<string> Reasons(IReadOnlyList<Evaluated> projects) =>
        [.. projects
            .Select(e => (e.Name, Set: Properties.Where(p => e.Value(p) == "true").ToArray()))
            .Where(e => e.Set.Length > 0)
            .Select(e => $"{e.Name}: {string.Join(", ", e.Set)}")];

    /// <summary>The reasons for a finding: the first three, and how many more.</summary>
    public static string Describe(IReadOnlyList<string> reasons) =>
        string.Join("; ", reasons.Take(3)) + (reasons.Count > 3 ? $"; and {reasons.Count - 3} more" : "");
}

/// <summary>
/// The other side of the trimming enforcement (plans/00008 step 3): in a repository that trims, each
/// <c>Bennewitz.Ninja.*</c> package a library or an app takes, transitive ones included, carries
/// <c>[assembly: AssemblyMetadata("IsTrimmable", "True")]</c> in every assembly it adds to the build.
/// </summary>
/// <remarks>
/// ⛔ <c>TrimMode=partial</c> trims only the assemblies that carry it, so an unmarked family package
/// is silently left whole and its trim warnings are never raised. Read from the project's
/// <c>project.assets.json</c> and each assembly's metadata, loading nothing. A package that adds no
/// assembly, as an analyzer or an MSBuild package does, has nothing to trim.
/// </remarks>
static class TrimmablePackages
{
    private const string Prefix = "Bennewitz.Ninja.";

    // Each assembly is read once, however many projects take its package.
    private static readonly Dictionary<string, bool?> Marked = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The problem with a project's restore graph, or null when each family package in it is trimmable.</summary>
    public static string? Problem(string assetsFile, IReadOnlyList<string> trimmedBy)
    {
        string trims = $"and the repository trims ({Trimming.Describe(trimmedBy)})";
        JsonObject? assets = null;
        try
        {
            assets = File.Exists(assetsFile) ? JsonNode.Parse(File.ReadAllText(assetsFile)) as JsonObject : null;
        }
        catch (JsonException)
        {
        }
        if (assets is null)
        {
            return $"has no readable restore graph{(assetsFile.Length > 0 ? " at " + assetsFile : "")}, so its {Prefix}* packages cannot be checked, {trims}.";
        }

        string folder = (assets["packageFolders"] as JsonObject)?.Select(f => f.Key).FirstOrDefault() ?? "";
        JsonObject libraries = assets["libraries"] as JsonObject ?? new JsonObject();
        SortedSet<string> unmarked = new(StringComparer.OrdinalIgnoreCase);
        SortedSet<string> unread = new(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, JsonNode?> target in assets["targets"] as JsonObject ?? new JsonObject())
        {
            foreach (KeyValuePair<string, JsonNode?> package in target.Value as JsonObject ?? new JsonObject())
            {
                if (!package.Key.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || Text(package.Value?["type"]) != "package")
                {
                    continue;
                }
                string path = Text(libraries[package.Key]?["path"]);
                string name = package.Key.Replace('/', ' ');
                foreach (KeyValuePair<string, JsonNode?> asset in package.Value?["runtime"] as JsonObject ?? new JsonObject())
                {
                    // An empty folder's placeholder: the package adds no assembly for this framework.
                    if (Path.GetFileName(asset.Key) == "_._")
                    {
                        continue;
                    }
                    switch (IsMarked(Path.Combine(folder, path, asset.Key)))
                    {
                        case false:
                            unmarked.Add(name);
                            break;
                        case null:
                            unread.Add($"{name} ({asset.Key})");
                            break;
                    }
                }
            }
        }

        if (unmarked.Count == 0 && unread.Count == 0)
        {
            return null;
        }
        List<string> problems = [];
        if (unmarked.Count > 0)
        {
            problems.Add($"takes {string.Join(", ", unmarked)}, without IsTrimmable");
        }
        if (unread.Count > 0)
        {
            problems.Add($"takes {string.Join(", ", unread)}, whose assembly could not be read");
        }
        return $"{string.Join("; ", problems)}, {trims}: every {Prefix}* package a library or an app takes, transitive ones included, must be trimmable. Take a version that is.";
    }

    /// <summary>Whether the assembly carries IsTrimmable; null when it cannot be read.</summary>
    private static bool? IsMarked(string file)
    {
        if (Marked.TryGetValue(file, out bool? known))
        {
            return known;
        }

        bool? marked = null;
        try
        {
            using FileStream stream = File.OpenRead(file);
            using PEReader pe = new(stream);
            if (pe.HasMetadata)
            {
                MetadataReader reader = pe.GetMetadataReader();
                marked = false;
                foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
                {
                    CustomAttribute attribute = reader.GetCustomAttribute(handle);
                    if (!IsAssemblyMetadata(reader, attribute.Constructor))
                    {
                        continue;
                    }
                    // The attribute's blob: a prolog of 1, then its two string arguments, key and value.
                    BlobReader value = reader.GetBlobReader(attribute.Value);
                    if (value.ReadUInt16() == 1 && value.ReadSerializedString() == "IsTrimmable")
                    {
                        marked = string.Equals(value.ReadSerializedString(), "True", StringComparison.OrdinalIgnoreCase);
                        break;
                    }
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
        }
        Marked[file] = marked;
        return marked;
    }

    private static bool IsAssemblyMetadata(MetadataReader reader, EntityHandle constructor)
    {
        if (constructor.Kind != HandleKind.MemberReference)
        {
            return false;
        }
        EntityHandle parent = reader.GetMemberReference((MemberReferenceHandle)constructor).Parent;
        if (parent.Kind != HandleKind.TypeReference)
        {
            return false;
        }
        TypeReference type = reader.GetTypeReference((TypeReferenceHandle)parent);
        return reader.StringComparer.Equals(type.Name, "AssemblyMetadataAttribute")
            && reader.StringComparer.Equals(type.Namespace, "System.Reflection");
    }

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";
}

/// <summary>
/// Package families (plans/00009): a repository that declares <c>families</c> in repository.json
/// releases each one alone, at its own version.
/// </summary>
static class Families
{
    /// <summary>
    /// A packable project never references a packable project of another family. Across families the
    /// reference is a <c>PackageReference</c> to a published version.
    /// </summary>
    /// <remarks>
    /// ⛔ A family's release packs at that family's version, so a <c>ProjectReference</c> across families
    /// would pack a dependency on a version of the other family that need not exist. Each side is read
    /// by its evaluated <c>PackageId</c>, never its file name. A test project, a tool or a demo that
    /// packs nothing may reference anything.
    /// </remarks>
    public static void CheckReferences(string root, IReadOnlyList<Evaluated> projects, Config? config, List<Finding> findings)
    {
        if (config?.Families is not { Count: > 0 } families)
        {
            return;
        }

        Dictionary<string, string> familyOf = ById(families);
        Dictionary<string, Evaluated> byPath = new(StringComparer.OrdinalIgnoreCase);
        foreach (Evaluated project in projects)
        {
            byPath[Full(Path.Combine(root, project.Path))] = project;
        }

        foreach (Evaluated project in projects)
        {
            if (FamilyOf(project, familyOf) is not { } family)
            {
                continue;
            }
            foreach (JsonNode reference in project.Item("ProjectReference"))
            {
                string path = reference["FullPath"] is JsonValue value && value.TryGetValue(out string? full) ? full : "";
                if (path.Length == 0 || !byPath.TryGetValue(Full(path), out Evaluated? target))
                {
                    continue;
                }
                if (FamilyOf(target, familyOf) is { } other && other != family)
                {
                    findings.Add(Finding.Fail("families", $"{project.Name}, of the {family} family, references {target.Name}, of the {other} family, as a project. Reference {target.Value("PackageId")} as a package at a published version: {family}'s release packs at {family}'s version, which {other} need not have."));
                }
            }
        }
    }

    /// <summary>
    /// Each family's README is <c>docs/&lt;stem&gt;.md</c> (plans/00011): it must be there, and link only
    /// absolutely. It is packed as the README of each of the family's packages, a page on nuget.org,
    /// where a relative link resolves against nuget.org and goes nowhere.
    /// </summary>
    /// <remarks>Read from the tree, so <c>check --repo</c> reaches it as well as a checkout.</remarks>
    public static void CheckReadmes(Tree tree, Config? config, List<Finding> findings)
    {
        if (config?.Families is not { Count: > 0 } families)
        {
            return;
        }

        foreach (string family in families.Keys)
        {
            string path = Readme(family);
            if (tree.Read(path) is not { } text || text.Trim().Length == 0)
            {
                findings.Add(Finding.Fail("families", $"The {family} family has no {path}. It is the family's README, packed as README.md into each of its packages: the page nuget.org shows for them."));
                continue;
            }
            foreach ((int line, string target) in Links.Relative(text))
            {
                findings.Add(Finding.Fail("families", $"{path}:{line} links to \"{target}\", relatively. It is the {family} family's README on nuget.org, where a relative link goes nowhere: link absolutely, or to an #anchor on the page."));
            }
        }
    }

    /// <summary>
    /// Each of a family's packages names the family's README in <c>FamilyReadme</c>, which the root
    /// props pack as its README.md, and carries the family's <c>AssemblyProduct</c> (plans/00011).
    /// </summary>
    /// <remarks>
    /// ⛔ A package that names no README packs the repository's, so its nuget.org page is the wrong
    /// one, and nothing else fails. The product is stamped into every assembly a version ships,
    /// permanently. It is the stem without regard to case or separators: <c>XamlQuality</c> for
    /// <c>xamlquality</c>, <c>AppServices</c> for <c>app-services</c>.
    /// </remarks>
    public static void CheckPackages(IReadOnlyList<Evaluated> projects, Config? config, List<Finding> findings)
    {
        if (config?.Families is not { Count: > 0 } families)
        {
            return;
        }

        Dictionary<string, string> familyOf = ById(families);
        foreach (Evaluated project in projects)
        {
            if (FamilyOf(project, familyOf) is not { } family)
            {
                continue;
            }

            string readme = project.Value("FamilyReadme").Trim().Replace('\\', '/');
            if (readme.Length == 0)
            {
                findings.Add(Finding.Fail("families", $"{project.Name}, of the {family} family, sets no FamilyReadme, so it packs the repository's README.md. Set it to {Readme(family)}, the family's."));
            }
            else if (readme != Readme(family))
            {
                findings.Add(Finding.Fail("families", $"{project.Name}, of the {family} family, has FamilyReadme \"{readme}\"; the family's is {Readme(family)}."));
            }

            string product = project.Value("AssemblyProduct");
            if (!Bare(product).Equals(Bare(family), StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(Finding.Fail("families", $"{project.Name}, of the {family} family, has AssemblyProduct \"{product}\"; a family's packages carry the family's, {family} without regard to case or separators, stamped into every assembly they ship."));
            }
        }
    }

    public static string Readme(string family) => $"docs/{family}.md";

    private static string Bare(string name) => string.Concat(name.Where(char.IsLetterOrDigit));

    private static Dictionary<string, string> ById(IReadOnlyDictionary<string, IReadOnlyList<string>> families)
    {
        Dictionary<string, string> familyOf = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string family, IReadOnlyList<string> ids) in families)
        {
            foreach (string id in ids)
            {
                familyOf.TryAdd(id, family);
            }
        }
        return familyOf;
    }

    private static string? FamilyOf(Evaluated project, Dictionary<string, string> familyOf) =>
        project.Value("IsPackable") == "true" && familyOf.TryGetValue(project.Value("PackageId"), out string? family) ? family : null;

    private static string Full(string path) => Path.GetFullPath(path.Replace('\\', '/')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
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

static class Props
{
    public const string AutoVersioningFloor = "2026.3.916";
    private const string AutoVersioning = "Bennewitz.Ninja.AutoVersioning";

    public static void Check(IReadOnlyList<Evaluated> projects, Config? config, string? repoName, List<Finding> findings)
    {
        if (projects.Count == 0)
        {
            return;
        }

        IReadOnlyDictionary<string, string> none = new Dictionary<string, string>();
        IReadOnlyList<string> trimmedBy = Trimming.Reasons(projects);
        List<string> roles = [];
        foreach (Evaluated evaluated in projects)
        {
            string name = evaluated.Name;
            JsonObject properties = evaluated.Properties;
            string role = evaluated.Role;
            roles.Add($"{name} ({role})");
            IReadOnlyDictionary<string, string> exempt = config?.PropsExempt.GetValueOrDefault(name) ?? none;

            foreach ((string rule, string? problem) in Rules(properties, evaluated.Debug, evaluated.Items, role, repoName, trimmedBy))
            {
                if (problem is null)
                {
                    if (exempt.ContainsKey(rule))
                    {
                        findings.Add(Finding.Note("props", $"{name}: the exemption for {rule} is no longer needed; remove it from repository.json."));
                    }
                }
                else if (!exempt.ContainsKey(rule))
                {
                    findings.Add(Finding.Fail("props", $"{name} ({role}): {problem}"));
                }
            }
        }

        findings.Add(Finding.Note("props", $"{projects.Count} projects evaluated after a restore, as CI evaluates them: {string.Join(", ", roles)}."));
    }

    /// <summary>Every rule for this role, each with its problem, or null where the project meets it.</summary>
    private static IEnumerable<(string Rule, string? Problem)> Rules(JsonObject p, JsonObject? debug, JsonNode? items, string role, string? repoName,
        IReadOnlyList<string> trimmedBy)
    {
        string frameworks = Value(p, "TargetFrameworks") is { Length: > 0 } many ? many : Value(p, "TargetFramework");
        string[] targets = frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string wantedFramework = role == "analyzer" ? "netstandard2.0" : "net10.0";
        yield return ("TargetFramework", targets.Contains(wantedFramework)
            ? null
            : $"targets [{frameworks}]; {(role == "analyzer" ? "an analyzer targets" : "the family targets")} {wantedFramework}.");

        yield return Expect(p, "Nullable", "enable");
        yield return Expect(p, "ImplicitUsings", "enable");
        yield return Expect(p, "TreatWarningsAsErrors", "true");
        yield return Expect(p, "ManagePackageVersionsCentrally", "true");
        yield return Expect(p, "AssemblyCompany", "Bennewitz.Ninja");

        string ci = Value(p, "IsContinuousIntegration");
        yield return ("IsContinuousIntegration", ci.Length == 0
            ? null
            : $"IsContinuousIntegration evaluates to \"{ci}\" as CI builds it. Nothing reads it since AutoVersioning {AutoVersioningFloor}; remove it.");

        string? version = AutoVersioningVersion(items);
        yield return ("AutoVersioning",
            version is null ? $"does not reference {AutoVersioning}."
            : Value(p, "GenerateAutoVersionedAssemblyInfo") != "true" ? "GenerateAutoVersionedAssemblyInfo is not true, so AutoVersioning generates nothing."
            : !AtLeast(version, AutoVersioningFloor) ? $"references {AutoVersioning} {version}; the family's floor is {AutoVersioningFloor}."
            : null);

        // ⭐ Symbols in both configurations, for every project that compiles: a Debug build writes a
        // .pdb beside the assembly or into it, for stepping through the source; a Release build embeds
        // it, so whatever ships, a package, a single-file app or a test run's binaries, carries its own.
        // A template package compiles nothing.
        //
        // ⚠ DebugType alone decides it. DebugSymbols evaluates to false in Release by default, and the
        // PDB is embedded all the same (measured on SDK 10.0.401), so a rule reading it would fail
        // every correct project.
        if (role != "template")
        {
            string releaseType = Value(p, "DebugType");
            yield return ("DebugType", releaseType == "embedded"
                ? null
                : $"builds Release with DebugType \"{releaseType}\"; the family embeds symbols in Release: DebugType \"embedded\".");

            string debugType = debug is null ? "" : Value(debug, "DebugType");
            yield return ("DebugSymbols", debug is null || debugType is "portable" or "embedded"
                ? null
                : $"builds Debug with DebugType \"{debugType}\", so no symbols; the family's are \"portable\" (a .pdb beside the assembly) or \"embedded\".");
        }

        // Libraries only (plans/00008): a tool runs on the installed framework, an app's trimming is
        // decided by its kind. A repository that does not trim holds no library to it, so an
        // exemption there is stale too.
        if (role == "library")
        {
            yield return ("Trimming", trimmedBy.Count == 0 || (Value(p, "IsTrimmable") == "true" && Value(p, "EnableTrimAnalyzer") == "true")
                ? null
                : $"is not trimmable, and the repository trims ({Trimming.Describe(trimmedBy)}): every library needs IsTrimmable and EnableTrimAnalyzer, as the template's src/Directory.Build.props sets them.");
        }

        // The other side (plans/00008 step 3): what a library or an app takes from the family is trimmed
        // with it. A test runs untrimmed, a tool on the installed framework and an analyzer in the
        // compiler, so their restore graphs owe nothing.
        if (role is "library" or "app")
        {
            yield return ("TrimmablePackages", trimmedBy.Count == 0 ? null : TrimmablePackages.Problem(Value(p, "ProjectAssetsFile"), trimmedBy));
        }

        if (role is not ("library" or "tool"))
        {
            yield break;
        }

        yield return ("Authors", Value(p, "Authors").Trim().Length > 0 ? null : "sets no Authors, so the package is authored by its assembly name.");
        yield return Expect(p, "PackageLicenseExpression", "MIT");
        if (repoName is not null)
        {
            string url = Value(p, "RepositoryUrl");
            yield return ("RepositoryUrl", url.Contains("github.com/" + repoName, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"RepositoryUrl is \"{url}\", not this repository, github.com/{repoName}.");
        }
        // A package shows a README on nuget.org whatever the file is called: DiffView's libraries
        // pack a hosting guide as theirs. That the named file is actually packed, NuGet enforces
        // itself: `dotnet pack` fails with NU5039 when it is not.
        yield return ("PackageReadmeFile", Value(p, "PackageReadmeFile").Trim().Length > 0
            ? null
            : "sets no PackageReadmeFile, so nuget.org shows the package without a README.");
    }

    private static (string Rule, string? Problem) Expect(JsonObject p, string name, string wanted)
    {
        string actual = Value(p, name);
        return (name, actual == wanted ? null : $"{name} is \"{actual}\"; the family's is \"{wanted}\".");
    }

    /// <summary>
    /// The version a project references AutoVersioning at, or null when it does not.
    /// ⚠ Under central package management a PackageReference carries no version: it is on the
    /// PackageVersion item. Without central management it is on the reference. A VersionOverride wins.
    /// </summary>
    private static string? AutoVersioningVersion(JsonNode? items)
    {
        JsonNode? reference = (items?["PackageReference"]?.AsArray() ?? []).FirstOrDefault(i => Text(i?["Identity"]) == AutoVersioning);
        if (reference is null)
        {
            return null;
        }
        JsonNode? pin = (items?["PackageVersion"]?.AsArray() ?? []).FirstOrDefault(i => Text(i?["Identity"]) == AutoVersioning);
        foreach (string candidate in (string[])[Text(reference["VersionOverride"]), Text(reference["Version"]), Text(pin?["Version"])])
        {
            if (candidate.Length > 0)
            {
                return candidate;
            }
        }
        return "an unknown version";
    }

    private static bool AtLeast(string version, string floor)
    {
        string bare = version.Trim('[', ']', '(', ')', ' ').Split(',')[0].Split('-', '+')[0];
        return Version.TryParse(bare, out Version? actual) && Version.TryParse(floor, out Version? minimum) && actual >= minimum;
    }

    private static string Value(JsonObject p, string name) => Text(p[name]);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";
}

/// <summary>
/// Solution-wide friend grants (plans/00006): every project grants InternalsVisibleTo to every
/// assembly the repository builds, through one generated file that Directory.Build.targets links into
/// each project, and one hand-written file for grants to other repositories.
/// </summary>
static class Grants
{
    public const string File = "AssemblyInfo.InternalsVisibleTo.cs";
    public const string External = "AssemblyInfo.InternalsVisibleTo.External.cs";
    public const string OptOut = "SolutionFriendGrants";

    private const string Regenerate = "dotnet run --file scripts/repo-conventions.cs -- grants";

    /// <summary>Ends a line of the External file whose prefixed name is a real assembly's.</summary>
    public const string PrefixChecked = "// repo-conventions: assembly name";

    // A grant declared in C#, read from the code alone (Code strips comments and every literal): an
    // [assembly: …] section anywhere, across lines, holding the attribute among others or under a
    // `using` alias. ⛔ Each of these was a real miss, or a false finding, of a line-by-line match: a
    // grant listed after another attribute, one split across lines, one through an alias, and a raw
    // string literal whose line begins with the attribute's text (CodeQuality's tests carry one).
    private static readonly Regex AssemblySection = new(@"\[\s*assembly\s*:(?<body>[^\]]*)\]", RegexOptions.CultureInvariant);

    private static readonly Regex Alias = new(
        @"\busing\s+(?<alias>\w+)\s*=\s*(global::)?(System\.Runtime\.CompilerServices\.)?InternalsVisibleTo(Attribute)?\s*;",
        RegexOptions.CultureInvariant);

    private static bool DeclaresGrant(string source)
    {
        string code = Code(source);
        string[] names = ["InternalsVisibleTo", "InternalsVisibleToAttribute", .. Alias.Matches(code).Select(m => m.Groups["alias"].Value)];
        Regex attribute = new(
            @"(?<![\w.])(global::)?(System\.Runtime\.CompilerServices\.)?(" + string.Join("|", names.Select(Regex.Escape)) + @")\s*\(",
            RegexOptions.CultureInvariant);
        return AssemblySection.Matches(code).Any(section => attribute.IsMatch(section.Groups["body"].Value));
    }

    /// <summary>
    /// C# source with comments and every string or character literal blanked, so only code is left:
    /// regular, verbatim (@), interpolated ($, $@, @$) and raw ("""…""", with any $ prefix).
    /// </summary>
    private static string Code(string text)
    {
        StringBuilder code = new(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            char next = i + 1 < text.Length ? text[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
                continue;
            }
            if (c == '/' && next == '*')
            {
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? text.Length : end + 2;
                code.Append(' ');
                continue;
            }
            if (c == '\'')
            {
                i++;
                while (i < text.Length && text[i] != '\'' && text[i] != '\n')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }
                i++;
                code.Append("' '");
                continue;
            }

            // A string literal, after its $ and @ prefixes.
            int start = i;
            bool verbatim = false;
            while (i < text.Length && (text[i] == '$' || text[i] == '@'))
            {
                verbatim |= text[i] == '@';
                i++;
            }
            if (i >= text.Length || text[i] != '"')
            {
                i = start;
                code.Append(c);
                i++;
                continue;
            }

            int quotes = 0;
            while (i + quotes < text.Length && text[i + quotes] == '"')
            {
                quotes++;
            }
            if (quotes >= 3)
            {
                // Raw: closed by the same run of quotes.
                int close = text.IndexOf(new string('"', quotes), i + quotes, StringComparison.Ordinal);
                i = close < 0 ? text.Length : close + quotes;
            }
            else if (verbatim)
            {
                i++;
                while (i < text.Length && !(text[i] == '"' && (i + 1 >= text.Length || text[i + 1] != '"')))
                {
                    i += text[i] == '"' ? 2 : 1;
                }
                i++;
            }
            else
            {
                i++;
                while (i < text.Length && text[i] != '"' && text[i] != '\n')
                {
                    i += text[i] == '\\' ? 2 : 1;
                }
                i++;
            }
            code.Append("\"\"");
        }
        return code.ToString();
    }

    private static readonly Regex Named = new(
        @"InternalsVisibleTo(Attribute)?\s*\(\s*""([^""]+)""", RegexOptions.CultureInvariant);

    public const string EmptyExternal =
        """
        // AssemblyInfo.InternalsVisibleTo.External.cs: grants to assemblies OUTSIDE this repository.
        //
        // Written by hand. `repo-conventions grants` writes this file only when it is missing, and never
        // touches it after. Directory.Build.targets links it into every project that links
        // AssemblyInfo.InternalsVisibleTo.cs, so a name here sees the internals of every such project.
        //
        // A name is an ASSEMBLY name. Most family assemblies are unprefixed ("AppServices"), and a grant
        // to the root namespace instead ("Bennewitz.Ninja.AppServices") compiles, ships and grants
        // nothing. Some do carry the prefix (FileServer's, Geo.Core's): `repo-conventions check` notes
        // every prefixed name, and a line ending `// repo-conventions: assembly name` marks one checked.
        // An internal another repository uses is a promise: change it only together with a release of
        // that repository. See docs/repository-conventions.md in Bennewitz.Ninja.Templates.
        //
        // [assembly: System.Runtime.CompilerServices.InternalsVisibleTo("AppServices")]

        """;

    /// <summary>The assembly names the generated file grants to: every evaluated project's but a template's.</summary>
    public static string[] Assemblies(IReadOnlyList<Evaluated> projects) =>
        [.. projects.Where(p => p.Role != "template")
            .Select(p => p.Value("AssemblyName") is { Length: > 0 } assembly ? assembly : p.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    public static string Generate(IEnumerable<string> assemblies)
    {
        StringBuilder text = new(
            """
            // <auto-generated>
            //   Written by `dotnet run --file scripts/repo-conventions.cs -- grants` from the assembly name
            //   of every project in this repository. Do not edit it: regenerate it. `repo-conventions check`
            //   fails when it differs from what `grants` would write.
            // </auto-generated>
            //
            // Solution-wide friend grants: Directory.Build.targets links this file into every project, so
            // each project's internals are visible to every other. `internal` means solution-internal, and a
            // member no other assembly may reach is `private`. A project that sets SolutionFriendGrants to
            // false grants nothing, and may declare grants of its own. None of this is a security boundary:
            // no family assembly is strong-named. See docs/repository-conventions.md in
            // Bennewitz.Ninja.Templates.


            """);
        foreach (string assembly in assemblies)
        {
            text.Append("[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"").Append(assembly).Append("\")]\n");
        }
        return text.ToString();
    }

    public static void Check(string root, Tree tree, IReadOnlyList<Evaluated> projects, List<Finding> findings)
    {
        if (projects.Count == 0)
        {
            return;
        }

        string[] wanted = Assemblies(projects);
        string? current = tree.Read(File);
        if (current is null)
        {
            findings.Add(Finding.Fail("grants", $"{File} is missing. Run `{Regenerate}`, which writes it from the projects' assembly names."));
        }
        else if (Normalize(current) != Normalize(Generate(wanted)))
        {
            findings.Add(Finding.Fail("grants",
                $"{File} is out of date: it grants [{string.Join(", ", Names(current))}], but the projects build [{string.Join(", ", wanted)}]. Run `{Regenerate}`."));
        }

        string? external = tree.Read(External);
        if (external is null)
        {
            findings.Add(Finding.Fail("grants", $"{External} is missing. `grants` writes an empty one; grants to other repositories go there, by hand."));
        }
        else
        {
            // ⚠ A NOTE, not a FAIL (the maintainer's decision, 2026-10-01): a prefixed name is a root
            // namespace by mistake for most family assemblies, but FileServer's, Geo.Core's and others'
            // assemblies really carry the prefix, and a grant to one of them is right. A wrong name
            // grants nothing, and fails the consumer's own build (CS0122) the moment it is used.
            foreach (string line in Uncommented(external).Where(l => !l.Contains(PrefixChecked, StringComparison.Ordinal)))
            {
                foreach (string name in Named.Matches(line).Select(m => m.Groups[2].Value).Where(n => n.StartsWith("Bennewitz.Ninja.", StringComparison.Ordinal)))
                {
                    findings.Add(Finding.Note("grants",
                        $"{External} grants \"{name}\", a prefixed name. A grant matches an assembly's name, so this is right only if an assembly is named exactly \"{name}\" (FileServer's and Geo.Core's are; most family assemblies are unprefixed); a name no assembly has grants nothing. "
                        + $"If it is wrong, name the assembly. If it is right, silence this note by ending that line with the comment `{PrefixChecked}`, so it reads: "
                        + $"[assembly: System.Runtime.CompilerServices.InternalsVisibleTo(\"{name}\")] {PrefixChecked}"));
                }
            }
        }

        string[] linked = [Path.GetFullPath(Path.Combine(root, File)), Path.GetFullPath(Path.Combine(root, External))];
        foreach (Evaluated project in projects.Where(p => p.Role != "template"))
        {
            // Case-insensitive, as MSBuild compares the link's condition: "False" unlinks the files too.
            if (string.Equals(project.Value(OptOut), "false", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] compiled = [.. project.Item("Compile").Select(i => Text(i["FullPath"]) is { Length: > 0 } full ? full : Text(i["Identity"]))];
            foreach (string file in linked.Where(f => !compiled.Contains(f, StringComparer.OrdinalIgnoreCase)))
            {
                findings.Add(Finding.Fail("grants",
                    $"{project.Name} does not compile {System.IO.Path.GetFileName(file)}. The root Directory.Build.targets links it into every project, as the template's does: add the link if the repository has none, import the root file from a nearer Directory.Build.targets that shadows it, or set {OptOut} to false in {project.Name}."));
            }

            foreach (JsonNode item in project.Item("InternalsVisibleTo"))
            {
                findings.Add(Finding.Fail("grants", $"{project.Name} declares its own grant to \"{Text(item["Identity"])}\" in an <InternalsVisibleTo> item. {OwnGrant}"));
            }

            foreach (JsonNode item in project.Item("AssemblyAttribute").Where(i =>
                Text(i["Identity"]) is "System.Runtime.CompilerServices.InternalsVisibleTo" or "System.Runtime.CompilerServices.InternalsVisibleToAttribute"))
            {
                findings.Add(Finding.Fail("grants", $"{project.Name} declares its own grant to \"{Text(item["_Parameter1"])}\" in an <AssemblyAttribute>. {OwnGrant}"));
            }

            foreach (string file in compiled.Where(f => !linked.Contains(f, StringComparer.OrdinalIgnoreCase) && System.IO.File.Exists(f)))
            {
                if (DeclaresGrant(System.IO.File.ReadAllText(file)))
                {
                    findings.Add(Finding.Fail("grants",
                        $"{project.Name}: {System.IO.Path.GetRelativePath(root, file).Replace('\\', '/')} declares [assembly: InternalsVisibleTo]. {OwnGrant}"));
                }
            }
        }
    }

    private const string OwnGrant =
        $"Grants come from {File}, or {External} for another repository; a project that needs its own sets {OptOut} to false.";

    /// <summary>The names granted in a file's code, comment lines left out.</summary>
    private static string[] Names(string text) =>
        [.. Uncommented(text).SelectMany(line => Named.Matches(line).Select(m => m.Groups[2].Value))];

    private static IEnumerable<string> Uncommented(string text) =>
        text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";
}

static class Drift
{
    private const string Canonical = "templates/bbpkg/scripts/repo-conventions.cs";
    private const string Copy = "scripts/repo-conventions.cs";

    /// <summary>
    /// Every repository carries its own copy of this script, and the template's is the one they
    /// follow. The comparison ignores line endings, which a Windows checkout may have converted.
    /// </summary>
    public static void Check(Tree tree, bool remoteTree, List<Finding> findings)
    {
        string? canonical = tree.Read(Canonical);
        if (canonical is null && remoteTree && File.Exists(Canonical))
        {
            // Run from the template repository against another one: compare with the local template.
            canonical = File.ReadAllText(Canonical);
        }
        if (canonical is null)
        {
            return;
        }

        string? copy = tree.Read(Copy);
        if (copy is null)
        {
            findings.Add(Finding.Fail("drift", $"{Copy} is missing."));
        }
        else if (Normalize(copy) != Normalize(canonical))
        {
            findings.Add(Finding.Fail("drift", $"{Copy} differs from the template's {Canonical}. Copy the template's over it."));
        }
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>The repository's files: a working tree, or the default branch read through the API.</summary>
sealed class Tree
{
    private readonly Func<string, string?> _read;

    private Tree(IReadOnlyList<string> files, Func<string, string?> read) => (Files, _read) = (files, read);

    /// <summary>Paths relative to the root, with forward slashes.</summary>
    public IReadOnlyList<string> Files { get; }

    public string? Read(string path) => Files.Contains(path) ? _read(path) : null;

    public IEnumerable<string> TopLevelDirectories() =>
        Files.Where(p => p.Contains('/')).Select(p => p[..p.IndexOf('/')]).Distinct().Order(StringComparer.Ordinal);

    public static Tree Local(string root)
    {
        // Tracked and untracked-but-not-ignored, so a document written and not yet committed counts.
        (int exit, string output) = Shell.Run("git", root, null, "ls-files", "--cached", "--others", "--exclude-standard", "-z");
        if (exit != 0)
        {
            throw new InvalidOperationException($"`git ls-files` failed in {root}: {output.Trim()}");
        }

        string[] files = [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => File.Exists(Path.Combine(root, p)))
            .Distinct()];
        return new Tree(files, path => File.ReadAllText(Path.Combine(root, path)));
    }

    public static Tree Remote(Api api, string branch)
    {
        (int status, string body) = api.Get($"git/trees/{branch}?recursive=1");
        if (status != 200)
        {
            throw new InvalidOperationException($"could not read the tree of {branch}.");
        }

        string[] files = [.. (JsonNode.Parse(body)?["tree"]?.AsArray() ?? [])
            .Where(e => e?["type"]?.GetValue<string>() == "blob")
            .Select(e => e!["path"]!.GetValue<string>())];

        return new Tree(files, path =>
        {
            (int fileStatus, string file) = api.Get($"contents/{Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal)}?ref={branch}");
            string? content = fileStatus == 200 ? JsonNode.Parse(file)?["content"]?.GetValue<string>() : null;
            return content is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(content.Replace("\n", "", StringComparison.Ordinal)));
        });
    }
}

/// <summary>The GitHub REST API through `gh`, or through fixture files in tests.</summary>
sealed class Api(string repo, string? fixtures)
{
    /// <summary>
    /// GET repos/{repo}/{path}. Status is 200 with a body, 204 without one, or 404 for anything that
    /// failed, which is how GitHub answers a caller who may not see the resource.
    /// </summary>
    public (int Status, string Body) Get(string path)
    {
        if (fixtures is not null)
        {
            string file = Path.Combine(fixtures, FixtureName(path));
            if (!File.Exists(file))
            {
                return (404, "");
            }
            string text = File.ReadAllText(file);
            return (text.Trim().Length == 0 ? 204 : 200, text);
        }

        (int exit, string output) = Gh.Api("GET", Target(path), null);
        return exit != 0 ? (404, output) : (output.Trim().Length == 0 ? 204 : 200, output);
    }

    public (bool Ok, string Output) Send(string method, string path, string? body)
    {
        (int exit, string output) = Gh.Api(method, Target(path), body);
        return (exit == 0, output);
    }

    private string Target(string path) => $"repos/{repo}" + (path.Length > 0 ? "/" + path : "");

    /// <summary>"" → repo.json, "rulesets/42" → rulesets-42.json; any query string is dropped.</summary>
    public static string FixtureName(string path)
    {
        string bare = path.Split('?')[0];
        return (bare.Length == 0 ? "repo" : bare.Replace('/', '-')) + ".json";
    }
}

static class Gh
{
    public static (int Exit, string Output) Api(string method, string target, string? body)
    {
        List<string> arguments = ["api", "--method", method, "-H", "Accept: application/vnd.github+json", target];
        if (body is not null)
        {
            arguments.AddRange(["--input", "-"]);
        }
        return Shell.Run("gh", null, body, [.. arguments]);
    }

    public static string? RepoOf(string root)
    {
        (int exit, string output) = Shell.Run("gh", root, null, "repo", "view", "--json", "nameWithOwner", "--jq", ".nameWithOwner");
        return exit == 0 && output.Trim().Length > 0 ? output.Trim() : null;
    }
}

static class Shell
{
    public static (int Exit, string Output) Run(string file, string? directory, string? input, params string[] arguments) =>
        Run(file, directory, input, null, arguments);

    public static (int Exit, string Output) Run(string file, string? directory, string? input,
        IReadOnlyDictionary<string, string>? environment, params string[] arguments)
    {
        ProcessStartInfo start = new(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
        };
        if (directory is not null)
        {
            start.WorkingDirectory = directory;
        }
        foreach ((string name, string value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)!;
        if (input is not null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, process.ExitCode == 0 ? stdout.Result : stdout.Result + stderr.Result);
    }
}

static class Json
{
    public static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(v => (JsonNode)JsonValue.Create(v)!)]);
}

sealed record Finding(string Kind, string Area, string Message)
{
    public static Finding Fail(string area, string message) => new("FAIL", area, message);

    /// <summary>Could not be read. Fails under --admin, where everything should be readable.</summary>
    public static Finding Unverified(string area, string message) => new("UNVERIFIED", area, message);

    /// <summary>Out of this depth's reach by design; never fails.</summary>
    public static Finding Note(string area, string message) => new("NOTE", area, message);
}
