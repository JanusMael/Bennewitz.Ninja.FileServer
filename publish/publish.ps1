<#
.SYNOPSIS
    Multi-RID publish orchestrator for Bennewitz.Ninja.FileServer.

.DESCRIPTION
    Drives Publish-Rid.ps1 across every supported RID, prompting the user
    before each one so a developer can cherry-pick the architectures they
    actually need (or quit partway through). Pass -All to skip prompts and
    build every RID unattended — this is the CI / release-cut mode.

    Script layout (all scripts live under publish/ at the repo root):

        publish.ps1                   (THIS FILE — orchestrator)
            └── Publish-Rid.ps1       (single-RID worker)
                ▲
        publish-<rid>.ps1             (thin per-RID wrappers that call
                                       Publish-Rid.ps1 directly)

    The orchestrator:
      1. Wipes dist/ and bin/obj/ once up front so all RIDs land on a
         clean slate. Per-RID scripts DO NOT need to clean again.
      2. For each RID, prompts [Y/n/a/q] (unless -All or -Rids explicitly
         narrows the set). Default on Enter is Yes.
      3. Invokes Publish-Rid.ps1 WITHOUT -Clean (orchestrator already did).
      4. Prints a summary table: Rid | ExitCode | Archive.

    Run from any directory:
        pwsh publish/publish.ps1
        pwsh publish/publish.ps1 -All
        pwsh publish/publish.ps1 -Rids win-x64,linux-x64

.PARAMETER All
    Build every RID in $Rids without prompting. Typical CI usage:
      `pwsh publish/publish.ps1 -All`

.PARAMETER Rids
    Restrict the orchestration to this subset of RIDs. Still prompts per
    RID unless -All is also specified. Defaults to all six supported RIDs.

.EXAMPLE
    # Interactive: step through all six RIDs, answering [Y/n/a/q] for each.
    pwsh publish/publish.ps1

.EXAMPLE
    # Unattended: build every RID, no prompts. Release-cut / CI mode.
    pwsh publish/publish.ps1 -All

.EXAMPLE
    # Interactive, but only offer the two Windows RIDs.
    pwsh publish/publish.ps1 -Rids win-x64,win-arm64
#>

[CmdletBinding()]
param(
    [switch] $All,

    [ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string[]] $Rids = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot is publish/ — the repo root is one level up.
$repoRoot   = Split-Path $PSScriptRoot -Parent
$srcRoot    = Join-Path $repoRoot 'src'
$distFolder = Join-Path $PSScriptRoot 'dist'
$logFolder  = Join-Path $distFolder 'logs'
$worker     = Join-Path $PSScriptRoot 'Publish-Rid.ps1'

Set-Location $repoRoot

# ── 1. Global pre-clean ─────────────────────────────────────────────────────
# Wipe dist/ (all prior zips, logs, staging) and every bin/obj under src/.
#
# We use a manual bin/obj wipe rather than `dotnet clean` because the Release
# config sets --self-contained true, which makes the SDK auto-inject the host
# RID into the clean-time restore graph evaluation. If the assets file left
# over from the previous run's LAST-published RID does not contain a target for
# the current host RID — which it won't, because each `dotnet publish -r <rid>`
# rewrites project.assets.json for just that RID — then `dotnet clean` fails
# with NETSDK1047. A manual wipe sidesteps that chicken-and-egg entirely.
# Scoping to $srcRoot avoids touching publish/ scripts or docs at repo root.
Write-Host "Cleaning previous build artifacts..." -ForegroundColor Magenta
if (Test-Path $distFolder) { Remove-Item -Recurse -Force $distFolder }
New-Item -ItemType Directory -Path $distFolder -Force | Out-Null
New-Item -ItemType Directory -Path $logFolder  -Force | Out-Null

Write-Host "  Wiping bin/ and obj/ under src/..." -ForegroundColor DarkGray
$cleanTargets = Get-ChildItem -Path $srcRoot -Directory -Recurse -Force `
        -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -eq 'bin' -or $_.Name -eq 'obj' } |
    Select-Object -ExpandProperty FullName
foreach ($dir in $cleanTargets) {
    Remove-Item -Recurse -Force -LiteralPath $dir -ErrorAction SilentlyContinue
}

# ── 2. Per-RID loop with optional prompting ─────────────────────────────────
function Read-BuildRidChoice {
    param([string] $Rid)
    while ($true) {
        Write-Host ("Build {0}? [Y]es / [N]o / [A]ll remaining / [Q]uit: " -f $Rid) `
            -ForegroundColor Cyan -NoNewline
        $answer = (Read-Host).Trim().ToLowerInvariant()
        switch ($answer) {
            ''     { return 'Yes'  }   # Enter = default Yes
            'y'    { return 'Yes'  }
            'yes'  { return 'Yes'  }
            'n'    { return 'No'   }
            'no'   { return 'No'   }
            'a'    { return 'All'  }
            'all'  { return 'All'  }
            'q'    { return 'Quit' }
            'quit' { return 'Quit' }
            default {
                Write-Host "  Unknown response '$answer' — please enter Y/N/A/Q." -ForegroundColor Yellow
            }
        }
    }
}

$buildAllRemaining = [bool] $All
$results           = [System.Collections.Generic.List[object]]::new()
$abort             = $false

foreach ($rid in $Rids) {
    if ($abort) { break }

    # Translate the interactive choice into a local action variable and act on
    # it outside the switch — `continue`/`break` inside a switch block act on
    # the switch, not the enclosing foreach.
    $action = 'Build'
    if (-not $buildAllRemaining) {
        switch (Read-BuildRidChoice -Rid $rid) {
            'Yes'  { $action = 'Build' }
            'No'   { $action = 'Skip'  }
            'All'  { $action = 'Build'; $buildAllRemaining = $true }
            'Quit' { $action = 'Abort' }
        }
    }

    if ($action -eq 'Skip') {
        Write-Host "  Skipping $rid." -ForegroundColor DarkGray
        continue
    }
    if ($action -eq 'Abort') {
        Write-Host "  Aborting remaining RIDs." -ForegroundColor DarkGray
        $abort = $true
        continue
    }

    # -Clean is NOT passed — the orchestrator already wiped bin/obj/dist.
    $result = & $worker -Rid $rid -DistFolder $distFolder
    $results.Add($result)
}

# ── 3. Summary table ────────────────────────────────────────────────────────
if ($results.Count -gt 0) {
    Write-Host "`nResults:" -ForegroundColor Green
    $results | Format-Table Rid, ExitCode, @{
        Name       = 'Archive'
        Expression = { if ($_.ArchivePath) { Split-Path $_.ArchivePath -Leaf } else { '(none)' } }
    } | Out-Host
    Write-Host "Output archives: $distFolder" -ForegroundColor Green
    Write-Host "Publish logs:    $logFolder"  -ForegroundColor Gray
}
else {
    Write-Host "`nNo RIDs were built." -ForegroundColor DarkYellow
}
