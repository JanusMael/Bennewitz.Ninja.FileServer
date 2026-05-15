<#
.SYNOPSIS
    Publishes Bennewitz.Ninja.FileServer as a self-contained binary for a single RID.

.DESCRIPTION
    The single-RID worker behind the split publish workflow:

        publish.ps1                     (orchestrator — prompts per RID)
            ├── Publish-Rid.ps1         (THIS FILE — does the work)
            ├── publish-win-x64.ps1     (thin wrapper → Publish-Rid.ps1 -Rid win-x64 -Clean)
            ├── publish-win-arm64.ps1
            ├── publish-linux-x64.ps1
            ├── publish-linux-arm64.ps1
            ├── publish-osx-x64.ps1
            └── publish-osx-arm64.ps1

    All scripts live under publish/ at the repo root.

    Each invocation:
      1. Optionally wipes bin/obj across the src tree (see -Clean).
      2. Runs `dotnet publish -c Release -r <rid> --self-contained true`
         with ReadyToRun=true and output tee'd to a per-RID log under dist/logs/.
      3. Packages the published folder:
           Windows RIDs  → dist/Bennewitz.Ninja.FileServer-<rid>.zip
           Linux/macOS   → dist/Bennewitz.Ninja.FileServer-<rid>.tar.gz
             (all entries set to Unix mode 0755 so the binary is executable on
              extraction without requiring a manual chmod)
      4. Removes the staging folder.
      5. Emits a structured PSCustomObject to the pipeline so orchestrators
         can aggregate results across RIDs.

.PARAMETER Rid
    The .NET runtime identifier to publish for.

.PARAMETER Clean
    When specified, wipes every bin/ and obj/ directory under src/ before
    publishing. Uses a manual directory wipe rather than `dotnet clean` —
    see the block comment in publish.ps1 for the NETSDK1047 rationale.
    Also removes the target RID's existing zip and staging folder under dist/
    so the new build lands on a clean slate; other RIDs' zips are preserved.

.PARAMETER DistFolder
    Absolute path where archives and logs should land. Defaults to
    `<PSScriptRoot>/dist` so standalone invocations and orchestrator
    invocations converge on the same layout.

.OUTPUTS
    PSCustomObject with properties:
      Rid          — the RID that was built
      ExitCode     — the exit code of `dotnet publish` (0 on success)
      LogPath      — absolute path to the per-RID publish log
      ArchivePath  — absolute path to the produced archive (if publish succeeded)

.EXAMPLE
    pwsh publish/Publish-Rid.ps1 -Rid win-x64 -Clean

.EXAMPLE
    pwsh publish/Publish-Rid.ps1 -Rid linux-arm64
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string] $Rid,

    [switch] $Clean,

    [string] $DistFolder
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is publish/ — the repo root is one level up.
$repoRoot    = Split-Path $PSScriptRoot -Parent
$srcRoot     = Join-Path $repoRoot 'src'
$projectPath = Join-Path $srcRoot 'Bennewitz.Ninja.FileServer' 'Bennewitz.Ninja.FileServer.csproj'

# Default dist/log folders under publish/ so repeated runs accumulate in one place.
if (-not $DistFolder) { $DistFolder = Join-Path $PSScriptRoot 'dist' }
$logFolder = Join-Path $DistFolder 'logs'

New-Item -ItemType Directory -Path $DistFolder -Force | Out-Null
New-Item -ItemType Directory -Path $logFolder  -Force | Out-Null

# ── Optional clean ───────────────────────────────────────────────────────────
# Wipe bin/obj across the src tree (not via `dotnet clean` — see NETSDK1047
# rationale in publish.ps1) AND drop any prior zip/staging for THIS RID.
# Other RIDs' zips in dist/ are left alone.
if ($Clean) {
    Write-Host "[$Rid] Wiping bin/ and obj/ under src/..." -ForegroundColor DarkGray
    $cleanTargets = Get-ChildItem -Path $srcRoot -Directory -Recurse -Force `
            -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
        Select-Object -ExpandProperty FullName
    foreach ($dir in $cleanTargets) {
        Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
    }

    $priorZip     = Join-Path $DistFolder "Bennewitz.Ninja.FileServer-$Rid.zip"
    $priorTar     = Join-Path $DistFolder "Bennewitz.Ninja.FileServer-$Rid.tar.gz"
    $priorStaging = Join-Path $DistFolder $Rid
    if (Test-Path $priorZip)     { Remove-Item -Force -LiteralPath $priorZip }
    if (Test-Path $priorTar)     { Remove-Item -Force -LiteralPath $priorTar }
    if (Test-Path $priorStaging) { Remove-Item -Recurse -Force -LiteralPath $priorStaging }
}

# ── Publish ──────────────────────────────────────────────────────────────────
Write-Host "`n>>> Publishing for $Rid..." -ForegroundColor Yellow
$ridFolder = Join-Path $DistFolder $Rid
$logPath   = Join-Path $logFolder  "publish-$Rid.log"

# Always wipe the staging folder before publishing so leftover files from a
# previous run do not end up in the distribution archive.
if (Test-Path $ridFolder) {
    Write-Host "[$Rid] Cleaning prior staging folder..." -ForegroundColor DarkGray
    Remove-Item -Recurse -Force -LiteralPath $ridFolder
}

# PublishSingleFile=true bundles the executable and all managed DLLs into one file.
# IncludeNativeLibrariesForSelfExtract=true also bundles native runtime libs (libcoreclr, etc.)
# so the only files in the archive are the binary and settings.json.
# ReadyToRun pre-compiles hot paths to improve Kestrel startup time.
# IncrementalBuild=false forces a fresh compilation per RID so the output
# cannot accidentally reuse another RID's artifacts.
# Tee-Object preserves $LASTEXITCODE so we can read the real dotnet exit code.
dotnet publish "$projectPath" `
    -c Release `
    -r $Rid `
    --output "$ridFolder" `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:ReadyToRun=true `
    -p:IncrementalBuild=false `
    -p:BuildInParallel=false 2>&1 | Tee-Object -FilePath $logPath

$publishExit = $LASTEXITCODE

# ── Post-publish sweep ────────────────────────────────────────────────────────
# Remove files that are not needed in a deployment archive:
#   *.pdb                              — debug symbols (single-file binary has none embedded)
#   *.xml                              — XML documentation (dev tool, not runtime)
#   *.staticwebassets.endpoints.json   — static asset fingerprinting metadata for MapStaticAssets();
#                                        this app uses UseStaticFiles() with embedded assets instead
# appsettings*.json and web.config are excluded at the MSBuild level (csproj), so they should
# not appear here; the patterns are a belt-and-suspenders fallback.
if (Test-Path $ridFolder) {
    $sweepPatterns = @('*.pdb', '*.xml', '*.staticwebassets.endpoints.json', 'appsettings*.json', 'web.config')
    foreach ($pattern in $sweepPatterns) {
        Get-ChildItem -LiteralPath $ridFolder -Filter $pattern -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}

# ── Rename settings.json → settings.json.example ─────────────────────────────
# The app loads settings.json automatically; shipping it under the .example name
# means the binary starts with no configuration on first run, shows help, and
# prompts the user to either rename the file or pass --root / set FILE_SERVER_ROOT.
$settingsJson    = Join-Path $ridFolder 'settings.json'
$settingsExample = Join-Path $ridFolder 'settings.json.example'
if (Test-Path $settingsJson) {
    Rename-Item -LiteralPath $settingsJson -NewName 'settings.json.example'
    Write-Host "[$Rid] settings.json → settings.json.example" -ForegroundColor DarkGray
}

# ── Extra distribution files ─────────────────────────────────────────────────
# Copy README.md and platform-relevant Docker artifacts into the staging folder
# so each release archive is self-contained without needing the repository.
# Windows RIDs get build-bundle.ps1; Linux/macOS RIDs get build-bundle.sh.
$isWindowsRid = $Rid -like 'win-*'
if ($publishExit -eq 0 -and (Test-Path $ridFolder)) {
    $readmeSrc = Join-Path $repoRoot 'README.md'
    if (Test-Path $readmeSrc) {
        Copy-Item -LiteralPath $readmeSrc -Destination $ridFolder
    }

    $dockerSrc = Join-Path $repoRoot 'docker'
    $dockerDst = Join-Path $ridFolder 'docker'
    New-Item -ItemType Directory -Path $dockerDst -Force | Out-Null

    foreach ($f in @('Dockerfile', 'Dockerfile.bundle',
                      'Dockerfile.dockerignore', 'Dockerfile.bundle.dockerignore',
                      'README.md')) {
        $src = Join-Path $dockerSrc $f
        if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination $dockerDst }
    }

    $bundleScript = if ($isWindowsRid) { 'build-bundle.ps1' } else { 'build-bundle.sh' }
    $src = Join-Path $dockerSrc $bundleScript
    if (Test-Path $src) { Copy-Item -LiteralPath $src -Destination $dockerDst }

    Write-Host "[$Rid] Copied README.md and docker/ ($bundleScript)" -ForegroundColor DarkGray
}

# ── Archive ──────────────────────────────────────────────────────────────────
# Windows → .zip   (native Windows archive format; no Unix permissions needed)
# Linux   → .tar.gz (preserves execute bit so the binary runs without chmod)
# macOS   → .tar.gz (same; Finder and tar both handle .tar.gz natively)
#
# NOTE: we do NOT use Windows bsdtar for Unix targets. NTFS has no execute bit,
# so bsdtar writes -rw-r--r-- for the binary, requiring a manual chmod +x after
# extraction. .NET's TarWriter (System.Formats.Tar, .NET 7+) lets us specify
# rwxr-xr-x explicitly for every entry.
$archivePath = $null
if ($publishExit -eq 0) {
    $archiveExt   = if ($isWindowsRid) { '.zip' } else { '.tar.gz' }
    $archivePath  = Join-Path $DistFolder "Bennewitz.Ninja.FileServer-$Rid$archiveExt"

    Write-Host ("[$Rid] Creating $archiveExt archive...") -ForegroundColor Gray

    try {
        if (Test-Path $archivePath) { Remove-Item -Force -LiteralPath $archivePath }

        if ($isWindowsRid) {
            # ── Windows: ZIP ─────────────────────────────────────────────────
            Add-Type -AssemblyName 'System.IO.Compression.FileSystem'
            [System.IO.Compression.ZipFile]::CreateFromDirectory(
                $ridFolder,
                $archivePath,
                [System.IO.Compression.CompressionLevel]::Optimal,
                $false)
        }
        else {
            # ── Linux / macOS: TAR.GZ ────────────────────────────────────────
            # rwxr-xr-x (0755) — appropriate for an executable binary and safe
            # for any other files present alongside it (settings.json, etc.).
            Add-Type -AssemblyName 'System.IO.Compression'   # GZipStream
            Add-Type -AssemblyName 'System.Formats.Tar'      # TarWriter, PaxTarEntry

            $execMode = [System.IO.UnixFileMode]::UserRead    -bor
                        [System.IO.UnixFileMode]::UserWrite   -bor
                        [System.IO.UnixFileMode]::UserExecute -bor
                        [System.IO.UnixFileMode]::GroupRead   -bor
                        [System.IO.UnixFileMode]::GroupExecute -bor
                        [System.IO.UnixFileMode]::OtherRead   -bor
                        [System.IO.UnixFileMode]::OtherExecute

            $fileStream = [System.IO.File]::Create($archivePath)
            $gzStream   = [System.IO.Compression.GZipStream]::new(
                              $fileStream,
                              [System.IO.Compression.CompressionLevel]::Optimal)
            # TarWriter(leaveOpen=$false) owns $gzStream; disposing the writer
            # also flushes GZip and closes $fileStream.
            $tarWriter = [System.Formats.Tar.TarWriter]::new($gzStream)
            try {
                foreach ($file in Get-ChildItem -Path $ridFolder -Recurse -File) {
                    # POSIX tar requires forward-slash entry names.
                    $entryName = $file.FullName.Substring($ridFolder.Length).TrimStart(
                        [System.IO.Path]::DirectorySeparatorChar,
                        [System.IO.Path]::AltDirectorySeparatorChar).Replace('\', '/')

                    $entry      = [System.Formats.Tar.PaxTarEntry]::new(
                                      [System.Formats.Tar.TarEntryType]::RegularFile,
                                      $entryName)
                    $entry.Mode = $execMode

                    $fs = [System.IO.File]::OpenRead($file.FullName)
                    try {
                        $entry.DataStream = $fs
                        $tarWriter.WriteEntry($entry)
                    }
                    finally { $fs.Dispose() }
                }
            }
            finally { $tarWriter.Dispose() }   # flushes GZip and closes the file
        }

        $fileCount = (Get-ChildItem -Path $ridFolder -Recurse -File).Count
        Write-Host "[$Rid] Created: $(Split-Path $archivePath -Leaf) ($fileCount file(s))" -ForegroundColor Cyan
    }
    catch {
        Write-Host "[$Rid] Failed to create archive: $($_.Exception.Message)" -ForegroundColor Red
        $archivePath = $null
    }

    # Remove the staging folder — the archive is the distributable artifact.
    if (Test-Path $ridFolder) {
        Remove-Item -Recurse -Force -LiteralPath $ridFolder
    }
}
else {
    Write-Host "[$Rid] dotnet publish exited with code $publishExit." -ForegroundColor Red
}

# Emit a structured result so orchestrators can aggregate without re-parsing.
[pscustomobject]@{
    Rid         = $Rid
    ExitCode    = $publishExit
    LogPath     = $logPath
    ArchivePath = $archivePath
}
