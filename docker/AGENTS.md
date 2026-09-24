# AGENTS.md — `docker/`

The container images. Every build runs from the repository root, which is the build context.
Usage is in `README.md` here.

| File | What it is |
|---|---|
| `Dockerfile` | The standard image: the CLI host on `aspnet:10.0`, files supplied by a volume at `FILE_SERVER_ROOT` |
| `Dockerfile.bundle` | The same image with `FilesRoot/` from the repository root baked in at `/data` |
| `Dockerfile.dockerignore`, `Dockerfile.bundle.dockerignore` | Build-context exclusions, one per Dockerfile |
| `build-bundle.ps1`, `build-bundle.sh` | Build the bundled image; both refuse to run without `FilesRoot/` |
| `README.md` | How to build and run both images |

## Rules

| Rule | Why | Guarded by |
|---|---|---|
| **The two Dockerfiles change together** | They differ only in the bundled content; a fix to one is a fix both need | nothing: CI builds `Dockerfile` only |
| Both build and publish `src/Bennewitz.Ninja.FileServer.Cli`, and `ENTRYPOINT` runs `Bennewitz.Ninja.FileServer.dll` | The library is not an executable; an image built from it had an entry point with no entry | CI's `docker` job, which requests a listing and a `.md` |
| The image runs as `USER app`, never a user it creates | The .NET runtime images ship neither `adduser` nor `useradd` | CI's `docker` job asserts uid `1654` |
| `THIRD-PARTY-NOTICES.md` is copied into `/app`, and neither `.dockerignore` excludes it | The embedded stylesheet's MIT notice must travel with the image | CI's `docker` job, `test -f /app/THIRD-PARTY-NOTICES.md` |
| `curl` is installed in the base stage | The `HEALTHCHECK` calls it, and the `aspnet` image omits it | nothing but the health check itself |
| A file renamed here is renamed in `publish/Publish-Rid.ps1` too | Each release archive carries a copy of this directory, and a missing file is skipped without a word | nothing: the script only tests that each file exists |
| `build-bundle.sh` and the Dockerfiles stay LF | A CRLF shell script or Dockerfile fails inside a Linux build | `.gitattributes`, `*.sh` and `Dockerfile*` |
