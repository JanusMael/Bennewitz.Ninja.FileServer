# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow a `YYYY.M.D` calendar scheme.

---

## [Unreleased]

### Fixed
- `settings.json.example` is now actually present in the release archives. It has been documented as shipping since 2026.5.15 but never reached the publish output, so the binary's first-run "example configuration file is available at…" hint never appeared.
- A local `settings.json` is no longer published. Previously a release built on a developer machine shipped that machine-specific file renamed to `settings.json.example`, leaking local paths into the archive.
- `publish/Publish-Rid.ps1` now publishes the CLI host project. After the library/CLI split it still pointed at the Razor class library, so every release publish failed with `NETSDK1099`.
- The Docker images build again. Both Dockerfiles restored, built, and published the class library after the split, producing an image whose entry point assembly had no entry point.

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

[Unreleased]: https://github.com/JanusMael/Bennewitz.Ninja.FileServer/compare/v2026.5.15...HEAD
[2026.5.15]: https://github.com/JanusMael/Bennewitz.Ninja.FileServer/releases/tag/v2026.5.15
