<#
.SYNOPSIS
    Packs the component into a local NuGet feed for testing it the way a consumer would.

.DESCRIPTION
    Building against the project directly proves less than it looks: a ProjectReference resolves
    types the compiler can see anyway, while the package has to carry its compiled views, its
    embedded assets, and its dependency list on its own. Installing the .nupkg is the only way to
    find out whether it does.

    Every pack gets a unique prerelease version. NuGet caches a package id and version pair in
    ~/.nuget/packages and will not look at the feed again once it has one, so re-packing the same
    version leaves the consumer building against the previous bits — the single most misleading
    failure mode in local package testing. A timestamped version sidesteps it, and the cached
    copies are evicted as well for anyone who pins a version by hand.

.PARAMETER FeedPath
    Directory to publish the .nupkg into. Defaults to publish/local-feed.

.PARAMETER Version
    Version to stamp. Defaults to a timestamped local prerelease, e.g. 2026.8.18-local.1435.

.PARAMETER KeepCache
    Leave previously cached copies of the package in the global packages folder.

.EXAMPLE
    pwsh publish/Pack-Local.ps1

.EXAMPLE
    pwsh publish/Pack-Local.ps1 -Version 2026.8.18-rc.1
#>
[CmdletBinding()]
param(
    [string] $FeedPath,
    [string] $Version,
    [switch] $KeepCache
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path $PSScriptRoot -Parent
$projectDir = Join-Path $repoRoot 'src' 'Bennewitz.Ninja.FileServer'
$packageId  = 'Bennewitz.Ninja.FileServer'

if (-not $FeedPath) { $FeedPath = Join-Path $PSScriptRoot 'local-feed' }

# The time is prefixed with 't' on purpose. A prerelease identifier made only of digits is
# numeric under SemVer, and numeric identifiers may not carry a leading zero — so any pack
# before 10:00 would produce an invalid version. NuGet reports that as
# "RestoreTask returned false but did not log an error", which names neither the version nor
# the rule, so the letter is cheaper than the afternoon.
if (-not $Version) { $Version = '{0}-local.t{1}' -f (Get-Date -Format 'yyyy.M.d'), (Get-Date -Format 'HHmm') }

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') {
    Write-Error "[pack] '$Version' is not a valid SemVer 2.0 version. NuGet reports this as an unrelated restore failure."
    exit 1
}

foreach ($identifier in ($Version -split '-', 2 | Select-Object -Skip 1) -split '\.') {
    if ($identifier -match '^0\d+$') {
        Write-Error "[pack] Prerelease identifier '$identifier' in '$Version' is numeric with a leading zero, which SemVer forbids. Prefix it with a letter."
        exit 1
    }
}

New-Item -ItemType Directory -Force -Path $FeedPath | Out-Null

# The version generator reads PublicVersion for assembly attributes and the packaging section
# maps it to the NuGet version, so one value keeps the two in step.
Write-Host "[pack] $packageId $Version -> $FeedPath" -ForegroundColor Cyan

& dotnet pack $projectDir -c Release -o $FeedPath -p:PublicVersion=$Version --nologo

if ($LASTEXITCODE -ne 0) {
    Write-Error "[pack] dotnet pack failed with exit code $LASTEXITCODE."
    exit $LASTEXITCODE
}

if (-not $KeepCache) {
    $cached = Join-Path $env:USERPROFILE '.nuget' 'packages' $packageId.ToLowerInvariant()
    if (Test-Path $cached) {
        Remove-Item -Recurse -Force $cached
        Write-Host "[pack] Evicted cached copies from the global packages folder." -ForegroundColor DarkGray
    }
}

$package = Join-Path $FeedPath "$packageId.$Version.nupkg"

Write-Host ''
Write-Host "[pack] Package: $package" -ForegroundColor Green
Write-Host ''
Write-Host 'Consume it from a sample application:' -ForegroundColor Cyan
Write-Host "  dotnet nuget add source `"$FeedPath`" --name bnfs-local   # once per machine"
Write-Host "  dotnet add package $packageId --version $Version"
Write-Host ''
