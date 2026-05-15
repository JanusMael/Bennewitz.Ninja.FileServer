<#
.SYNOPSIS
    Smoke-tests a published Bennewitz.Ninja.FileServer binary.

.DESCRIPTION
    Catches boot-time failures that don't show up in the build:

      - Kestrel fails to bind the port (bad configuration, missing runtime).
      - The app exits immediately (startup exception, missing FILE_SERVER_ROOT).
      - The HTTP redirect from / to /<route> is not issued.

    The smoke runs:
      1. Optionally runs `dotnet publish -c Release -r <rid>` (re-uses the
         framework-dependent path; pass -SkipPublish to test an existing binary).
      2. Creates a temp directory and sets FILE_SERVER_ROOT to it.
      3. Starts the published binary on a dedicated port (default 15550).
      4. Polls until Kestrel accepts connections or times out.
      5. Issues GET / and asserts a 302 redirect to /<route>.
      6. Kills the process and asserts it had not already crashed.
      7. Cleans up the temp directory.

    Returns 0 on success, non-zero on any failure.

.PARAMETER Rid
    .NET runtime identifier to smoke. Defaults to the host RID auto-detected
    from the current OS and architecture.

.PARAMETER TimeoutSeconds
    How long to wait for Kestrel to accept connections. Default 15.

.PARAMETER SkipPublish
    Skip `dotnet publish` and assume a binary already exists at the expected
    path. Useful when iterating on the smoke logic itself.

.PARAMETER Port
    HTTP port to use for the smoke run. Default 15550 — avoids clashing with
    a running instance on the default port 5550.

.EXAMPLE
    pwsh publish/Smoke-Test.ps1
    # Auto-detects host RID, publishes, smokes, exits 0 on success.

.EXAMPLE
    pwsh publish/Smoke-Test.ps1 -Rid win-x64 -TimeoutSeconds 30

.EXAMPLE
    pwsh publish/Smoke-Test.ps1 -SkipPublish -Port 19000
#>

[CmdletBinding()]
param(
    [string] $Rid            = $null,
    [int]    $TimeoutSeconds = 15,
    [switch] $SkipPublish,
    [int]    $Port           = 15550
)

$ErrorActionPreference = 'Stop'

# ─── 1. Resolve RID ───────────────────────────────────────────────────────────
if (-not $Rid) {
    # OSArchitecture reflects the OS — not the process — so it is correct even when running
    # an x64 PowerShell session on an ARM64 Windows machine ($env:PROCESSOR_ARCHITECTURE
    # would report AMD64 in that scenario).
    $isArm = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq `
             [System.Runtime.InteropServices.Architecture]::Arm64
    $Rid = if ($IsWindows) {
        if ($isArm) { 'win-arm64' } else { 'win-x64' }
    } elseif ($IsMacOS) {
        if ($isArm) { 'osx-arm64' } else { 'osx-x64' }
    } elseif ($IsLinux) {
        if ($isArm) { 'linux-arm64' } else { 'linux-x64' }
    } else {
        throw 'Unable to auto-detect host RID; pass -Rid explicitly.'
    }
    Write-Host "[smoke] auto-detected RID: $Rid"
}

# ─── 2. Publish (optional) ────────────────────────────────────────────────────
$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$projectPath = Join-Path $repoRoot 'src' 'Bennewitz.Ninja.FileServer' 'Bennewitz.Ninja.FileServer.csproj'
$pubDir      = Join-Path $repoRoot 'src' 'Bennewitz.Ninja.FileServer' 'bin' 'Release' 'net10.0' $Rid 'publish'

if (-not $SkipPublish) {
    Write-Host "[smoke] publishing $Rid (framework-dependent output, not dist archive)..."
    & dotnet publish "$projectPath" -c Release -r $Rid --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v minimal
    if ($LASTEXITCODE -ne 0) {
        Write-Error "[smoke] publish failed (exit $LASTEXITCODE) — smoke aborted."
        exit 1
    }
} else {
    Write-Host "[smoke] -SkipPublish set; using existing binary at $pubDir"
}

# ─── 3. Locate the binary ─────────────────────────────────────────────────────
$exeName = if ($Rid.StartsWith('win-')) { 'Bennewitz.Ninja.FileServer.exe' } else { 'Bennewitz.Ninja.FileServer' }
$exePath = Join-Path $pubDir $exeName
if (-not (Test-Path $exePath)) {
    Write-Error "[smoke] expected binary not found at $exePath"
    exit 1
}
Write-Host "[smoke] binary: $exePath"

# ─── 4. Start the server ─────────────────────────────────────────────────────
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) "FileServerSmoke-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
Write-Host "[smoke] FILE_SERVER_ROOT: $tempRoot"
Write-Host "[smoke] port: $Port"

$startInfo                       = [System.Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName              = $exePath
$startInfo.UseShellExecute       = $false
# Do NOT redirect stdout/stderr: the smoke test doesn't consume them, and leaving
# the pipe buffer unread would deadlock the server process if output exceeded ~4 KB.
$startInfo.Environment['FILE_SERVER_ROOT']      = $tempRoot
$startInfo.Environment['FILE_SERVER_HTTP_PORT'] = [string] $Port
$startInfo.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'

$proc = [System.Diagnostics.Process]::new()
$proc.StartInfo = $startInfo
$proc.Start() | Out-Null
Write-Host "[smoke] launched PID $($proc.Id)"

# ─── 5. Poll until port is open ───────────────────────────────────────────────
$deadline = [System.DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$portOpen  = $false
while ([System.DateTime]::UtcNow -lt $deadline) {
    if ($proc.HasExited) {
        Write-Error "[smoke] process exited early (code $($proc.ExitCode)) — boot crash"
        Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue
        exit 2
    }
    try {
        $tcp = [System.Net.Sockets.TcpClient]::new()
        $tcp.Connect('127.0.0.1', $Port)
        $tcp.Close()
        $portOpen = $true
        break
    } catch { }
    Start-Sleep -Milliseconds 250
}

if (-not $portOpen) {
    if (-not $proc.HasExited) { try { $proc.Kill() } catch { } }
    Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue
    Write-Error "[smoke] port $Port never opened within $TimeoutSeconds seconds — Kestrel did not start"
    exit 3
}
Write-Host "[smoke] port $Port open"

# ─── 6. HTTP GET / — expect 302 redirect ──────────────────────────────────────
$redirectOk = $false
try {
    $response = Invoke-WebRequest -Uri "http://localhost:$Port/" -MaximumRedirection 0 -ErrorAction SilentlyContinue
    if ($response.StatusCode -eq 302) {
        $location = $response.Headers['Location']
        Write-Host "[smoke] 302 redirect to: $location" -ForegroundColor Green
        $redirectOk = $true
    } else {
        Write-Host "[smoke] unexpected status $($response.StatusCode) (expected 302)" -ForegroundColor Red
    }
} catch {
    # PowerShell throws on non-2xx; parse the response from the exception.
    $webEx = $_.Exception
    if ($webEx.Response -and $webEx.Response.StatusCode -eq 302) {
        Write-Host "[smoke] 302 redirect confirmed (via exception path)" -ForegroundColor Green
        $redirectOk = $true
    } else {
        Write-Host "[smoke] HTTP request failed: $($webEx.Message)" -ForegroundColor Red
    }
}

# ─── 7. Kill and check for crash ──────────────────────────────────────────────
$crashedBeforeKill = $proc.HasExited
try {
    if (-not $proc.HasExited) {
        $proc.Kill()
        $proc.WaitForExit(5000) | Out-Null
    }
} catch {
    Write-Warning "[smoke] kill failed: $_"
}

Remove-Item -Recurse -Force -LiteralPath $tempRoot -ErrorAction SilentlyContinue

# ─── 8. Report ────────────────────────────────────────────────────────────────
if ($crashedBeforeKill) {
    Write-Error "[smoke] FAIL — process exited on its own (code $($proc.ExitCode)) before we could issue the HTTP request"
    exit 4
}

if (-not $redirectOk) {
    Write-Error "[smoke] FAIL — did not receive expected 302 redirect from GET /"
    exit 5
}

Write-Host "[smoke] PASS — Kestrel started, port $Port opened, GET / returned 302" -ForegroundColor Green
exit 0
