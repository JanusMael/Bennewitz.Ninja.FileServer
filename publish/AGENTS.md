# AGENTS.md — `publish/`

PowerShell 7 scripts, run as `pwsh publish/<script>.ps1` from any directory. Output goes to
`dist/` and `local-feed/` here, both gitignored.

| Script | What it does | Run by |
|---|---|---|
| `publish.ps1` | Wipes `dist/` and every `bin/` and `obj/` under `src/`, then runs `Publish-Rid.ps1` for each RID, prompting unless `-All`; exits non-zero if any RID failed | `release.yml`, with `-All` |
| `Publish-Rid.ps1` | Publishes the CLI host self-contained and single-file for one RID, adds `README.md`, `LICENSE`, `THIRD-PARTY-NOTICES.md` and `docker/`, then archives it: `.zip` for `win-*`, `.tar.gz` otherwise | `publish.ps1`; the `publish-<rid>.ps1` wrappers |
| `publish-<rid>.ps1` | One per RID: `Publish-Rid.ps1 -Rid <rid> -Clean` | a developer |
| `Smoke-Test.ps1` | Publishes for the host RID, starts the binary on port `15550`, and asserts `GET /` returns `302` | CI's `build` job |
| `Pack-Local.ps1` | Packs the component into `local-feed/` at a fresh prerelease version and evicts it from the global packages folder | a developer, before running `samples/` |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **Every script publishes `src/Bennewitz.Ninja.FileServer.Cli`**, never the library | The library is not an executable; publishing it fails with `NETSDK1099` | CI's `Smoke-test the published binary` step |
| The RID list is the same `ValidateSet` in `publish.ps1` and `Publish-Rid.ps1`, and a wrapper exists for each | A RID added in one place is refused by the other | nothing |
| bin and obj are wiped by hand, never by `dotnet clean` | After a publish for another RID, `dotnet clean` fails with `NETSDK1047` | the comment in `publish.ps1` |
| Linux and macOS archives are written by `TarWriter` with mode `0755` | An archive written on NTFS loses the execute bit | `Publish-Rid.ps1` |
| A local pack's version is SemVer with a letter-prefixed time, `-local.tHHmm` | A digits-only identifier with a leading zero fails restore with an error that names neither | `Pack-Local.ps1` validates the version before packing |
| A version passed to a pack goes in as `PublicVersion` | The library maps `PublicVersion` to the NuGet version, and the version generator reads it for the assembly | `Pack-Local.ps1`; `release.yml` |
