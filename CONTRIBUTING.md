# Contributing

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [PowerShell 7+](https://github.com/PowerShell/PowerShell) (`pwsh`) — for publish scripts
- [Docker](https://www.docker.com/) — optional, for Docker image testing

## Building and running locally

```sh
# Debug run (reads settings.json from the project directory)
dotnet run --project src/Bennewitz.Ninja.FileServer

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
