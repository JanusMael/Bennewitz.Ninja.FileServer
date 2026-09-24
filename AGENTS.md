# AGENTS.md — Bennewitz.Ninja.FileServer

> For anyone changing this repository, human or agent. The invariants a change must not break, the
> commands, and the checklists for recurring work. Each top-level directory has an `AGENTS.md` of
> its own for what only its files show. Work state is [`PROGRESS.md`](PROGRESS.md). What every
> repository in this family carries, and how it is checked, is prescribed in
> [`docs/repository-conventions.md`](https://github.com/JanusMael/Bennewitz.Ninja.Templates/blob/main/docs/repository-conventions.md)
> in Bennewitz.Ninja.Templates.

## What this repository is

A file browser for ASP.NET Core, shipped three ways from one component:

| Form | Built from | Published by `release.yml` as |
|---|---|---|
| NuGet package `Bennewitz.Ninja.FileServer`: `AddFileServer()`, then `MapFileServer(prefix, …)` | `src/Bennewitz.Ninja.FileServer` | the `.nupkg`, pushed to nuget.org through trusted publishing |
| Standalone server, a self-contained single-file binary per RID | `src/Bennewitz.Ninja.FileServer.Cli` | `.zip` and `.tar.gz` archives on the GitHub Release |
| Container images, volume-mounted or with content baked in | `docker/` | nothing: CI builds and runs the image, and no job pushes one |

The server is not a dotnet tool: the CLI project sets `IsPackable` to `false`. What it does for a
user is in [`README.md`](README.md); how to build, test and release is in
[`CONTRIBUTING.md`](CONTRIBUTING.md); what shipped when is in [`CHANGELOG.md`](CHANGELOG.md).

## Layout

| Directory | What it holds |
|---|---|
| `src/` | The component library and the CLI host that consumes it |
| `tests/` | `Bennewitz.Ninja.FileServer.Tests`, for the component |
| `samples/` | `SampleWebApp`, which installs the component from a package; not in the solution |
| `publish/` | PowerShell scripts: per-RID publish, local pack, and the boot smoke test |
| `docker/` | The standard and bundled-content Dockerfiles and the bundle build scripts |
| `scripts/` | `repo-conventions.cs`, the family's conventions check |
| `plans/` | Numbered plans; approved ones are left as approved |
| `.github/` | The CI and release workflows, and `repository.json` |

## Invariants

| Invariant | Failure if broken | Guarded by |
|---|---|---|
| Files are served by `Handle` from the mount's route group, never by static-file middleware | `RequireAuthorization()` on the group protects listings while downloads stay open | `MountAuthorizationTests.RequireAuthorization_UnauthenticatedFileDownload_IsChallenged` |
| The asset endpoint is mapped outside the group, with `AllowAnonymous()` | The login page a visitor is sent to renders unstyled | `MountAuthorizationTests.RequireAuthorization_AssetEndpoint_StaysAnonymous` |
| Containment resolves links on every segment and compares ordinally: `FileServerPath.TryResolveWithin` | A symlinked intermediate directory, or a case variant, escapes the mount root | `FileServerPathTests` |
| Listing and download apply the same rules: `SensitivePathPolicy` and `FileServerMount.IsAllowed` are consulted by both `Handle` and `ListingFileProvider` | A file hidden from listings still downloads by URL | `SensitivePathTests.EveryFileIsListedExactlyWhenItIsServed`; `MountRequestTests.Mount_AllowedExtensions_HidesAndRefusesFilteredFiles` |
| `ExposedSensitivePatterns` matches with `Ordinal`, `UnlistedPatterns` with `OrdinalIgnoreCase` | On a case-sensitive file system an exposure pattern admits a directory it does not name | `ExposedSensitivePatternTests.Exposure_IsCaseSensitive`; `UnlistedPatternTests.Matching_IgnoresCase` |
| A mount whose prefix or root collides with another fails at registration | Two mounts with different policies answer for the same files | `FileServerMountRegistry.Register`; `MountRegistrationTests` |
| The CLI uses only the public API; `InternalsVisibleTo` names the test assembly alone | The executable can do what a package consumer cannot | `Bennewitz.Ninja.FileServer.csproj` |
| The library's `AssemblyName` is `Bennewitz.Ninja.FileServer.Hosting`; the CLI's `PackageId` is `Bennewitz.Ninja.FileServer.Cli` | Restore fails with "Ambiguous project name", or two assemblies collide in one output folder | both `.csproj` files; CI's `Pack` step |
| The version is the tag, `vYYYY.M.D`, passed in as `PublicVersion` | Every package is published as `1.0.0`, and a version on nuget.org can never be replaced | `Version` in `Bennewitz.Ninja.FileServer.csproj`; `release.yml`, step `Extract version from tag` |
| `THIRD-PARTY-NOTICES.md` travels in the package, every archive and both images | The vendored `github-markdown.min.css` is redistributed without its MIT notice | the library `.csproj`; `Publish-Rid.ps1`; both Dockerfiles; CI's `docker` job |
| A local `settings.json` is never published or committed | A release archive leaks a machine's paths | `CopyToPublishDirectory` `Never` in the CLI `.csproj`; `.gitignore` |
| Warnings are errors | A warning ships, including a missing XML doc on public API | `Directory.Build.props` |
| The repository meets the family conventions | Documentation or settings drift unnoticed | `scripts/repo-conventions.cs`, run by CI's `conventions` job |

## Commands

```bash
dotnet restore
dotnet build -c Release --no-restore
dotnet test -c Release --no-build
dotnet pack src/Bennewitz.Ninja.FileServer -c Release --no-build -o publish/local-feed
dotnet build samples/SampleWebApp -c Release
pwsh publish/Smoke-Test.ps1
dotnet test --filter FullyQualifiedName~SensitivePathTests
pwsh publish/Pack-Local.ps1
pwsh publish/publish.ps1 -All
docker build -f docker/Dockerfile -t fileserver:ci .
dotnet run --file scripts/repo-conventions.cs -- check
gh workflow run release.yml
```

- The first seven lines are CI's `build` job in order. Tests run on VSTest (`Microsoft.NET.Test.Sdk`).
- `gh workflow run release.yml` is the credential preflight: it logs in to nuget.org and stops.
- Write `-p:` rather than `/p:`: Git Bash on Windows rewrites a leading-slash argument into a path.

## Checklists

**Releasing:** `CONTRIBUTING.md`, "Releasing". Move `[Unreleased]` in `CHANGELOG.md` under the new
version and update its link definitions, tag `vYYYY.M.D` and push. Once the package is live on
nuget.org, bump `FileServerVersion` in `samples/SampleWebApp/SampleWebApp.csproj`, never before.

**Adding a mount option:** the property on `FileServerMountOptions` with XML docs; its
normalisation in `MapFileServer`; the CLI's `settings.json` key, `FILE_SERVER_*` variable and
flag in `Settings`, assigned in `Program`; the configuration table in `README.md`, the package's
`src/Bennewitz.Ninja.FileServer/README.md`, and `CHANGELOG.md`.

**Changing containment, the extension filter or the sensitive-path rule:** break the rule on
purpose and watch the suite go red before fixing it. `CONTRIBUTING.md`, "Tests", names the
mutation and the test that must fail for each.

**Vendoring third-party material:** add its notice to `THIRD-PARTY-NOTICES.md` in the same change.

**Adding a top-level directory:** give it an `AGENTS.md` and a `CLAUDE.md` containing `@AGENTS.md`,
or exempt it in `.github/repository.json` under `undocumented`, with the reason. CI fails until
one of the two is done.

**Every change:** update `PROGRESS.md` in the same commit, and `CHANGELOG.md` under `[Unreleased]`
when a user would notice. Commits are Conventional Commits.
