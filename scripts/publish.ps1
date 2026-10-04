<#
.SYNOPSIS
    Tests, publishes and packages Lumen: a portable .zip and (if Inno Setup is installed) a setup .exe.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
    powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -Version 1.2.0 -SkipTests
#>
param(
    [string]$Version = "1.0.0",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

$artifacts = Join-Path $root "artifacts"
$publishDir = Join-Path $artifacts "publish"
if (Test-Path $artifacts) { Remove-Item $artifacts -Recurse -Force }

if (-not $SkipTests) {
    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test tests/Lumen.Core.Tests -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

# Self-contained: the .NET runtime ships inside the app folder, so users install nothing else.
# ReadyToRun: methods are precompiled to native code, which shortens startup noticeably.
Write-Host "==> Publishing $Runtime" -ForegroundColor Cyan
dotnet publish src/Lumen.App -c Release -r $Runtime --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish failed" }

Write-Host "==> Creating portable zip" -ForegroundColor Cyan
$zip = Join-Path $artifacts "Lumen-$Version-$Runtime-portable.zip"
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip

$iscc = @(
    (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

if ($iscc) {
    Write-Host "==> Building installer with $iscc" -ForegroundColor Cyan
    & $iscc "/DMyAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$artifacts" "installer\Lumen.iss"
    if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }
} else {
    Write-Warning "Inno Setup not found; skipped the installer. Install it with: winget install JRSoftware.InnoSetup"
}

Write-Host "`nDone. Output in $artifacts" -ForegroundColor Green
Get-ChildItem $artifacts -File | ForEach-Object { "  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB) }
