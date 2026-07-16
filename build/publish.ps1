# OneVolume publish script — self-contained single-file executables.
# Usage: .\build\publish.ps1 [-Runtime win-x64] [-Configuration Release]

param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root "dist\$Runtime"

Write-Host "Publishing OneVolume ($Configuration, $Runtime)…" -ForegroundColor Cyan
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

dotnet publish (Join-Path $root "src\OneVolume.App\OneVolume.App.csproj") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "App publish failed" }

Copy-Item (Join-Path $root "LICENSE") $dist -Force
Copy-Item (Join-Path $root "README.md") $dist -Force -ErrorAction SilentlyContinue

Get-ChildItem $dist -File | ForEach-Object {
    Write-Host ("  {0,-22} {1,10:N1} MB" -f $_.Name, ($_.Length / 1MB))
}

Write-Host "`nDone → $dist" -ForegroundColor Green
