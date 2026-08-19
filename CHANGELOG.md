# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow a `YYYY.M.D` calendar scheme.

---

## [Unreleased]

### Added
- Directory listings carry the Auto / Light / Dark control that documents already had, and both remember the choice under one key — a pinned scheme holds while browsing instead of reverting on every listing.

### Changed
- Listings are vertically tighter. The table sets its own leading rather than inheriting the page's prose line-height, which was adding more height per row than the cell padding was; rows are about a fifth shorter and the header sits closer to the listing it labels.
- The parent-directory link carries an up arrow, as file managers do. U+2191 rather than U+2B06, which takes emoji presentation on some platforms and would render in colour against a monochrome button.
- Sizes and modified times are set in monospace so figures line up down the column by construction, while names and headers keep the page's own face — proportional type reads better and fits more of a long name before it wraps. The stack names a floor per platform rather than a favourite, since no monospace face is present everywhere: Consolas has shipped with Windows since Vista and Menlo with macOS since 10.6, while Linux guarantees nothing.
- Ligatures are disabled across the listing. Every font family has some pair it wants to fuse — programming faces join `->` `!=` `>=`, ordinary text faces join `fi` `fl` — and a file name is an exact string, so the listing should not render one the filesystem does not contain.
- Buttons in an action row share one colour. A link styled as a button took the accent colour while a real button beside it took the foreground, so two controls in the same row looked like different kinds of thing. The accent is now reserved for navigable content: file names and breadcrumbs.

### Fixed
- The MIT notice for [github-markdown-css](https://github.com/sindresorhus/github-markdown-css), vendored and embedded in the assembly, now ships with every form the software is distributed in: `THIRD-PARTY-NOTICES.md` is packed into the NuGet package, copied into each release archive alongside `LICENSE`, and copied into both container images. Its licence requires the notice to travel with substantial portions of the work, and nothing carried it before. CI asserts the image still has it.

---

## [2026.8.18] — 2026-08-18

### Added
- The file browser is installable as a NuGet package, [`Bennewitz.Ninja.FileServer`](https://www.nuget.org/packages/Bennewitz.Ninja.FileServer). `AddFileServer()` plus `MapFileServer("/docs", …)` mounts a browsable, downloadable directory on a route of any ASP.NET Core application; call it once per directory to serve several. Views, styles, and script are compiled or embedded into the assembly, so a host needs no `wwwroot/` and never has to call `UseStaticFiles`.
- `MapFileServer` returns the mount's route group, so `RequireAuthorization()` applied to it covers file downloads as well as listings — files are served from endpoints rather than static-file middleware precisely so that authorization has something to enforce against. The stylesheet endpoint stays anonymous, so an unauthenticated visitor still lands on a styled login page.
- Per-mount options: `RootPath`, `AllowedExtensions`, `EnableDirectoryBrowsing`, `RenderMarkdown`, `LayoutPath` (render inside a host layout), `IncludeDefaultStyles`, and `CacheControl`. Mounts share no state, so each can have its own filter, layout, and policy.
- Registrations that would make authorization ambiguous now fail while the pipeline is built rather than at request time: a duplicate route prefix, or a root directory that overlaps another mount's root.
- Fenced code blocks in rendered Markdown are tokenised, using GitHub's own token classes so the colour-scheme toggle governs code along with the rest of the page. Around two dozen languages are recognised; a fence tagged with anything else keeps its text and loses only the colour. Adds one dependency, `ColorCode.HTML`.
- Test suite for the component: containment (including symlinked leaves and symlinked intermediate directories), the extension filter on both the listing and download paths, prefix normalisation, mount conflict detection, and integration tests proving `RequireAuthorization` covers downloads while the asset endpoint stays anonymous.
- `samples/SampleWebApp`, an application that installs the component from a package and mounts it four times — default styling, inside the host's own layout, with an extension filter, and behind `RequireAuthorization` — together with `publish/Pack-Local.ps1`, which packs into a local feed for testing it that way.
- CI runs the tests, packs the component, builds the sample against that package, smoke-tests the published binary, and builds and runs the container image. A plain build caught none of the breakages found while assembling this release.
- The release workflow publishes the package to NuGet.org using trusted publishing, so no long-lived API key is stored in the repository. Tagging without the publishing account configured still produces a complete GitHub Release.

### Changed
- `--help` prints the build's version, which the contributing guide has always told bug reporters to read from it.
- The standalone server runs on the component, through the same public API a package consumer uses. Its Bootstrap-based directory formatter and Markdown page are gone, along with the `/view` redirect — a `.md` file now renders at its own URL, with `?raw` serving the source.
- Listings and rendered Markdown use a self-contained stylesheet whose every class is prefixed `bnfs-`, so the pages look right in a host that brings no CSS and cannot collide with one that brings its own.
- The Auto / Light / Dark control now themes the whole page rather than only the document body, so a pinned scheme no longer leaves a light document inside dark chrome.
- Path containment resolves symlinks on every path segment before comparing against the mount root, and compares ordinally. String canonicalisation alone never touches the filesystem, so a link in an intermediate directory could otherwise escape the root undetected.
- Component assets are served with `Cache-Control: public, max-age=31536000, immutable`. Their URLs carry a build-specific version segment, so an upgrade re-fetches them.

### Removed
- Bootstrap and the stock ASP.NET favicon are no longer shipped. Nothing referenced them once the component owned the UI, and they were the bulk of the embedded assets.

### Fixed
- `settings.json.example` is now actually present in the release archives. It has been documented as shipping since 2026.5.15 but never reached the publish output, so the binary's first-run "example configuration file is available at…" hint never appeared.
- A local `settings.json` is no longer published. Previously a release built on a developer machine shipped that machine-specific file renamed to `settings.json.example`, leaking local paths into the archive.
- `publish/Publish-Rid.ps1` now publishes the CLI host project. After the library/CLI split it still pointed at the Razor class library, so every release publish failed with `NETSDK1099`.
- The Docker images build again. Both Dockerfiles restored, built, and published the class library after the split, producing an image whose entry point assembly had no entry point.
- `publish/Smoke-Test.ps1` publishes and smokes the CLI host. Like the publish script and the Dockerfiles, it still pointed at the class library after the split, so the smoke could never have found a binary to run.
- The container image builds again on current .NET runtime images. They ship neither `adduser` nor `useradd`, so creating an unprivileged user failed with exit 127; the build now runs as the non-root `app` user (uid 1654) those images already provide.
- A `settings.json` bind-mounted at `/app/settings.json` is now read. Settings were resolved next to `Process.MainModule`, which under `dotnet App.dll` is the shared dotnet host, so the container looked in `/usr/share/dotnet` and the mount the image documents had no effect. Resolution is now relative to the application directory, which is also correct for a single-file publish.

---

## [2026.5.15] — 2026-05-15

### Added
- Self-contained single-file binaries for six platforms: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- Markdown rendering: `.md` files are served as formatted HTML with raw/download controls and an auto/light/dark theme toggle.
- HTTPS support via PFX certificate (`CertificatePath` / `CertificatePassword`).
- `AllowedExtensions` filter: restrict which file types appear in listings and can be downloaded.
- `--help` / `-h` / `-?` CLI flag prints all configuration options and exits.
- Docker images: standard (volume-mounted files) and bundled-content (`FilesRoot/` baked in at build time).
- UTF-8 charset declared for all served text files; XML-family types forced to `text/plain` so browsers display source rather than rendering.
- `settings.json.example` shipped alongside each binary as a configuration starter.
- CI workflow (build on push/PR to `main`) and release workflow (publish all RIDs + GitHub Release on `v*` tag).

[Unreleased]: https://github.com/JanusMael/Bennewitz.Ninja.FileServer/compare/v2026.8.18...HEAD
[2026.8.18]: https://github.com/JanusMael/Bennewitz.Ninja.FileServer/compare/v2026.5.15...v2026.8.18
[2026.5.15]: https://github.com/JanusMael/Bennewitz.Ninja.FileServer/releases/tag/v2026.5.15
