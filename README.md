# Bennewitz.Ninja.FileServer

[![CI](https://github.com/JanusMael/Bennewitz.Ninja.FileServer/actions/workflows/ci.yml/badge.svg)](https://github.com/JanusMael/Bennewitz.Ninja.FileServer/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/JanusMael/Bennewitz.Ninja.FileServer)](https://github.com/JanusMael/Bennewitz.Ninja.FileServer/releases/latest)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

A lightweight ASP.NET Core web app that serves a local directory over HTTP or HTTPS with directory browsing enabled. Point it at a folder; browse it in any web browser.

> **Security note:** This server has no authentication or authorization. Use it only on trusted networks or behind a reverse proxy that handles access control.

---

## Quick start

**Prerequisites:** [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

1. Clone the repository and create a `settings.json` next to the executable (or in the project directory when running with `dotnet run`):

   ```json
   {
     "ServedFilesRoot": "/path/to/your/files",
     "ServedFilesRoute": "files",
     "HttpPort": 5550
   }
   ```

2. Run the app from the repository root:

   ```sh
   dotnet run --project src/Bennewitz.Ninja.FileServer
   ```

3. Open `http://localhost:5550/` in a browser — it redirects automatically to the file browser.

---

## HTTPS

HTTPS is enabled automatically when a PFX certificate is configured. The server will listen on both the HTTP port (redirecting to HTTPS) and the HTTPS port.

### Generating a self-signed certificate

**Using .NET's built-in tool (simplest):**
```sh
dotnet dev-certs https -ep ./server.pfx -p yourpassword
```

**Using OpenSSL (cross-platform, production-grade):**
```sh
openssl req -x509 -newkey rsa:4096 -keyout key.pem -out cert.pem -days 365 -nodes \
  -subj "/CN=fileserver"
openssl pkcs12 -export -out server.pfx -inkey key.pem -in cert.pem -passout pass:yourpassword
```

### Enabling HTTPS in `settings.json`

Add `CertificatePath` and optionally `CertificatePassword` to your `settings.json`:

```json
{
  "ServedFilesRoot": "/path/to/your/files",
  "HttpPort": 5550,
  "HttpsPort": 5551,
  "CertificatePath": "/path/to/server.pfx",
  "CertificatePassword": "yourpassword"
}
```

> **Note:** Browsers will show a security warning for self-signed certificates. You can suppress this permanently by adding the certificate to your OS or browser trust store.

---

## Docker

See [docker/README.md](docker/README.md) for Docker usage, including the standard (volume-mounted) and bundled-content image variants.

---

## Markdown rendering

`.md` files are rendered as formatted HTML instead of raw text. Every rendered page has three controls in the top-right corner:

- **View raw** — opens the raw Markdown source in the browser.
- **Download** — downloads the `.md` file directly.
- **Auto / Light / Dark** — cycles the colour theme between system default, forced light, and forced dark. The choice is remembered across pages via `localStorage`.

Append `?raw=1` to any `.md` URL to bypass rendering from any client.

When `AllowedExtensions` is set and `.md` is not included, the intercept is skipped and `.md` files are handled (or hidden) like any other excluded extension.

---

## Configuration

Settings are resolved in this order (later sources override earlier ones):

1. `settings.json` — located next to the executable (or the project root when using `dotnet run`)
2. Environment variables
3. Command-line arguments *(highest priority)*

| `settings.json` key | Environment variable | CLI argument | Default | Description |
|---|---|---|---|---|
| `ServedFilesRoot` | `FILE_SERVER_ROOT` | `--root` | *(required)* | Absolute path to the directory to serve. The app fails to start if absent or not absolute. |
| `ServedFilesRoute` | `FILE_SERVER_ROUTE` | `--route` | `files` | URL path segment under which files appear (e.g. `files` → `/files/…`). |
| `HttpPort` | `FILE_SERVER_HTTP_PORT` | `--http-port` | `5550` | TCP port Kestrel listens on for HTTP. Redirects to HTTPS when a certificate is configured. |
| `HttpsPort` | `FILE_SERVER_HTTPS_PORT` | `--https-port` | `5551` | TCP port Kestrel listens on for HTTPS. Has no effect when no certificate is configured. |
| `CertificatePath` | `FILE_SERVER_CERT_PATH` | `--cert` | *(none — HTTP only)* | Absolute path to a PFX certificate file. When set, HTTPS is enabled on `HttpsPort`. |
| `CertificatePassword` | `FILE_SERVER_CERT_PASSWORD` | `--cert-password` | *(empty)* | Password for the PFX file. May be omitted for password-less PFX files. |
| `AllowedExtensions` | `FILE_SERVER_ALLOWED_EXTENSIONS` | `--allowed-extensions` | *(empty — all files)* | JSON string array of permitted file extensions (e.g. `[".pdf", ".txt"]`). Env var and CLI: semicolon-delimited (e.g. `.pdf;.txt;.zip`). When non-empty, only matching files appear in listings and can be downloaded. Directories are always visible. Leading dot is optional. |

### CLI argument syntax

Both space-separated and equals forms are accepted:

```sh
FileServer --root /srv/files --http-port 8080
FileServer --root=/srv/files --http-port=8080
```

Run `FileServer --help` (or `-h` / `-?`) to print all options and exit.

### Cross-platform path notes

- **Windows:** use backslashes or forward slashes — both work (`C:\Share` or `C:/Share`).
- **Linux / macOS / Docker:** use POSIX paths (`/srv/files`).
- Relative paths are rejected at startup with a descriptive error.

---

## Building

```sh
# Debug build
dotnet build -c Debug

# Release build (also produces XML documentation)
dotnet build -c Release

# Publish self-contained single-file binary for Linux x64
dotnet publish -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The publish output contains only the executable and `settings.json.example`. All UI assets (`wwwroot/`) are embedded into the binary at build time — no separate folder is needed at runtime. Rename `settings.json.example` to `settings.json` (or use environment variables / CLI arguments) to configure the server.

---

## Development

Set `ASPNETCORE_ENVIRONMENT=Development` to enable detailed error pages. Configuration comes from `settings.json` (and environment variables / CLI arguments) — `appsettings.json` is not used by this app.

---

## License

MIT — see [LICENSE](LICENSE).
