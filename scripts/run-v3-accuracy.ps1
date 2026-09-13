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
# Steady error excludes black-sweep (no correct frame exists during a black gap) and
# settling samples. Recovery time is swept over tolerances because a tolerance below
# the steady error can never be reached - see the "V3 criteria" section in
# docs/GSTREAMER-GPU-VALIDATION-PLAN-2026-09-12.md.
#
# NOTE: keep this file ASCII-only and BOM-less like the other scripts in this repo.
[CmdletBinding()]
param(
    [ValidateSet('gst', 'mpv')][string[]]$Backends = @('gst', 'mpv'),
    [Parameter(Mandatory)][string]$Label,
    [string]$TestProject = 'C:\Users\codea\Documents\timecode-sync-player-wt-verify-oe-20260911-1344\tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj',
    [string]$LogRoot = 'C:\Users\codea\Documents\timecode-sync-player-wt-integrate-20260912\TestResults\v3',
    [int]$Repeats = 1,
    # 0 = Cpu compositor, 1 = Gpu compositor. MUST stay 0: this harness samples the
    # WriteableBitmap and the Gpu path never writes one, so every sample comes back
    # as 'unexpected-black' and nothing is measurable. Measured 2026-09-13 with
    # outputBackend=1: 2243 samples, 0 measured, 1744 unexpected-black.
    # GPU-path timing needs the output trace instead (analyze-v3-seek-breakdown.py).
    [ValidateSet(0, 1)][int]$OutputBackend = 0
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$analyzer = Join-Path $PSScriptRoot 'analyze-sync-accuracy.py'

$runs = @()
foreach ($backend in $Backends) {
    foreach ($i in 1..$Repeats) {
        $name = '{0}-{1}{2}' -f $Label, $backend, $(if ($Repeats -gt 1) { "-$i" } else { '' })
        $report = Join-Path $LogRoot $name
        if (Test-Path $report) { Remove-Item $report -Recurse -Force }
        New-Item -ItemType Directory -Force $report | Out-Null
        $backendValue = if ($backend -eq 'gst') { 1 } else { 0 }
        ('{"backend":' + $backendValue + ',"outputBackend":' + $OutputBackend + '}') |
            Set-Content (Join-Path $report 'settings.json') -Encoding UTF8

        Write-Output ('=== {0} {1}' -f (Get-Date).ToString('HH:mm:ss'), $name)
        $env:TIMECODE_ACCURACY_REPORT_DIR = $report
        & dotnet test $TestProject -c Debug --no-build --filter 'FullyQualifiedName~SyncAccuracy' 2>&1 |
            Select-String -Pattern 'Passed!|Failed!|:\s+\d+' | Select-Object -Last 1 | ForEach-Object { '    ' + $_.ToString().Trim() }
        Remove-Item Env:TIMECODE_ACCURACY_REPORT_DIR -ErrorAction SilentlyContinue

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
