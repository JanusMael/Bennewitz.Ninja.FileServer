# 00001 — Unlisted files, and refusing sensitive paths

> Status: **approved 2026-09-23**. Supersedes nothing.

A mount can make a file invisible only by refusing it (`AllowedExtensions`) or by switching off
browsing for the whole mount (`EnableDirectoryBrowsing = false`). This plan adds a per-mount set of
glob patterns whose matches are left out of directory listings but still answer at their exact URL.

It also closes a gap found along the way. Dot-prefixed entries, and entries with the Hidden or System
attribute, are left out of listings, because `PhysicalFileProvider`'s default `ExclusionFilters.Sensitive`
drops them. But `Handle` serves by physical path and never consults the provider, so `.env` or
`.git/config` downloads today to anyone who types its URL. No test covers either half. This plan
refuses those paths, and adds a pattern-scoped opt-out for the ones a host genuinely needs to serve,
such as `.well-known/`.

## Decisions

| # | Decision | Why |
|---|----------|-----|
| 1 | New option `FileServerMountOptions.UnlistedPatterns : IReadOnlyList<string>`, default empty. | Explicit and per-mount, like every other option; empty keeps listing behaviour exactly as it is. |
| 2 | Patterns — in both pattern options, `UnlistedPatterns` and `ExposedSensitivePatterns` — are globs matched with `Microsoft.Extensions.FileSystemGlobbing.Matcher` against the entry's path **relative to the mount root**, `/`-separated. Both share one normaliser and one validator. | Already referenced through the library's `Microsoft.AspNetCore.App` framework reference, so no new package. |
| 2b | **Case follows the direction of the effect.** `UnlistedPatterns` matches with `OrdinalIgnoreCase`, since it only ever hides. `ExposedSensitivePatterns` matches with `Ordinal`, since it widens what is served. | This is the repo's own containment rule ([FileServerPath.cs](../src/Bennewitz.Ninja.FileServer/FileServerPath.cs)): a case-insensitive comparison can admit on a case-sensitive file system, where `.WELL-KNOWN/` is a different directory from `.well-known/`, and the probe confirms `Matcher` would match it. Ordinal can only deny, and a URL this component generates always carries the on-disk casing. |
| 2a | **Patterns are anchored at the mount root.** `*.key` matches `a.key` but not `sub/a.key`; any depth is `**/*.key`. `private/**` matches what is inside `private` but not the `private` entry itself; covering both takes `private` and `private/**`. | This is `Matcher`'s behaviour, confirmed by a probe. Docs, examples and tests must state it, because a gitignore-trained reader will assume the opposite. Gitignore-style rewriting of slashless patterns to `**/pattern` was rejected: it is a second rule to explain, and it removes the plain way to target the top level. *Confirmed by the developer.* |
| 3 | An unlisted pattern can match a directory as well as a file. A matching directory is omitted from its parent's listing. Typing its URL still renders its listing (if browsing is enabled), and its contents are listed there unless they match a pattern themselves. | "Unlisted" means "not advertised", nowhere "not reachable". Anything else would make it half access control, which it must not pretend to be. *Confirmed by the developer.* |
| 4 | `AllowedExtensions` takes precedence over both pattern options. A file refused by the extension filter is 404 whether or not it is unlisted or exposed. | Neither pattern option may widen what the extension filter allows. *Confirmed by the developer.* |
| 5 | `UnlistedPatterns` affects only listings. The download path does not consult it. | `RenderDirectory` is the only place anything is enumerated (the only `GetDirectoryContents` call in `src/`). The existing file-provider wrapper is already the single place listings are filtered. |
| 6 | **Sensitive paths are refused, not merely unlisted.** A path is *sensitive* when **any segment below the mount root** is dot-prefixed, or names a file or directory with the `Hidden` or `System` attribute. A sensitive path returns 404 (files and directories alike) and is never listed, unless decision 6d exposes it. | Listing and download must agree, and `.env` / `.git/config` are the classic file-server leak. Checking only the final name would still serve `.git/config`. *Confirmed by the developer, reversing the first draft's "keep downloadable".* |
| 6a | **One rule, one implementation.** `FileServerMount` constructs `PhysicalFileProvider(ResolvedRoot, ExclusionFilters.None)`. An internal `SensitivePathPolicy` (sensitivity plus exposure) is consulted by both `Handle` and the listing wrapper. A parity test pins them together. | The provider's built-in filter cannot know about exposure. Keeping it would mean the listing and download rules are two implementations of one idea again, which is the bug being fixed. |
| 6b | Only segments below the root are checked. The root's own name and attributes are the host's choice. A non-sensitive symlink that points at a sensitive target is listed and served under its own name. | The mount root is configured deliberately. A link is an explicit act by whoever controls the directory, and judging it by its own name keeps listing and download consistent. |
| 6c | The refusal is a breaking change. The CHANGELOG records it under `Changed`, with the migration: add the path to `ExposedSensitivePatterns`, and also to `UnlistedPatterns` to keep it out of listings as before. | Honest release notes for a behaviour change that turns 200s into 404s. |
| 6d | **Opt-out, scoped by pattern:** `FileServerMountOptions.ExposedSensitivePatterns : IReadOnlyList<string>`, default empty. A sensitive path whose **full relative path** matches a pattern is treated as a normal path: listed (unless unlisted) and served (subject to `AllowedExtensions`). | Lets a root-mounted host serve `.well-known/security.txt` or ACME challenges while `.git` and `.env` stay refused. A mount-wide switch would reopen every dotfile to expose one. *Chosen by the developer.* |
| 6e | Exposure is judged on the full path, not per segment. With `.well-known/**`, `.well-known/acme-challenge/tok` is served, and so is `.well-known/.x`, since the pattern covers it. The `.well-known` directory itself stays refused and unlisted unless `.well-known` is also a pattern. | Per-segment matching would require every sensitive ancestor to match separately, so `.well-known/**` alone would expose nothing. The full-path rule plus root anchoring (2a) is one rule, already documented. |
| 6f | **The policy judges the canonical path, never the raw request string.** Before any check, the request fragment is combined with `ResolvedRoot` and canonicalised lexically with `Path.GetFullPath` (collapsing `.` and `..`, and treating `\` as a separator on Windows). It must still be within the root, and is then re-expressed relative to it and split on both separators. Links are not resolved here, which keeps 6b. On top of that: on Windows, a segment containing `:` (an alternate data stream) or any `Path.GetInvalidFileNameChars()` character is refused (this also keeps the lookup's wildcards `*`, `?`, `<`, `>` and `"` out of the long-name enumeration); the dot check uses each existing segment's **on-disk long name**, so an 8.3 alias such as `ENV~1` for `.env` is caught; and an existing entry whose attributes cannot be read is refused. | A raw-string walk is bypassable. `sub%5C.env` decodes to one segment, `sub\.env`, with no leading dot, yet `TryResolveWithin` resolves it to `.env`. Hosts other than Kestrel, and `TestServer` itself, do not normalise dot segments. Short names are generated by default on system volumes. Every one of these fails closed. |
| 7 | CLI surface for each pattern option: a `settings.json` string array, an environment variable and a flag (semicolon-delimited), with the same precedence as `AllowedExtensions`. `UnlistedPatterns` / `FILE_SERVER_UNLISTED_PATTERNS` / `--unlisted-patterns`; `ExposedSensitivePatterns` / `FILE_SERVER_EXPOSED_SENSITIVE_PATTERNS` / `--exposed-sensitive-patterns`. | Mirrors the existing extension setting so the three surfaces stay uniform. |
| 8 | Docs say plainly that unlisted is not access control. Anyone holding the URL gets the file, and URLs leak through history, referrers and logs. | Otherwise someone will use it for secrets. |

Dismissed alternatives:

- **Exact file-name list instead of globs.** Covers one file at a time, and a list of paths goes stale when files move.
- **A marker file (`.unlisted`) per directory.** Puts configuration inside the served content, where anyone who can write a file can change visibility.
- **Keeping dotfiles downloadable as a built-in "unlisted" mechanism.** It serves `.env` and `.git` by default; `UnlistedPatterns` covers the legitimate use.
- **A mount-wide `ServeSensitiveFiles` switch, or a `SensitiveEntries` Refuse/Unlisted/Visible mode.** All-or-nothing: exposing one dotfile reopens every one (decision 6d).
- **No opt-out at all.** It blocks a root-mounted host from serving `.well-known/`. *Rejected by the developer.*
- **Refusing only when the final name is dot-prefixed.** Leaves `.git/config` downloadable.
- **Hiding unlisted directories' contents recursively.** Makes a directory pattern behave differently from a file pattern for no gain, since `dir/**` already says that.

## Scope

In:

- `UnlistedPatterns` and `ExposedSensitivePatterns` options: validation and normalisation, listing filtering, and exposure.
- Refusal of sensitive paths on the request path, sharing one policy with listings (decisions 6, 6a).
- CLI settings binding for both options on all three surfaces.
- Tests: listing behaviour pinned first, the refusal written red-first, then the features.
- README option table rows, an "Unlisted files" section and a "Hidden and dot-prefixed files" section. `CHANGELOG.md` `[Unreleased]`: `Added` for the two options, `Changed` (breaking) for the refusal.

Out:

- Any other change to what is downloadable. Only decision 6 turns a 200 into a 404, and only 6d turns one back.
- Negation patterns (`!foo`). `Matcher` supports excludes, but exposing them is a separate decision.
- Hiding unlisted entries from breadcrumbs. Breadcrumbs only show the path the visitor already typed.
- Per-request or per-user visibility.
- The sample app. It is unchanged.

## Test rule

- **Listing absence:** every "is omitted from the listing" assertion also checks that the listing responded 200 and that a named sibling entry **is** present in the same response.
- **Refusal:** every "is refused" assertion checks that the file's content is absent from the body, and that a non-sensitive sibling in the same directory returns 200.

An absence check alone passes vacuously when the listing fails or renders empty.

## Steps

### 1. Pin what listings do today (tests only, no production change)

Add to `MountRequestTests`:

- `Mount_DotPrefixedFile_IsOmittedFromListing`: `.secret.txt` does not appear in the `/docs` listing, while `visible.txt` does.
- `Mount_DotPrefixedDirectory_IsOmittedFromListing`: same for `.private/`.
- `Mount_HiddenAttributeFile_IsOmittedFromListing`: sets `FileAttributes.Hidden`. It uses a new `WindowsFactAttribute` in `Infrastructure/`, modelled on `SymlinkFactAttribute`, because xunit 2.9 has no runtime skip. Elsewhere it is skipped, since Hidden has no meaning separate from a leading dot there.

**Verify:** all three pass against the current code with no source change. If one fails, stop: the
premise that listings already exclude sensitive entries is wrong and the plan needs revisiting. These
tests guard step 2, which replaces the mechanism behind them.

### 2. Refuse sensitive paths through one policy (red, then green)

Write these first and watch them fail against the current code. Every file case serves its content
today. For the directory case, record what it actually returns before the fix rather than assuming it
is 200, because the listing may already render as absent.

- `.secret.txt` returns 404, and its content is absent from the body.
- `.private/` returns 404 as a directory. `.private/a.txt` returns 404.
- `.git/config` returns 404: a sensitive **directory** segment with a non-sensitive leaf.
- `sub/.env` returns 404: sensitive leaf below a normal directory.
- Windows only (`WindowsFact`): a Hidden-attribute file and a file inside a Hidden-attribute directory both return 404. The same for `System`.
- `?raw=1` on `.notes.md` returns 404 (the Markdown path is covered too).
- Symlink (`SymlinkFact`): `link.txt -> .secret.txt` returns 200 and is listed (decision 6b).
- Raw-path forms (decision 6f), each 404 while a normal sibling is 200:
  - `sub/../.env` and `sub/%2e%2e/.env` (`TestServer` does not normalise, so this exercises the policy);
  - Windows only: `sub%5C.env`, `.env::$DATA`, and the 8.3 alias of `.env`. The alias test uses a `ShortNameFact` that skips when the volume generates no short name, detected by enumerating the alias.

Then implement:

- `SensitivePathPolicy` (internal), built once per mount from `ResolvedRoot`:
  - `Canonicalise(requestPath)`: applies decision 6f. It returns the root-relative segments, or refuses.
  - `IsSensitiveSegment(fullPath, name)`: the on-disk name starts with `.`, or `File.GetAttributes` has `Hidden` or `System`. Unreadable attributes on an existing entry count as sensitive.
  - `IsRefused(requestPath)`: canonicalises, then walks the segments from `ResolvedRoot` via `Path.Combine`, before any link resolution. True when canonicalisation refuses or a segment is sensitive (exposure is added in step 5).
- `FileServerMount`: construct `PhysicalFileProvider(ResolvedRoot, ExclusionFilters.None)` and pass the policy to the listing wrapper.
- `Handle`: after `TryResolve` and **before** the `Directory.Exists` branch, return `NotFound()` when `policy.IsRefused(path)`.
- Listing wrapper: omit an entry when `IsRefused` holds for its relative path. It computes once per listing whether the directory's own path has a sensitive segment. Then, per entry, it adds only the entry's own segment check (one stat per entry, not one per ancestor). An entry inside a sensitive-but-exposed directory is therefore still sensitive, and step 5's exposure match on its full path decides it.

**Verify:**
- The red tests now pass, and the step-1 tests still pass.
- Parity test: over one fixture holding normal, dot-prefixed and (on Windows) Hidden entries at two depths, every file is listed exactly when it downloads with 200 (all extensions allowed, no patterns).

### 3. Pattern options: normalisation and validation

- Add `UnlistedPatterns` and `ExposedSensitivePatterns` to `FileServerMountOptions`, with XML docs in the file's existing style:
  - both docs state the root-anchoring rule (2a);
  - `UnlistedPatterns` carries the "not access control" warning;
  - `ExposedSensitivePatterns` states the full-path rule (6e), with the `.well-known/**` example.
- A single normaliser used for both, in `MapFileServer`: trim each entry, drop empties, convert `\` to `/`, strip a leading `/`. Replace the list rather than editing it, as `AllowedExtensions` does.
- After that normalisation, reject with `ArgumentException` at registration any pattern that is still rooted (`C:/x`, `//server/x`) or has a `..` **segment**. A name such as `a..b` is fine. The message names which option held it.

**Verify:** `MountRegistrationTests`, run against both options, cover:

- a caller's list left unmodified;
- `\`-separated input normalised;
- `/x` accepted as `x`, and `a..b.txt` accepted;
- `../x`, `sub/../x` and `C:\x` rejected at startup, with the option named in the message.

### 4. Unlisted filter

- Rename `AllowedExtensionsFileProvider` to `ListingFileProvider` (`internal`, so no public break). It takes the extension set, the policy, and an unlisted `Matcher` built once per mount.
- `GetDirectoryContents(subpath)`: keep an entry when (directory, or allowed extension) **and** not refused (step 2) **and** its relative path does not match an unlisted pattern. That path is `subpath/name`, or just `name` at the mount root, where `subpath` is empty; a leading `/` would match nothing.
- `GetFileInfo` is unchanged. Unlisting does not affect lookup.

**Verify:** `ExtensionFilterTests` (renamed or extended) cover:

- `*.key` hides `a.key` but **not** `sub/a.key`, while `**/*.key` hides both (2a).
- `drafts/*.md` hides `drafts/a.md` but not `a.md`.
- `private` hides the directory from the root listing, while `private/**` does **not** hide it (2a).
- Case-insensitivity: `drafts/*.md` hides `Drafts/A.MD`.

### 5. Exposed sensitive patterns

- `SensitivePathPolicy` takes an exposure `Matcher` (`Ordinal`, decision 2b). `IsRefused(requestPath)` becomes: canonicalisation refuses, **or** some segment is sensitive **and** the canonical relative path matches no exposed pattern (6e). The match runs on the canonical path from 6f, never the raw one. `Handle` and the listing wrapper pick this up unchanged.

**Verify:** with `ExposedSensitivePatterns = [".well-known/**"]`:

- `.well-known/security.txt` and `.well-known/acme-challenge/tok` return 200 with their content.
- `.well-known/` itself returns 404 and is absent from the root listing (6e), while a normal sibling is present.
- `/.well-known/acme-challenge` lists `tok`.
- `.git/config` and `.env` still return 404 (their siblings return 200).
- Adding `.well-known` to the patterns makes the directory listable and listed.
- With `UnlistedPatterns = [".well-known/**"]` as well, `security.txt` is served but not listed in `/.well-known`.
- `AllowedExtensions = { ".md" }`: an exposed `.well-known/security.txt` is still 404 (decision 4).
- Windows only: a Hidden-attribute file matched by an exposed pattern is served.
- `.well-known/../.git/config` and `.well-known/%2e%2e/.git/config` return 404: the canonical path is `.git/config`, which no pattern exposes.
- Case (2b): on Linux, `.WELL-KNOWN/x`, a separate directory, returns 404. On Windows, `/.WELL-KNOWN/security.txt` returns 404, which fails closed, while the generated lowercase URL returns 200.
- The parity test from step 2, re-run with exposed patterns, still holds.

### 6. End-to-end behaviour

Add to `MountRequestTests`, each following the test rule:

- An unlisted file is missing from the listing HTML, and its URL returns 200 with its content.
- An unlisted directory is missing from its parent listing. Its URL renders a listing that shows its non-matching contents.
- `UnlistedPatterns` plus `AllowedExtensions`: a disallowed unlisted file is 404 (decision 4).
- An unlisted `.md` still renders as Markdown, and `?raw=1` still serves raw.
- `EnableDirectoryBrowsing = false` plus patterns: nothing else changes, files still serve, and sensitive files are still refused.
- An unlisted pattern cannot rescue a sensitive path. With `UnlistedPatterns = [".env"]`, `.env` is still 404.

**Verify:** the full suite passes: `dotnet test` from the repo root.

### 7. CLI

- `Settings`: add both options to the JSON model, their environment variables and flags, with the same override messages and precedence as `AllowedExtensions`.
- `Program.cs`: assign both.

**Verify:** run the CLI against a temp directory holding `a.key`, `sub/b.key`, `private/c.txt`, `.env`,
`.git/config` and `.well-known/security.txt`. Use the flags
`--unlisted-patterns "**/*.key;private"` and `--exposed-sensitive-patterns ".well-known/**"`. Then check:

- The root listing omits `a.key`, `private/`, `.env`, `.git/` and `.well-known/`.
- `/sub` omits `b.key`, and `/private` lists `c.txt`.
- `curl` returns 200 for `a.key`, `sub/b.key`, `private/c.txt` and `.well-known/security.txt`.
- `curl` returns 404 for `.env`, `.git`, `.git/config` and `.well-known`.

### 8. Docs and changelog

- README:
  - Add two rows to the options table.
  - Add an "Unlisted files" section with the root-anchoring rule, the `**/` example and the not-access-control warning.
  - Add a "Hidden and dot-prefixed files" section. Such paths are never listed and never served, including anything beneath a dot-prefixed or Hidden directory, and the Hidden and System attributes apply on Windows. The section also gives `ExposedSensitivePatterns` with the `.well-known/**` example and the full-path rule.
- `CHANGELOG.md` `[Unreleased]`:
  - `Added`: `UnlistedPatterns` and `ExposedSensitivePatterns`.
  - `Changed` (**breaking**): sensitive paths now return 404 on direct request. Give the migration from 6c.

**Verify:** `dotnet build -warnaserror` is clean (XML docs complete) and the README table renders.
