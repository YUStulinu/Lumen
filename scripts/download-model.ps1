<#
.SYNOPSIS
    Downloads an ONNX Runtime GenAI model from Hugging Face into %LOCALAPPDATA%\Lumen\models.

.DESCRIPTION
    Uses only what ships with Windows (PowerShell + curl.exe): no Python or huggingface-cli needed.
    The default is Phi-4-mini-instruct, CPU int4 variant (about 4.7 GB on disk).
    When it finishes, it prints the folder to paste into Lumen -> Settings -> Local model.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/download-model.ps1

.EXAMPLE
    # Another model / variant from the same family of repositories:
    powershell -ExecutionPolicy Bypass -File scripts/download-model.ps1 `
        -Repository microsoft/Phi-3.5-mini-instruct-onnx -Variant cpu_and_mobile/cpu-int4-awq-block-128-acc-level-4
#>
param(
    [string]$Repository = "microsoft/Phi-4-mini-instruct-onnx",
    [string]$Variant = "cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4"
)

$ErrorActionPreference = 'Stop'
$repoName = $Repository.Split('/')[-1]
$destination = Join-Path $env:LOCALAPPDATA ("Lumen\models\$repoName\" + $Variant.Replace('/', '\'))
New-Item -ItemType Directory -Force $destination | Out-Null

Write-Host "Listing files of $Repository/$Variant ..." -ForegroundColor Cyan
$files = Invoke-RestMethod "https://huggingface.co/api/models/$Repository/tree/main/$Variant"
$files = $files | Where-Object { $_.type -eq 'file' -and $_.path -notlike '*.py' }
$totalGb = ($files | Measure-Object size -Sum).Sum / 1GB
Write-Host ("{0} files, {1:N1} GB" -f $files.Count, $totalGb)

foreach ($file in $files) {
    $name = Split-Path $file.path -Leaf
    $target = Join-Path $destination $name
    if ((Test-Path $target) -and (Get-Item $target).Length -eq $file.size) {
        Write-Host "  $name (already downloaded)"
        continue
    }

    Write-Host ("  {0} ({1:N0} MB)" -f $name, ($file.size / 1MB))
    # -C - resumes an interrupted download of a large file.
    curl.exe -L --fail --retry 5 -C - -o $target "https://huggingface.co/$Repository/resolve/main/$($file.path)"
    if ($LASTEXITCODE -ne 0) { throw "Download of $name failed" }
}

Write-Host "`nDone. In Lumen -> Settings -> Local model, use this folder:" -ForegroundColor Green
Write-Host $destination
