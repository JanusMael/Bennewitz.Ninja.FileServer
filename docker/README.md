# Docker

All Docker artifacts live in this directory. Build commands are run from the **repository root**.

---

## Standard image

Files are supplied at runtime via a volume mount.

**HTTP only:**
```sh
docker build -f docker/Dockerfile -t file-server .

docker run \
  -e FILE_SERVER_ROOT=/data \
  -v /host/path/to/files:/data \
  -p 5550:5550 \
  file-server
```

**HTTPS:**
```sh
docker run \
  -e FILE_SERVER_ROOT=/data \
  -e FILE_SERVER_CERT_PATH=/certs/server.pfx \
  -e FILE_SERVER_CERT_PASSWORD=yourpassword \
  -v /host/path/to/files:/data \
  -v /host/path/to/certs:/certs:ro \
  -p 5550:5550 \
  -p 5551:5551 \
  file-server
```

HTTP on port 5550 redirects automatically to HTTPS. The container runs as `app` (uid 1654), the
non-root user the .NET runtime images provide; a health check polls `http://localhost:5550/`
every 30 seconds.

**Configuring with a file instead of environment variables:**
```sh
docker run \
  -v /host/path/to/files:/data \
  -v /host/path/to/settings.json:/app/settings.json:ro \
  -p 5550:5550 \
  file-server
```

Settings are read from `/app`, next to the application. Precedence is unchanged inside the
container: `settings.json`, then environment variables, then any arguments appended to the
`docker run` command.

To use different external ports, remap them:
```sh
docker run -e FILE_SERVER_HTTP_PORT=8080 -e FILE_SERVER_HTTPS_PORT=8443 ... \
  -p 8080:8080 -p 8443:8443 file-server
```

---

## Bundled-content image

Files are baked into the image at build time — no volume mount needed at runtime.

1. Create a `FilesRoot/` directory at the repository root and populate it with the files to serve.
2. Build and run:

**Windows:**
```powershell
.\docker\build-bundle.ps1              # default tag: file-server-bundle
.\docker\build-bundle.ps1 -Tag my-tag
```

**Linux / macOS:**
```sh
./docker/build-bundle.sh               # default tag: file-server-bundle
./docker/build-bundle.sh my-tag
```

The scripts abort with a clear error if `FilesRoot/` is not present.

Once built, run with no env vars required:
```sh
docker run -p 5550:5550 file-server-bundle
```

Optional overrides still work (route, ports, HTTPS cert — see standard image examples above).

> `FilesRoot/` is not gitignored by default. Add it to `.gitignore` if the content should not be committed.

---

## Files in this directory

| File | Purpose |
|---|---|
| `Dockerfile` | Standard image — files supplied via volume at runtime |
| `Dockerfile.bundle` | Bundled image — files baked in from `FilesRoot/` at build time |
| `Dockerfile.dockerignore` | Build-context exclusions for `Dockerfile` (Docker 23.0+) |
| `Dockerfile.bundle.dockerignore` | Build-context exclusions for `Dockerfile.bundle` (Docker 23.0+) |
| `build-bundle.ps1` | Windows build script for the bundled image |
| `build-bundle.sh` | Linux/macOS build script for the bundled image |
