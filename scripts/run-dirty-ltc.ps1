# M6: run the dirty-LTC limit harness for one condition plan and leave the evidence
# in one report directory per run.
#
#   powershell -File scripts\run-dirty-ltc.ps1 -Condition dry-clean
#   powershell -File scripts\run-dirty-ltc.ps1 -Condition A-noise -Repeats 2 -Label near-limit
#
# One run = one condition; the levels are played back to back inside the run by
# tests\TimecodeSyncPlayer.Tests\E2E\DirtyLtcE2ETests.cs. The plan json lives in
# scripts\m6-plans\, the app under test is unchanged, and all signal conditioning
# is done on the test side.
# With -Repeats N the condition is run N times into separate report dirs and the
# analyzer prints a run-to-run comparison (for the limit-neighbourhood repeats).
#
# Exit codes: 0 = all test runs passed, 1 = at least one test failure (partial
# traces stay in the report).
# NOTE: keep this file ASCII-only and BOM-less like the other scripts in this repo.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Condition,
    [string]$Label = '',
    [int]$Repeats = 1,
    [string]$ReportDir = '',
    [string]$TestProject = '',
    [string]$AppExe = '',
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$planPath = Join-Path $PSScriptRoot ('m6-plans\' + $Condition + '.json')
if (-not (Test-Path -LiteralPath $planPath)) { throw "plan not found: $planPath" }
if ($Repeats -lt 1) { throw "-Repeats must be >= 1" }
if ($Repeats -gt 1 -and $ReportDir) { throw "-ReportDir cannot be combined with -Repeats > 1" }
if (-not $TestProject) {
    $TestProject = Join-Path $repoRoot 'tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj'
}
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe'
}
if (-not (Test-Path -LiteralPath $AppExe)) { throw "App exe not found: $AppExe (pass -AppExe)" }
$AppExe = (Resolve-Path -LiteralPath $AppExe).Path

if (-not $SkipBuild) {
    & dotnet build $TestProject -c Debug
    if ($LASTEXITCODE) { throw "tests build failed ($LASTEXITCODE)" }
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$reports = @()
$exitCodes = @()
for ($i = 1; $i -le $Repeats; $i++) {
    $suffix = if ($Label) { '-' + $Label } else { '' }
    if ($Repeats -gt 1) { $suffix = $suffix + '-r' + $i }
    $runReport = if ($ReportDir) { $ReportDir } else {
        Join-Path $repoRoot ('TestResults\v3\m6-' + $Condition + $suffix + '-' + $stamp)
    }
    New-Item -ItemType Directory -Force -Path $runReport | Out-Null
    $runReport = (Resolve-Path -LiteralPath $runReport).Path
    $reports += $runReport

    # Gpu compositor + smooth correction, same as the V3 harness (backend key is obsolete).
    '{"outputBackend":1,"syncCorrectionMode":0}' | Set-Content (Join-Path $runReport 'settings.json') -Encoding UTF8

    $env:TIMECODE_SYNC_PLAYER_E2E_APP_PATH = $AppExe
    $env:TIMECODE_ACCURACY_REPORT_DIR = $runReport
    $env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE = $runReport
    $env:TCS_M6_DIRTY_PLAN = (Resolve-Path -LiteralPath $planPath).Path

    Write-Output "app=$AppExe"
    Write-Output "report=$runReport"
    Write-Output "plan=$($env:TCS_M6_DIRTY_PLAN)"

    $testStart = Get-Date
    & dotnet test $TestProject -c Debug --no-build --filter 'FullyQualifiedName~DirtyLtc' `
        --results-directory $runReport --logger 'trx;LogFileName=results.trx' `
        *> (Join-Path $runReport 'dotnet-test.log')
    $exitCodes += $LASTEXITCODE
    Get-Content -LiteralPath (Join-Path $runReport 'dotnet-test.log') -Tail 2 | ForEach-Object { Write-Output $_ }

    # Keep the app log lines for this run (decodedFrames/2s, sampleRate/bits, signal loss).
    $appLogDir = Join-Path (Split-Path $AppExe -Parent) 'logs'
    $appLogDest = Join-Path $runReport 'app-logs'
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

    Remove-Item Env:TIMECODE_ACCURACY_REPORT_DIR -ErrorAction SilentlyContinue
    Remove-Item Env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE -ErrorAction SilentlyContinue
    Remove-Item Env:TCS_M6_DIRTY_PLAN -ErrorAction SilentlyContinue
}

# Analysis runs offline (no machine needed).
$previousEncoding = [Console]::OutputEncoding
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $env:PYTHONIOENCODING = 'utf-8'
    $analyzer = Join-Path $PSScriptRoot 'analyze-dirty-ltc.py'
    foreach ($runReport in $reports) {
        & python $analyzer $runReport |
            Out-File -FilePath (Join-Path $runReport 'analysis-dirty.txt') -Encoding utf8
    }
    if ($reports.Count -gt 1) {
        & python $analyzer $reports |
            Out-File -FilePath (Join-Path $reports[0] 'analysis-dirty-combined.txt') -Encoding utf8
        Get-Content -LiteralPath (Join-Path $reports[0] 'analysis-dirty-combined.txt') -Encoding UTF8
    } else {
        Get-Content -LiteralPath (Join-Path $reports[0] 'analysis-dirty.txt') -Encoding UTF8
    }
} finally {
    if ($previousEncoding) { [Console]::OutputEncoding = $previousEncoding }
    Remove-Item Env:PYTHONIOENCODING -ErrorAction SilentlyContinue
}

$leftover = @(Get-Process -Name TimecodeSyncPlayer -ErrorAction SilentlyContinue)
$failedRuns = @($exitCodes | Where-Object { $_ -ne 0 }).Count
Write-Output ('SUMMARY test_exit=' + ($exitCodes -join ',') + ' failed_runs=' + $failedRuns +
    ' leftover=' + $leftover.Count + ' reports=' + ($reports -join ';'))
if ($failedRuns -gt 0) { exit 1 }
exit 0
