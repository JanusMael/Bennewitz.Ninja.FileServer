# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions follow a `YYYY.M.D` calendar scheme.

---

## [Unreleased]

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
