# AGENTS.md — `scripts/`

File-based C# apps, run with `dotnet run --file`. Each compiles with this repository's
`Directory.Build.props`, so warnings are errors.

| Script | What it does | Run by |
|---|---|---|
| `repo-conventions.cs` | Checks and applies the family's repository conventions: the documents, `repository.json`, the GitHub settings and rulesets | CI's `conventions` job, `check` |

```bash
dotnet run --file scripts/repo-conventions.cs -- check
dotnet run --file scripts/repo-conventions.cs -- check --admin
dotnet run --file scripts/repo-conventions.cs -- apply --dry-run
dotnet run --file scripts/repo-conventions.cs -- apply
```

`check --admin` and `apply` need the maintainer's `gh` login.

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **`repo-conventions.cs` is a copy and is never edited here.** The canonical file is `templates/bbpkg/scripts/repo-conventions.cs` in Bennewitz.Ninja.Templates; a change goes there and is copied over this one | Every family repository runs the same check | `check --repo` run from Bennewitz.Ninja.Templates reports a copy that differs |
| Run a script with `--file` | It keeps working if a project is ever added at the root, where a bare `dotnet run <file.cs>` binds to the project | `ci.yml`, `conventions` job |
| No project at the root compiles this directory | An SDK project's default glob takes every `.cs` beneath it and fails with `CS9314` on the `#!` line | the projects live under `src/`, `tests/` and `samples/` |
