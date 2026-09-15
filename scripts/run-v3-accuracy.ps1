# V3 (LTC sync accuracy): run the VB-CABLE accuracy harness for one or both backends
# and print the two metrics the plan defines: steady error and recovery time.
#
#   powershell -File scripts\run-v3-accuracy.ps1 -Backends gst -Label after-d2
#
# NOTE: array parameters do not survive `powershell -File` (the whole list arrives
# as one string and fails ValidateSet), so call this once per backend.
#
# Each run needs its own empty report directory (phases.jsonl uses FileMode.CreateNew)
# and a settings.json placed there BEFORE the run to select the backend.
#
# LTC fps matrix: -LtcFps selects the signal rate (24/25/29.97/30, default 25).
# -LtcFpsMode selects the app's LtcFpsModeCombo: auto (default, detection is logged
# and checked) or fixed (the matching Fixed24/25/29_97/30 mode). Non-drop 29.97 is
# generated; the app cannot auto-distinguish it from 30 (drop-frame flag is false),
# so use -LtcFpsMode fixed for that combination if the auto run misresolves.
# The run name includes the LTC fps token so matrix runs never share a directory.
#
# Steady error excludes black-sweep (no correct frame exists during a black gap) and
# settling samples. Recovery time is swept over tolerances because a tolerance below
# the steady error can never be reached - see the "V3 criteria" section in
# docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md.
#
# outputBackend=1 (Gpu compositor) is the V3 validation target. With the accuracy trace
# enabled the app records samples from the published GPU texture
# (OutputEngine.RecordGpuAccuracyFrame), so the GPU path is measurable. The older note
# that required outputBackend=0 (WriteableBitmap sampling only) is obsolete (2026-09-15).
#
# NOTE: keep this file ASCII-only and BOM-less like the other scripts in this repo.
[CmdletBinding()]
param(
    [ValidateSet('gst', 'mpv')][string[]]$Backends = @('gst', 'mpv'),
    [Parameter(Mandatory)][string]$Label,
    [ValidateSet('24', '25', '29.97', '30')][string]$LtcFps = '25',
    [ValidateSet('auto', 'fixed')][string]$LtcFpsMode = 'auto',
    [ValidateSet('smooth', 'jump')][string]$SyncCorrectionMode = 'smooth',
    [string]$TestProject = 'C:\Users\codea\Documents\timecode-sync-player-wt-verify-oe-20260911-1344\tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj',
    [string]$LogRoot = 'C:\Users\codea\Documents\timecode-sync-player-wt-integrate-20260912\TestResults\v3',
    [int]$Repeats = 1,
    # 0 = Cpu compositor, 1 = Gpu compositor. Default 1: V3 judges the shipping GPU path.
    [ValidateSet(0, 1)][int]$OutputBackend = 1
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$analyzer = Join-Path $PSScriptRoot 'analyze-sync-accuracy.py'
$fpsToken = $LtcFps -replace '\.', '_'

$runs = @()
foreach ($backend in $Backends) {
    foreach ($i in 1..$Repeats) {
        $name = '{0}-ltc{1}-{2}{3}' -f $Label, $fpsToken, $backend, $(if ($Repeats -gt 1) { "-$i" } else { '' })
        $report = Join-Path $LogRoot $name
        if (Test-Path $report) { Remove-Item $report -Recurse -Force }
        New-Item -ItemType Directory -Force $report | Out-Null
        $backendValue = if ($backend -eq 'gst') { 1 } else { 0 }
        $correctionValue = if ($SyncCorrectionMode -eq 'jump') { 1 } else { 0 }
        ('{"backend":' + $backendValue + ',"outputBackend":' + $OutputBackend + ',"syncCorrectionMode":' + $correctionValue + '}') |
            Set-Content (Join-Path $report 'settings.json') -Encoding UTF8

        Write-Output ('=== {0} {1} ltcFps={2} mode={3} correction={4}' -f (Get-Date).ToString('HH:mm:ss'), $name, $LtcFps, $LtcFpsMode, $SyncCorrectionMode)
        $env:TIMECODE_ACCURACY_REPORT_DIR = $report
        # Also emit the output trace into the same directory, so one run feeds both
        # tables: the accuracy CSV (steady error / recovery) and the per-stage / per-seek
        # breakdown (analyze-v3-spread-stages.py and analyze-v3-seek-breakdown.py read
        # events.jsonl from the run root). No name clash: OutputTrace writes
        # manifest.json / events.jsonl / summary.json, none of which the harness writes.
        $env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE = $report
        $env:TCS_V3_LTC_FPS = $LtcFps
        $env:TCS_V3_LTC_FPS_MODE = $LtcFpsMode
        & dotnet test $TestProject -c Debug --no-build --filter 'FullyQualifiedName~SyncAccuracy' 2>&1 |
            Select-String -Pattern 'Passed!|Failed!|:\s+\d+' | Select-Object -Last 1 | ForEach-Object { '    ' + $_.ToString().Trim() }
        Remove-Item Env:TIMECODE_ACCURACY_REPORT_DIR -ErrorAction SilentlyContinue
        Remove-Item Env:TIMECODE_SYNC_PLAYER_OUTPUT_TRACE -ErrorAction SilentlyContinue
        Remove-Item Env:TCS_V3_LTC_FPS -ErrorAction SilentlyContinue
        Remove-Item Env:TCS_V3_LTC_FPS_MODE -ErrorAction SilentlyContinue

        & python $analyzer --trace (Join-Path $report 'trace.jsonl') --fixture (Join-Path $report 'fixture.json') `
            --phases (Join-Path $report 'phases.jsonl') --output (Join-Path $report 'analysis') 2>&1 |
            Select-Object -Last 1 | ForEach-Object { '    ' + $_ }
        $runs += [pscustomobject]@{ Name = $name; Backend = $backend; Report = $report }
        Start-Sleep -Seconds 3
    }
}

Write-Output ''
Write-Output '=== steady error (black-sweep excluded, settling excluded) ==='
$summarize = Join-Path $PSScriptRoot 'summarize-v3-accuracy.py'
foreach ($run in $runs) { & python $summarize (Join-Path $run.Report 'analysis\accuracy-samples.csv') $run.Name }
