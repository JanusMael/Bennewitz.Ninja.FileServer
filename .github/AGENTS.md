# AGENTS.md — `.github/`

This repository's workflows and settings.

| File | What it is |
|---|---|
| `workflows/ci.yml` | On every push and pull request to `main`. `build`: restore, build, test, pack the component into `publish/local-feed`, build `samples/SampleWebApp`, then `publish/Smoke-Test.ps1`. `docker`: builds `docker/Dockerfile`, runs it, and checks the listing, a rendered `.md`, uid `1654` and `/app/THIRD-PARTY-NOTICES.md`. `conventions`: `repo-conventions check` |
| `workflows/release.yml` | On a `v*` tag: `publish/publish.ps1 -All`, packs the component, pushes it to nuget.org, and creates the GitHub Release with the archives and the `.nupkg`. Dispatched by hand, it only logs in to nuget.org and reports the account |
| `repository.json` | Description, topics and `requiredChecks`, the job names the `main` ruleset requires |
| `copilot-instructions.md` | A pointer to the root `AGENTS.md` |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **A job named in `requiredChecks` is never renamed alone.** Rename it in `repository.json` too and run `repo-conventions apply` in the same change | A required check that no job reports blocks every pull request, and GitHub never says why | `repo-conventions check` refuses a `requiredChecks` entry no job reports |
| **`release.yml` keeps its file name**, and the preflight stays in it | The trusted-publishing policy on nuget.org names `release.yml`, and the OIDC token is bound to the workflow file | `CONTRIBUTING.md`, "Publishing credentials" |
| `NuGet/login` stays immediately before `Push the package to NuGet.org` | The token it returns is short-lived and expires across a slow step | the comment on `Log in to NuGet.org (OIDC)` |
| Every publishing step is gated on `RELEASING`, true only for a tag push | A manual run would publish instead of only proving the credentials | `release.yml`, `env` |
| `permissions` keeps `id-token: write` and `contents: write` | Without the first the login returns 403; without the second the GitHub Release cannot be created | `release.yml`, `publish` job |
| An unset `NUGET_USER` secret skips the push, never the release | A fork, or a repository without a policy, still gets a complete GitHub Release | `release.yml`, `Push the package to NuGet.org` |
| `docker` asserts on the running container, not on the build | The image has broken in ways the .NET build cannot see: a base image dropping a tool, an entry point naming a library | `ci.yml`, `Run it and check it serves` |
