#requires -Version 7.0
# Self-test of scripts\LtcRunMetrics.psm1 and the fields it adds to run-result.json
# (v0.6.0 stage 5b: commitFreeGbAtStart, cDriveFreeGbAtStart, appExit, prores). Builds a small fake report
# directory under %TEMP%, runs ltc-run-report.ps1 on it (reads that folder only, no
# -Prune) and checks the JSON. Starts no app and no test run; removes the folder at the end.
#
#   pwsh -NoProfile -File scripts\test-ltc-run-metrics.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LtcRunMetrics.psm1') -Force

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output ('ok   ' + $Name) } else { Write-Output ('FAIL ' + $Name); $failures.Add($Name) }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-ltc-run-metrics-' + [Guid]::NewGuid().ToString('N'))
try {
    # Times: the run starts 10:00:00 local. Scenario C-1 exits in 0.25 s, C-2 in 17.5 s (slow),
    # C-3 was never seen exiting (waited 25 s, then killed), S-1 exited within the first wait.
    $day = [DateTime]::new(2026, 9, 30, 10, 0, 0, [DateTimeKind]::Local)
    $offset = [TimeZoneInfo]::Local.GetUtcOffset($day)
    $zone = ($(if ($offset -lt [TimeSpan]::Zero) { '-' } else { '+' })) + $offset.ToString('hh\:mm')
    function AppLine([DateTime]$At, [string]$Text) { $At.ToString('yyyy-MM-dd HH:mm:ss.fff') + ' ' + $zone + ' [INF] ' + $Text }
    function ShimLine([DateTime]$At, [string]$Text) { $At.ToString('yyyy-MM-dd HH:mm:ss.fff') + ' [tcs-gst] ' + $Text }
    function Utc([DateTime]$At) { $At.ToUniversalTime().ToString('o') }

    New-Item -ItemType Directory -Force -Path (Join-Path $root 'app-logs') | Out-Null
    [ordered]@{ commitFreeGbAtStart = 18.52; cDriveFreeGbAtStart = 47.25; measuredAt = $day.ToString('o') } | ConvertTo-Json |
        Set-Content -LiteralPath (Join-Path $root 'runner-preflight.json') -Encoding UTF8
    @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="$($day.ToString('o'))" start="$($day.ToString('o'))" finish="$($day.AddMinutes(10).ToString('o'))" />
  <Results>
    <UnitTestResult testName="Ns.LtcScenarioE2ETests.C1_Test" outcome="Passed" />
  </Results>
</TestRun>
"@ | Set-Content -LiteralPath (Join-Path $root 'results.trx') -Encoding UTF8

    $c2Press = $day.AddMinutes(2)
    $app = @(
        (AppLine $day.AddHours(-1) '終了手順: 資源解放'),                      # an earlier run of the same day
        (AppLine $c2Press.AddMilliseconds(5) '終了手順: 新規受付停止'),
        (AppLine $c2Press.AddMilliseconds(6) '終了手順: GStreamer 停止'),
        (AppLine $c2Press.AddMilliseconds(20) 'unrelated line inside the window'),
        (AppLine $c2Press.AddSeconds(17) '終了手順: 資源解放'),
        (AppLine $c2Press.AddSeconds(60) '終了手順: 新規受付停止')             # the next scenario
    )
    [IO.File]::WriteAllLines((Join-Path $root 'app-logs\timecodesyncplayer-20260930.log'), $app)
    $shimDaily = @(
        (ShimLine $day.AddHours(-1) 'load.summary path=C:\m\old.mov paused=1 total_ms=50.0 attempt=0 profile=prores-gpu'),
        (ShimLine $day.AddMinutes(1) 'load.summary path=C:\m\a b.mov paused=1 total_ms=50.0 attempt=0 profile=prores-gpu'),
        (ShimLine $day.AddMinutes(1) 'load.summary path=C:\m\h.mp4 paused=1 total_ms=9.0 attempt=0 profile=h264-gpu'),
        (ShimLine $day.AddMinutes(1) 'load.skip path=C:\m\h.mp4 attempt=0 profile=h264-gpu reason=decoder-adapter-mismatch shim_luid=0:1')
    )
    [IO.File]::WriteAllLines((Join-Path $root 'app-logs\tcs-gst-20260930.log'), $shimDaily)

    function Scenario([string]$Name, [object[]]$Events, [string[]]$RawShim) {
        $dir = Join-Path $root ('scenarios\' + $Name)
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $lines = foreach ($e in $Events) {
            [ordered]@{ timestampUtc = (Utc $day); event = 'app-exit-timing'; details = $e } | ConvertTo-Json -Compress -Depth 4
        }
        [IO.File]::WriteAllLines((Join-Path $dir 'harness.jsonl'), [string[]]@($lines))
        if ($RawShim) { [IO.File]::WriteAllLines((Join-Path $dir 'tcs-gst-raw.log'), $RawShim) }
    }
    $c1Press = $day.AddMinutes(1)
    Scenario 'C-1-20260930-100100' @(
        [ordered]@{ phase = 'verify'; exited = $true; pressedAtUtc = (Utc $c1Press); exitedAtUtc = (Utc $c1Press.AddSeconds(0.25)); seconds = 0.25 }
    ) @(
        (ShimLine $c1Press 'load.summary path=C:\m\x.mov paused=0 total_ms=40.0 attempt=1 profile=prores-cpu'),
        (ShimLine $c1Press 'load.fail profile=prores-gpu reason=decoder-adapter-mismatch same=0 read=1 want=2 (forced)'),
        (ShimLine $c1Press.AddSeconds(0.1) 'destroy: enter')
    )
    Scenario 'C-2-20260930-100200' @(
        [ordered]@{ phase = 'verify'; exited = $false; pressedAtUtc = (Utc $c2Press); seconds = $null; waitedSeconds = 15.02 },
        [ordered]@{ phase = 'dispose'; exited = $true; pressedAtUtc = (Utc $c2Press); exitedAtUtc = (Utc $c2Press.AddSeconds(17.5)); seconds = 17.5 }
    ) @(
        (ShimLine $c2Press.AddMilliseconds(30) 'destroy: enter'),
        (ShimLine $c2Press.AddMilliseconds(31) 'destroy: bus join begin joinable=1'),
        (ShimLine $c2Press.AddSeconds(16.9) 'destroy: exit elapsed_ms=16870.0'),
        (ShimLine $c2Press.AddSeconds(16.9) 'load.summary path=C:\m\y.mov paused=0 total_ms=40.0 attempt=0 profile=prores-gpu')
    )
    $c3Press = $day.AddMinutes(4)
    Scenario 'C-3-20260930-100400' @(
        [ordered]@{ phase = 'verify'; exited = $false; pressedAtUtc = (Utc $c3Press); seconds = $null; waitedSeconds = 15.0 },
        [ordered]@{ phase = 'dispose'; exited = $false; pressedAtUtc = (Utc $c3Press); seconds = $null; waitedSeconds = 25.1 }
    ) $null
    $s1Press = $day.AddMinutes(5)
    Scenario 'S-1-20260930-100500' @(
        [ordered]@{ phase = 'dispose'; exited = $true; requestedAtUtc = (Utc $s1Press); exitedAtUtc = (Utc $s1Press.AddSeconds(1.0)); seconds = 1.0 }
    ) $null

    # ---- the functions ----
    $median = Get-TcsMedian @(3.0, 1.0, 2.0, 10.0)
    Check ($median -eq 2.5) 'median of an even count is the mean of the middle two'
    Check ((Get-TcsMedian @()) -eq $null) 'median of nothing is null'
    Check ((Get-TcsLogLineUtc (AppLine $day 'x')) -eq $day.ToUniversalTime()) 'app log time (with offset) as UTC'
    Check ((Get-TcsLogLineUtc (ShimLine $day 'x')) -eq $day.ToUniversalTime()) 'shim log time (local) as UTC'

    # ---- ltc-run-report.ps1 on the fake folder ----
    $out = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'ltc-run-report.ps1') -ReportDir $root 2>&1 | ForEach-Object { [string]$_ })
    Check ($LASTEXITCODE -eq 0) ('ltc-run-report.ps1 exit 0 (' + ($out -join ' | ') + ')')
    $json = Get-Content -LiteralPath (Join-Path $root 'run-result.json') -Raw | ConvertFrom-Json
    Check ($json.schema -eq 'ltc-run-result/1') 'schema unchanged'
    Check ($json.commitFreeGbAtStart -eq 18.52) 'commitFreeGbAtStart from runner-preflight.json'
    Check ($json.cDriveFreeGbAtStart -eq 47.25) 'cDriveFreeGbAtStart from runner-preflight.json'
    Check ($json.prores.gpu -eq 2) 'prores.gpu: 2 (the line before the trx start is not counted; a path with a space counts)'
    Check ($json.prores.cpu -eq 1) 'prores.cpu: 1'
    Check ($json.prores.adapterMismatch -eq 1) 'prores.adapterMismatch: load.fail only (D16-b load.skip is not counted)'
    Check ($json.appExit.count -eq 3) 'appExit.count: 3 measured exits (C-1, C-2 from the dispose wait, S-1)'
    Check ($json.appExit.medianSeconds -eq 1.0) 'appExit.medianSeconds: 1.0'
    Check ($json.appExit.maxSeconds -eq 17.5) 'appExit.maxSeconds: 17.5'
    $over = @($json.appExit.over15s)
    Check ($over.Count -eq 2) 'appExit.over15s: C-2 (17.5 s) and C-3 (not seen, 25.1 s)'
    $c2 = $over | Where-Object { $_.test -eq 'C-2' }
    Check ($null -ne $c2 -and $c2.seconds -eq 17.5 -and $c2.exited) 'C-2 over15s entry'
    $c2Lines = @($c2.logLines)
    Check ($c2Lines.Count -eq 6) ('C-2 logLines: 3 exit-step app lines + 3 destroy lines, sorted, no duplicates (got ' + $c2Lines.Count + ')')
    Check (@($c2Lines | Where-Object { $_ -match 'unrelated|load\.summary' }).Count -eq 0) 'C-2 logLines: only exit-stage lines'
    Check ($c2Lines[0] -match '新規受付停止' -and $c2Lines[-1] -match '資源解放') 'C-2 logLines: first and last in time order'
    $c3 = $over | Where-Object { $_.test -eq 'C-3' }
    Check ($null -ne $c3 -and -not $c3.exited -and $c3.seconds -eq 25.1) 'C-3 over15s entry: exited=false, seconds = waited (lower bound)'
    Write-Output ('--- appExit and prores of the fake run:')
    [ordered]@{ commitFreeGbAtStart = $json.commitFreeGbAtStart; cDriveFreeGbAtStart = $json.cDriveFreeGbAtStart; appExit = $json.appExit; prores = $json.prores } | ConvertTo-Json -Depth 6
} finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Output ('FAILED ' + $failures.Count)
    exit 1
}
Write-Output 'ALL OK'
