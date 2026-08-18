# Contributing

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`) — for publish scripts
- [Docker](https://www.docker.com/) — optional, for Docker image testing

## Repository layout

| Path | What it is |
| --- | --- |
| `src/Bennewitz.Ninja.FileServer` | The component. A Razor class library packed as the `Bennewitz.Ninja.FileServer` NuGet package; its assembly is named `…​.Hosting` so it can sit beside the executable in one output folder. |
| `src/Bennewitz.Ninja.FileServer.Cli` | The standalone server. Translates `settings.json`, environment variables, and CLI arguments into one `MapFileServer` call and does nothing the package cannot do. |
| `tests/Bennewitz.Ninja.FileServer.Tests` | Tests for the component, including the containment rules. |
| `samples/SampleWebApp` | A host that installs the component **from a package**, not a project reference. Not in the solution: it needs a packed local feed to restore. |
| `publish/` | Single-file publish per RID, the local package feed, and the boot smoke test. |
| `docker/` | Both images and their build scripts. |

## Building and running locally

```sh
# Debug run (reads settings.json from the project directory)
dotnet run --project src/Bennewitz.Ninja.FileServer.Cli

# Release build
dotnet build -c Release
```

Create a `settings.json` in the CLI project directory. It is copied to the build output, which
is where the app reads it from — configuration is resolved next to the application, not from the
working directory:

```json
{
  "ServedFilesRoot": "/path/to/your/files",
  "HttpPort": 5550
}
```

## Tests

```sh
# Whole suite
dotnet test

# One area
dotnet test --filter FullyQualifiedName~FileServerPathTests
```

The suite covers the component library. The CLI host has none of its own by design: it
translates settings into a single `MapFileServer` call, so its behaviour is the component's.

Three things worth knowing before adding to it:

- **Containment tests use the real filesystem.** Whether a path escapes a root is a property of
  the filesystem, and an abstraction can answer differently from the thing being protected.
  `TempDirectory` creates and cleans up real directories.
- **Symlink tests skip themselves where links cannot be created.** `[SymlinkFact]` probes the
  capability once by trying it; on Windows without Developer Mode or elevation those tests
  report as skipped rather than failing. Check the skip count before concluding a change is safe.
- **Refusal tests need a positive twin.** A containment suite that only asserts refusals passes
  just as well when everything is refused, so each rule has a matching test that something
  legitimate still works — a traversal that returns inside the root, a sibling directory sharing
  a name prefix, an unprotected mount served anonymously.

When changing containment or the extension filter, break the rule on purpose and confirm the
suite goes red before fixing it. Removing link resolution from `FileServerPath` should fail four
tests; removing the download-path extension check should fail
`Mount_AllowedExtensions_HidesAndRefusesFilteredFiles`. A test that survives its own mutation is
not protecting anything.

## Testing the package locally

A project reference proves less than it looks: the compiler sees types the package might not
actually ship. To exercise the component the way a consumer installs it:

```sh
# Pack into publish/local-feed — a fresh prerelease version each time
pwsh publish/Pack-Local.ps1

# Run the sample host, which restores from that feed
dotnet run --project samples/SampleWebApp
```

`samples/SampleWebApp` mounts the component four times — default styling, inside the host's
layout, with an extension filter, and behind `RequireAuthorization` — so one run covers the
claims that matter. See [samples/README.md](samples/README.md).

Two things that will otherwise cost you an afternoon:

- **NuGet caches by id and version.** Re-packing the same version leaves consumers building
  against the previous bits. `Pack-Local.ps1` stamps a new prerelease each run and evicts the
  cached copies, so this cannot bite silently.
- **A prerelease identifier of only digits may not have a leading zero.** SemVer calls it a
  numeric identifier; NuGet reports the violation as `RestoreTask returned false but did not log
  an error`, which names neither the version nor the rule. The script's default timestamp is
  prefixed with a letter for this reason.

## Publishing a local binary

```sh
# Single RID — outputs to publish/dist/
pwsh publish/Publish-Rid.ps1 -Rid win-x64 -Clean

# All RIDs interactively
pwsh publish/publish.ps1

# All RIDs unattended (CI mode)
pwsh publish/publish.ps1 -All
```

## Releasing

Versions follow a `YYYY.M.D` calendar scheme, and the tag is the source of truth: the workflow
passes it in as `PublicVersion`, which becomes both the assembly version and the NuGet package
version. Nothing needs editing in a `.csproj` to cut a release.

1. Merge to `main`.
2. Move the `[Unreleased]` entries in `CHANGELOG.md` under a new `## [YYYY.M.D] — YYYY-MM-DD`
   heading, and update the two link definitions at the bottom of the file.
3. Tag and push:

   ```sh
   git tag v2026.8.18
   git push origin v2026.8.18
   ```

The `Release` workflow then publishes all six single-file binaries, packs the component, pushes
it to NuGet.org, and creates the GitHub Release with the archives and the `.nupkg` attached.

### Publishing credentials

Publishing uses [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing):
GitHub mints a short-lived OIDC token that NuGet.org exchanges for a publish token, so there is
no long-lived API key in the repository to rotate or leak. Two things have to line up:

- A policy at [nuget.org/account/trustedpublishing](https://www.nuget.org/account/trustedpublishing)
  naming repository owner `JanusMael`, repository `Bennewitz.Ninja.FileServer`, and workflow file
  `release.yml`. The filename must match exactly — it is how NuGet.org identifies the caller.
- A repository secret `NUGET_USER` holding the **nuget.org profile name**, not an email address.
  It is not a credential; it only says which account the token is exchanged against.

Without `NUGET_USER` the push step is skipped and everything else still runs, so tagging from a
fork — or before the policy exists — produces a complete GitHub Release rather than a failure.

To add an approval gate before anything reaches NuGet.org, create a `release` environment with
required reviewers, add `environment: release` to the `publish` job, and set the same environment
name on the nuget.org policy.

## Code style

- C# uses the existing nullable and implicit-usings settings; match the surrounding style.
- `TreatWarningsAsErrors` is enabled — the build must produce zero warnings.
- No new public API surface unless the feature genuinely requires it.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your changes; ensure `dotnet build -c Release` and `dotnet test` both pass cleanly.
3. Open a pull request against `main` with a clear description of what changed and why.

## Reporting issues

Open an issue on GitHub. Include the platform, binary version (run `FileServer --help` to see it), and steps to reproduce.
