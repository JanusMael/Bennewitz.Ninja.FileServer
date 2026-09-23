# Progress

## Current work — [plan 00001](plans/00001-unlisted-files.md): unlisted files and sensitive-path refusal

Branch `feat/unlisted-patterns`. All eight steps are implemented and verified; not yet committed
beyond the plan itself.

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
