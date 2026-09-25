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
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

string? verb = args.Length > 0 ? args[0] : null;
if (verb is not ("check" or "apply"))
{
    return Usage("the first argument must be 'check' or 'apply'.");
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

bool remoteTree = repoName is not null && root is null;
string localRoot = Path.GetFullPath(root ?? Directory.GetCurrentDirectory());
repoName ??= Environment.GetEnvironmentVariable("GITHUB_REPOSITORY") is { Length: > 0 } fromCi
    ? fromCi
    : offline ? null : Gh.RepoOf(localRoot);

if (repoName is null && !offline)
{
    return Usage("could not tell which repository this is; pass --repo OWNER/NAME.");
}

Api api = new(repoName ?? "", fixtures);
(int repoStatus, string repoBody) = offline ? (0, "") : api.Get("");
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
        config = Config.Parse(configText, findings);
    }
    catch (JsonException error)
    {
        findings.Add(Finding.Fail("repository.json", ".github/repository.json is not valid JSON: " + error.Message));
    }
}

return verb == "apply" ? Apply() : Check();

int Check()
{
    Documentation.Check(tree, config, findings);

    if (offline)
    {
        if (config is not null)
        {
            Workflows.CheckRequired(tree, config.RequiredChecks, findings);
        }
        Props.Check(localRoot, tree, config, repoName, remoteTree, findings);
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
        return Report();
    }

    string[] liveTopics = [.. (live["topics"]?.AsArray() ?? []).Select(t => t!.GetValue<string>()).Order(StringComparer.Ordinal)];
    foreach (string missing in Baseline.Topics.Except(liveTopics))
    {
        findings.Add(Finding.Fail("topics", $"GitHub's topics lack \"{missing}\", which every family repository carries."));
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
    Props.Check(localRoot, tree, config, repoName, remoteTree, findings);
    Drift.Check(tree, remoteTree, findings);
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
    foreach (JsonObject ruleset in Baseline.Rulesets(config.RequiredChecks))
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

    string depth = offline ? "offline" : release ? "release" : admin ? "admin" : "token";
    string subject = repoName ?? localRoot;
    Console.WriteLine(failures == 0
        ? $"repo-conventions: {subject} conforms ({verb}, {depth} depth)."
        : $"repo-conventions: {subject} has {failures} finding(s) ({verb}, {depth} depth).");
    return failures == 0 ? 0 : 1;
}

static int Usage(string problem)
{
    Console.Error.WriteLine("repo-conventions: " + problem);
    Console.Error.WriteLine("usage: repo-conventions (check [--admin | --release | --offline] | apply [--dry-run] [--replace-branch-protection]) [--repo OWNER/NAME] [--root DIR] [--api-fixtures DIR]");
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

    public static readonly string[] Topics = ["csharp", "dotnet", "nuget"];

    /// <summary>GitHub's built-in repository-admin role.</summary>
    public const int AdminRoleId = 5;

    public static IEnumerable<JsonObject> Rulesets(IReadOnlyList<string> requiredChecks)
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

        yield return Ruleset("main", "branch", "~DEFAULT_BRANCH", mainRules);
        yield return Ruleset("release-tags", "tag", "refs/tags/v*", new JsonArray(Rule("creation"), Rule("update"), Rule("deletion")));
    }

    private static JsonObject Ruleset(string name, string target, string include, JsonArray rules) => new()
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
            ["ref_name"] = new JsonObject { ["include"] = Json.Array([include]), ["exclude"] = new JsonArray() },
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
    bool TrimmingRequired)
{
    public static Config Parse(string text, List<Finding> findings)
    {
        JsonObject json = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("the document is empty.");

        string description = json["description"]?.GetValue<string>() ?? "";
        if (description.Trim().Length == 0)
        {
            findings.Add(Finding.Fail("repository.json", "\"description\" is empty. One sentence saying what the repository is and which packages it ships."));
        }

        string[] topics = Strings(json["topics"]);
        foreach (string missing in Baseline.Topics.Except(topics))
        {
            findings.Add(Finding.Fail("repository.json", $"\"topics\" lacks \"{missing}\", which every family repository carries."));
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

        string trimming = json["trimming"]?.GetValue<string>() ?? "";
        if (trimming is not ("" or "required"))
        {
            findings.Add(Finding.Fail("repository.json", $"\"trimming\" is \"{trimming}\"; it is \"required\", or absent while a repository works towards it."));
        }

        return new Config(
            description,
            json["homepage"]?.GetValue<string>() ?? "",
            topics,
            Strings(json["requiredChecks"]),
            [.. Strings(json["content"]).Select(p => p.TrimEnd('/') + "/")],
            undocumented,
            propsExempt,
            trimming == "required");
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

        foreach (JsonObject wanted in Baseline.Rulesets(config?.RequiredChecks ?? []))
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
static class Props
{
    public const string AutoVersioningFloor = "2026.3.916";
    private const string AutoVersioning = "Bennewitz.Ninja.AutoVersioning";

    private static readonly string[] Names =
    [
        "TargetFramework", "TargetFrameworks", "Nullable", "ImplicitUsings", "TreatWarningsAsErrors",
        "ManagePackageVersionsCentrally", "GenerateAutoVersionedAssemblyInfo", "AssemblyCompany",
        "IsContinuousIntegration", "IsPackable", "OutputType", "IsTestProject", "PackAsTool",
        "IsRoslynComponent", "PackageType", "Authors", "PackageLicenseExpression", "RepositoryUrl",
        "PackageReadmeFile", "DebugType", "IsTrimmable", "EnableTrimAnalyzer",
    ];

    private static readonly Dictionary<string, string> Ci = new(StringComparer.Ordinal) { ["GITHUB_ACTIONS"] = "true" };

    public static void Check(string root, Tree tree, Config? config, string? repoName, bool remoteTree, List<Finding> findings)
    {
        if (remoteTree)
        {
            findings.Add(Finding.Note("props", "Build properties are evaluated from a checkout, after a restore; `check --repo` cannot reach them."));
            return;
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
                return;
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
            return;
        }

        foreach (string target in solution is not null ? [solution] : projects)
        {
            (int exit, string output) = Shell.Run("dotnet", root, null, Ci, "restore", target, "--nologo", "-v", "quiet");
            if (exit != 0)
            {
                findings.Add(Finding.Fail("props", $"`dotnet restore {target}` failed, so no property could be evaluated: {Tail(output)}"));
                return;
            }
        }

        IReadOnlyDictionary<string, string> none = new Dictionary<string, string>();
        List<string> roles = [];
        foreach (string project in projects)
        {
            string name = Path.GetFileNameWithoutExtension(project.Replace('\\', '/'));
            string[] arguments = ["msbuild", project, "-p:Configuration=Release", .. Names.Select(n => "-getProperty:" + n),
                "-getItem:PackageReference", "-getItem:PackageVersion"];
            (int exit, string output) = Shell.Run("dotnet", root, null, Ci, arguments);
            JsonObject? evaluated = exit == 0 ? Parse(output) : null;
            if (evaluated?["Properties"] is not JsonObject properties)
            {
                findings.Add(Finding.Fail("props", $"{name} could not be evaluated: {Tail(output)}"));
                continue;
            }

            string role = Role(properties);
            roles.Add($"{name} ({role})");
            IReadOnlyDictionary<string, string> exempt = config?.PropsExempt.GetValueOrDefault(name) ?? none;

            foreach ((string rule, string? problem) in Rules(properties, evaluated["Items"], role, repoName))
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

            if (role == "library" && !(Value(properties, "IsTrimmable") == "true" && Value(properties, "EnableTrimAnalyzer") == "true")
                && !exempt.ContainsKey("Trimming"))
            {
                string message = $"{name} is not yet trimmable: it needs IsTrimmable and EnableTrimAnalyzer, as the template's src/Directory.Build.props sets them.";
                findings.Add(config?.TrimmingRequired == true
                    ? Finding.Fail("props", message + " repository.json requires trimming.")
                    : Finding.Note("props", message + " repository.json's \"trimming\": \"required\" makes this fail."));
            }
        }

        findings.Add(Finding.Note("props", $"{projects.Count} projects evaluated after a restore, as CI evaluates them: {string.Join(", ", roles)}."));
    }

    /// <summary>
    /// The first role that matches, in this order. ⚠ The order matters: an xUnit v3 test project is a
    /// non-packable executable, and would read as an app if <c>app</c> were tested first.
    /// </summary>
    private static string Role(JsonObject p) =>
        Value(p, "PackageType") == "Template" ? "template"
        : Value(p, "IsRoslynComponent") == "true" ? "analyzer"
        : Value(p, "IsTestProject") == "true" ? "test"
        : Value(p, "PackAsTool") == "true" ? "tool"
        : Value(p, "IsPackable") == "true" ? "library"
        : Value(p, "OutputType") is "Exe" or "WinExe" ? "app"
        : "other";

    /// <summary>Every rule for this role, each with its problem, or null where the project meets it.</summary>
    private static IEnumerable<(string Rule, string? Problem)> Rules(JsonObject p, JsonNode? items, string role, string? repoName)
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
        yield return Expect(p, "PackageReadmeFile", "README.md");
        yield return Expect(p, "DebugType", "embedded");
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

    private static string Value(JsonObject p, string name) => Text(p[name]);

    private static string Text(JsonNode? node) => node is JsonValue value && value.TryGetValue(out string? text) ? text ?? "" : "";

    private static string Tail(string output)
    {
        string[] lines = [.. output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];
        return lines.Length == 0 ? "no output" : string.Join(" | ", lines.TakeLast(3));
    }
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
