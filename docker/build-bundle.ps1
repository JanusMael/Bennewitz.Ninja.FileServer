[CmdletBinding()]
param(
    [string]$Tag = "file-server-bundle"
)

$repoRoot  = Split-Path $PSScriptRoot -Parent
$filesRoot = Join-Path $repoRoot "FilesRoot"

if (-not (Test-Path $filesRoot -PathType Container)) {
    Write-Error "ERROR: 'FilesRoot/' directory not found at the repository root.`nCreate it and populate it with files to bake into the image, then re-run."
    exit 1
}

$fileCount = (Get-ChildItem $filesRoot -Recurse -File).Count
Write-Host "Baking $fileCount file(s) from FilesRoot/ into image '$Tag'..."

docker build -f "$PSScriptRoot/Dockerfile.bundle" -t $Tag $repoRoot
