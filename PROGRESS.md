# Progress

## On `main`, not yet released

Build and CI changes from `plans/00004` in Bennewitz.Ninja.Templates, the family's standard build
properties. None changes what a user of the package or the server sees.

- `IsContinuousIntegration` is gone and AutoVersioning is `2026.3.916` (`1f16364`).
- Central package management (`f10c674`): every version is in `Directory.Packages.props`, and the
  sample keeps its `$(FileServerVersion)` through a conditional `PackageVersion`; the tests gain
  AutoVersioning. Every package resolves as before. Both Dockerfiles copy `Directory.Packages.props`
  before the restore (`762cdd9`); CI builds only `Dockerfile`, so `Dockerfile.bundle`'s copy of the
  fix is unbuilt.
- `scripts/repo-conventions.cs` evaluates every project against the family's build properties
  (`f10c674`, `514fec0`).
- The `nuget` topic is required only where `packages.push` names an id: `scripts/repo-conventions.cs` is
  the template's current copy again (Templates `fb6961a`).

**The library is deliberately not marked trimmable.** Measured for step 7 of that plan: with its three
`MapGet(string, Delegate)` calls moved to `RequestDelegate` handlers, the trim analyzer and an ILLink
pass report nothing. But a consumer publishing with `TrimMode=partial` then trims the library and
keeps each compiled Razor view without its constructor, and every listing and Markdown page answers
500. An embedded `ILLink.Descriptors.xml` preserving `AspNetCoreGeneratedDocument` fixes that, but
only a trimmed publish that serves a page can prove it stays fixed, and the library brings in MVC,
which is not supported trimmed. Marking it would need both, and a CI job to hold them.

## Completed — [plan 00001](plans/00001-unlisted-files.md): unlisted files and sensitive-path refusal

All eight steps are implemented and verified. Merged in
[#10](https://github.com/JanusMael/Bennewitz.Ninja.FileServer/pull/10) and released as 2026.9.23.
No work is in progress.

| Step | Status | Verified by |
| --- | --- | --- |
| 1. Pin listing behaviour | Done | Three listing tests passed against the unchanged code. |
| 2. Refuse sensitive paths | Done | 13 `SensitivePathTests` red before the fix, green after, parity test included. |
| 3. Pattern options | Done | `GlobPatternTests`. |
| 4. Unlisted filter | Done | `UnlistedPatternTests` (listing-provider cases). |
| 5. Exposed sensitive patterns | Done | `ExposedSensitivePatternTests`. |
| 6. End-to-end behaviour | Done | `UnlistedPatternTests` (request cases). Full suite on Windows: 159/159, none skipped. On Linux (WSL, ext4): 152 passed, 7 Windows-only skipped, 0 failed. |
| 7. CLI | Done | CLI run against a fixture with both flags; every listing and status the plan names. |
| 8. Docs and changelog | Done | Both READMEs, `CHANGELOG.md`, `CONTRIBUTING.md`; Release build with `-warnaserror` clean. |

## Decisions recorded during implementation

Where the build departed from the approved plan's wording. The plan stays as approved.

- **8.3 aliases are refused, not read back to their long name (6f).** .NET's directory
  enumeration matches long names only, so a lookup by alias finds nothing. Any `~` segment that
  reaches an entry only through its alias is therefore refused, even for an ordinary file. This
  fails closed; such a URL is never one the component generates.
- **Test placement.** The step 3–6 tests live in `GlobPatternTests`, `UnlistedPatternTests`,
  `SensitivePathTests` and `ExposedSensitivePatternTests`, rather than being added to
  `MountRegistrationTests`, `ExtensionFilterTests` and `MountRequestTests`. The coverage is the
  plan's.
- **Parity with exposure** runs with `.well-known` and `.well-known/**` both exposed. With the
  contents pattern alone, the directory is refused and cannot list what it serves (decision 6e),
  which the listed-exactly-when-served helper would report as a mismatch by design.
- **Raw versus canonical exposure matching is an equivalent mutant for the tested inputs.**
  `Matcher` itself collapses `..` and treats `\` as a separator. Canonical matching is kept as
  defence in depth.
