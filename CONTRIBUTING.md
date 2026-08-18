# Contributing

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`) — for publish scripts
- [Docker](https://www.docker.com/) — optional, for Docker image testing

## Building and running locally

```sh
# Debug run (reads settings.json from the project directory)
dotnet run --project src/Bennewitz.Ninja.FileServer.Cli

# Release build
dotnet build -c Release
```

Create a `settings.json` next to the project (or at the repo root when using `dotnet run`):

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

## Publishing a local binary

```sh
# Single RID — outputs to publish/dist/
pwsh publish/Publish-Rid.ps1 -Rid win-x64 -Clean

# All RIDs interactively
pwsh publish/publish.ps1

# All RIDs unattended (CI mode)
pwsh publish/publish.ps1 -All
```

## Code style

- C# uses the existing nullable and implicit-usings settings; match the surrounding style.
- `TreatWarningsAsErrors` is enabled — the build must produce zero warnings.
- No new public API surface unless the feature genuinely requires it.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your changes; ensure `dotnet build -c Release` passes cleanly.
3. Open a pull request against `main` with a clear description of what changed and why.

## Reporting issues

Open an issue on GitHub. Include the platform, binary version (run `FileServer --help` to see it), and steps to reproduce.
