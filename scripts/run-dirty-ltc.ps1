# M6: run the dirty-LTC limit harness for one condition plan and leave the evidence
# in one report directory.
#
#   powershell -File scripts\run-dirty-ltc.ps1 -Condition dry-clean
#   powershell -File scripts\run-dirty-ltc.ps1 -Condition A-noise -Label run2
#
# One run = one condition; the levels are played back to back inside the run by
# tests\TimecodeSyncPlayer.Tests\E2E\DirtyLtcE2ETests.cs. The plan json lives in
# scripts\m6-plans\, the app under test is unchanged, and all signal conditioning
# is done on the test side.
#
# Exit codes: 0 = test passed, 1 = test failure (partial traces stay in the report).
# NOTE: keep this file ASCII-only and BOM-less like the other scripts in this repo.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Condition,
    [string]$Label = '',
    [string]$ReportDir = '',
    [string]$TestProject = '',
    [string]$AppExe = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$planPath = Join-Path $PSScriptRoot ('m6-plans\' + $Condition + '.json')
if (-not (Test-Path -LiteralPath $planPath)) { throw "plan not found: $planPath" }
if (-not $TestProject) {
    $TestProject = Join-Path $repoRoot 'tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj'
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe'
}
if (-not (Test-Path -LiteralPath $AppExe)) { throw "App exe not found: $AppExe (pass -AppExe)" }
$AppExe = (Resolve-Path -LiteralPath $AppExe).Path

if (-not $ReportDir) {
    $suffix = if ($Label) { '-' + $Label } else { '' }
    $ReportDir = Join-Path $repoRoot ('TestResults\v3\m6-' + $Condition + $suffix + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path -LiteralPath $ReportDir).Path

# Gpu compositor + smooth correction, same as the V3 harness (backend key is obsolete).
'{"outputBackend":1,"syncCorrectionMode":0}' | Set-Content (Join-Path $ReportDir 'settings.json') -Encoding UTF8

$env:TIMECODE_SYNC_PLAYER_E2E_APP_PATH = $AppExe
$env:TIMECODE_ACCURACY_REPORT_DIR = $ReportDir
$env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE = $ReportDir
$env:TCS_M6_DIRTY_PLAN = (Resolve-Path -LiteralPath $planPath).Path

Write-Output "app=$AppExe"
Write-Output "report=$ReportDir"
Write-Output "plan=$($env:TCS_M6_DIRTY_PLAN)"

if (-not $SkipBuild) {
    & dotnet build $TestProject -c Debug
    if ($LASTEXITCODE) { throw "tests build failed ($LASTEXITCODE)" }
}

$testStart = Get-Date
& dotnet test $TestProject -c Debug --no-build --filter 'FullyQualifiedName~DirtyLtc' `
    --results-directory $ReportDir --logger 'trx;LogFileName=results.trx' `
    *> (Join-Path $ReportDir 'dotnet-test.log')
$testExit = $LASTEXITCODE
Get-Content -LiteralPath (Join-Path $ReportDir 'dotnet-test.log') -Tail 3 | ForEach-Object { Write-Output $_ }

# Keep the app log lines for this run (decodedFrames/2s, sampleRate/bits, signal loss).
$appLogDir = Join-Path (Split-Path $AppExe -Parent) 'logs'
$appLogDest = Join-Path $ReportDir 'app-logs'
New-Item -ItemType Directory -Force -Path $appLogDest | Out-Null
if (Test-Path -LiteralPath $appLogDir) {
    foreach ($log in @(Get-ChildItem -LiteralPath $appLogDir -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -like 'timecodesyncplayer-*.log' -or $_.Name -like 'tcs-gst-*.log') -and
            $_.LastWriteTime -ge $testStart.AddMinutes(-2)
        })) {
        Copy-Item -LiteralPath $log.FullName -Destination $appLogDest -Force
    }
}

# Analysis runs offline (no machine needed).
$previousEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $env:PYTHONIOENCODING = 'utf-8'
    & python (Join-Path $PSScriptRoot 'analyze-dirty-ltc.py') $ReportDir |
        Out-File -FilePath (Join-Path $ReportDir 'analysis-dirty.txt') -Encoding utf8
} finally {
    if ($previousEncoding) { [Console]::OutputEncoding = $previousEncoding }
    Remove-Item Env:PYTHONIOENCODING -ErrorAction SilentlyContinue
}
Get-Content -LiteralPath (Join-Path $ReportDir 'analysis-dirty.txt') -Encoding UTF8

Remove-Item Env:TIMECODE_ACCURACY_REPORT_DIR -ErrorAction SilentlyContinue
Remove-Item Env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE -ErrorAction SilentlyContinue
Remove-Item Env:TCS_M6_DIRTY_PLAN -ErrorAction SilentlyContinue

$leftover = @(Get-Process -Name TimecodeSyncPlayer -ErrorAction SilentlyContinue)
Write-Output ('SUMMARY test_exit=' + $testExit + ' leftover=' + $leftover.Count + ' report=' + $ReportDir)
if ($testExit -ne 0) { exit 1 }
exit 0
