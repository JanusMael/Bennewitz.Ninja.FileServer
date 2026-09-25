# AGENTS.md — `tests/`

One project, `Bennewitz.Ninja.FileServer.Tests`: xunit.v3 on Microsoft.Testing.Platform, an
executable selected by `global.json`, with a `ProjectReference` to
the component and access to its internals. The CLI has no tests; `CONTRIBUTING.md`, "Tests", says
why.

| Class | What it guards |
|---|---|
| `FileServerPathTests` | Containment: traversal, absolute paths, name-prefix siblings, case, symlinked leaves and intermediate directories |
| `SensitivePathTests` | Refusal of dot-prefixed, Hidden and System paths, and of `..`, backslash, alternate-data-stream and 8.3 forms |
| `ExposedSensitivePatternTests`, `UnlistedPatternTests`, `GlobPatternTests` | The two pattern options: anchoring, case, precedence under `AllowedExtensions`, normalisation |
| `ExtensionFilterTests` | `AllowedExtensions` on the listing provider and the download path, and its normaliser |
| `MountRegistrationTests` | Prefix normalisation and the conflicts `FileServerMountRegistry` refuses |
| `MountRequestTests`, `MountAuthorizationTests` | Requests through a `TestServer`: listings, Markdown, headers, assets, and `RequireAuthorization` covering downloads |
| `MarkdownHighlightingTests` | Fenced-code tokenising and escaping |
| `Packaging/PackagingTests` | Every packable project under `src/` is in `packages.push` or `packages.local`, and `release.yml` globs nothing and takes its ids from `packages.push` |
| `Infrastructure/` | `FileServerTestHost`, `TempDirectory`, `TestAuthenticationHandler`, and `SymlinkFact`, `WindowsFact`, `ShortNameFact` |

## Rules

| Rule | Why |
|---|---|
| **Containment and refusal tests use the real filesystem**, through `TempDirectory` | Whether a path escapes is a property of the filesystem; an abstraction can answer differently |
| Every refusal has a positive twin: a sibling that still returns 200, or a named entry still listed | A suite of refusals alone passes when everything is refused |
| A platform-dependent test uses `SymlinkFact`, `WindowsFact` or `ShortNameFact`, which skip where the capability is absent | A skip is reported, where an early return would read as a pass. Read the skip count before concluding a change is safe |
| A custom `Fact` attribute takes `[CallerFilePath]` and `[CallerLineNumber]` and passes them to the base | xunit.v3 reports a test's source location through them; the build fails `xUnit3003` without | the build |
| Every call that accepts a `CancellationToken` in a test passes `Cancel`, the class's `TestContext.Current.CancellationToken` | A cancelled run stops at the next await instead of finishing each request | the build: `xUnit1051` |
| A new guard is proven by breaking the rule it guards and watching it fail | `CONTRIBUTING.md`, "Tests", names the mutation and the failing test for each rule |
| The parity tests `EveryFileIsListedExactlyWhenItIsServed` and `EveryFileIsListedExactlyWhenItIsServed_WithExposure` stay | They are what pins listing and download to one rule |
