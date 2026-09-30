#requires -Version 7.0
# Aggregations for the LTC runner result (ltc-run-report.ps1, run-result.json schema
# ltc-run-result/1). Added in v0.6.0 stage 5b; the fields are additions only:
#   commitFreeGbAtStart  - system commit free when the runner started (runner-preflight.json)
#   appExit              - seconds from pressing Exit (BtnExitNormal) to the process being gone,
#                          from the harness event "app-exit-timing" of each scenario
#   prores               - ProRes loads by profile (prores-gpu / prores-cpu) and the
#                          decoder-adapter-mismatch firings, from the tcs-gst log
# Kept apart from ltc-run-report.ps1 so scripts\test-ltc-run-metrics.ps1 can check the
# functions on small fake artifacts without a run.

Set-StrictMode -Version 3.0

# A JSON date (ConvertFrom-Json turns ISO strings into DateTime) or a string, as UTC.
function ConvertTo-TcsUtc($Value) {
    if ($null -eq $Value) { return $null }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    if ($Value -is [DateTime]) {
        switch ($Value.Kind) {
            'Utc' { return $Value }
            'Local' { return $Value.ToUniversalTime() }
            default { return [DateTime]::SpecifyKind($Value, [DateTimeKind]::Utc) }
        }
    }
    $text = [string]$Value
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    return [DateTimeOffset]::Parse($text, [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
}

# Time of a log line as UTC, or $null. App (Serilog): "2026-09-30 15:17:39.462 +09:00 [INF] ...".
# Shim: "2026-09-30 15:17:39.513 [tcs-gst] ..." in the local time of the machine that wrote it
# (the report runs on the same machine as the test).
function Get-TcsLogLineUtc([string]$Line) {
    if ($Line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) ([+-]\d{2}:\d{2}) ') {
        return [DateTimeOffset]::ParseExact($matches[1] + ' ' + $matches[2], 'yyyy-MM-dd HH:mm:ss.fff zzz',
            [Globalization.CultureInfo]::InvariantCulture).UtcDateTime
    }
    if ($Line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3}) \[tcs-gst\]') {
        $local = [DateTime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss.fff', [Globalization.CultureInfo]::InvariantCulture)
        return [DateTime]::SpecifyKind($local, [DateTimeKind]::Local).ToUniversalTime()
    }
    return $null
}

# Start of the test run (trx Times/@start) as UTC, or $null. The copied logs are whole
# daily files, so lines older than this belong to earlier runs of the same day.
function Get-TcsRunStartUtc([string]$TrxPath) {
    if (-not $TrxPath -or -not (Test-Path -LiteralPath $TrxPath)) { return $null }
    [xml]$doc = Get-Content -LiteralPath $TrxPath
    $times = $doc.TestRun.Times
    if (-not $times -or -not $times.start) { return $null }
    return ConvertTo-TcsUtc ([string]$times.start)
}

function Select-TcsLinesSince([string[]]$Lines, $SinceUtc) {
    if ($null -eq $SinceUtc) { return @($Lines) }
    @($Lines | Where-Object {
        $at = Get-TcsLogLineUtc $_
        $null -eq $at -or $at -ge $SinceUtc
    })
}

# prores: { gpu, cpu, adapterMismatch }
#   gpu / cpu        - "load.summary ... profile=prores-gpu|prores-cpu" (one line per finished load)
#   adapterMismatch  - "load.fail profile=... reason=decoder-adapter-mismatch" (the v0.6.0 stage 2
#                      check that fails the prores-gpu attempt and moves on to prores-cpu). The
#                      D16-b "load.skip ... reason=decoder-adapter-mismatch" of the per-adapter
#                      H.264/HEVC classes is a different check and is not counted.
function Get-TcsProResLoadSummary {
    param([string[]]$ShimLines = @(), $SinceUtc = $null)
    $lines = Select-TcsLinesSince $ShimLines $SinceUtc
    [ordered]@{
        gpu = @($lines | Where-Object { $_ -match '\bload\.summary\b.*\sprofile=prores-gpu\s*$' }).Count
        cpu = @($lines | Where-Object { $_ -match '\bload\.summary\b.*\sprofile=prores-cpu\s*$' }).Count
        adapterMismatch = @($lines | Where-Object { $_ -match '\bload\.fail\b.*\sreason=decoder-adapter-mismatch\b' }).Count
    }
}

# A property of a parsed JSON object, or $null when it is missing (older journals).
function Get-TcsField($Object, [string]$Name) {
    if ($null -eq $Object) { return $null }
    $property = $Object.PSObject.Properties[$Name]
    if ($property) { return $property.Value }
    return $null
}

function Get-TcsMedian([double[]]$Values) {
    if ($Values.Count -eq 0) { return $null }
    $sorted = @($Values | Sort-Object)
    $mid = [int][math]::Floor($sorted.Count / 2)
    if ($sorted.Count % 2 -eq 1) { return $sorted[$mid] }
    return ($sorted[$mid - 1] + $sorted[$mid]) / 2
}

# Exit-stage lines of one slow exit: the app's ExitCoordinator lines ("終了手順: <step>" and its
# failure lines) and the shim's tcs_player_destroy step lines ("destroy: ... elapsed_ms="), from
# one second before the press to one second after the process was gone (or the wait ended).
function Get-TcsExitLogLines {
    param([string[]]$AppLines = @(), [string[]]$ShimLines = @(), [DateTime]$FromUtc, [DateTime]$ToUtc, [int]$Max = 80)
    $from = $FromUtc.AddSeconds(-1)
    $to = $ToUtc.AddSeconds(1)
    $picked = New-Object System.Collections.Generic.List[object]
    foreach ($line in $AppLines) {
        if ($line -notmatch '終了手順|終了の開始') { continue }
        $at = Get-TcsLogLineUtc $line
        if ($null -ne $at -and $at -ge $from -and $at -le $to) { $picked.Add([pscustomobject]@{ At = $at; Line = $line }) }
    }
    foreach ($line in $ShimLines) {
        if ($line -notmatch '\[tcs-gst\] destroy: ') { continue }
        $at = Get-TcsLogLineUtc $line
        if ($null -ne $at -and $at -ge $from -and $at -le $to) { $picked.Add([pscustomobject]@{ At = $at; Line = $line }) }
    }
    # The same shim line can come twice (the scenario's tcs-gst-raw.log and the whole list).
    @($picked | Sort-Object At | ForEach-Object { $_.Line } | Select-Object -Unique | Select-Object -First $Max)
}

# appExit: { count, medianSeconds, maxSeconds, over15s: [ { test, seconds, exited, logLines } ] }
# Events: one entry per "app-exit-timing" harness event, @{ test; scenario; details } where
# details = { phase, exited, pressedAtUtc, requestedAtUtc, exitedAtUtc, seconds, waitedSeconds }.
# A scenario can log two (the 15 s wait of VerifyAndExit, then the Dispose wait); its last
# event that saw the exit is used, else its last event. count/median/max cover the measured
# exits. An exit that was never seen (the process was killed) is listed in over15s with
# exited=false and seconds = how long it was waited for (a lower bound) when that is over the
# threshold. ShimLinesByScenario: scenario -> its tcs-gst-raw.log lines.
function Get-TcsAppExitSummary {
    param(
        [object[]]$Events = @(),
        [string[]]$AppLines = @(),
        [string[]]$ShimLines = @(),
        [hashtable]$ShimLinesByScenario = @{},
        [double]$ThresholdSeconds = 15
    )
    $byScenario = [ordered]@{}
    foreach ($e in $Events) {
        $key = [string]$e.scenario
        if (-not $byScenario.Contains($key)) { $byScenario[$key] = New-Object System.Collections.Generic.List[object] }
        $byScenario[$key].Add($e)
    }
    $measured = New-Object System.Collections.Generic.List[double]
    $over = New-Object System.Collections.Generic.List[object]
    foreach ($key in $byScenario.Keys) {
        $list = $byScenario[$key]
        $seen = @($list | Where-Object { (Get-TcsField $_.details 'exited') -and $null -ne (Get-TcsField $_.details 'seconds') })
        $pick = if ($seen.Count -gt 0) { $seen[-1] } else { $list[$list.Count - 1] }
        $d = $pick.details
        $pressedAt = Get-TcsField $d 'pressedAtUtc'
        $start = ConvertTo-TcsUtc $(if ($pressedAt) { $pressedAt } else { Get-TcsField $d 'requestedAtUtc' })
        $rawSeconds = Get-TcsField $d 'seconds'
        $waited = Get-TcsField $d 'waitedSeconds'
        $exited = [bool](Get-TcsField $d 'exited') -and $null -ne $rawSeconds
        $seconds = $null
        $end = $null
        if ($exited) {
            $seconds = [math]::Round([double]$rawSeconds, 3)
            $measured.Add($seconds)
            $end = ConvertTo-TcsUtc (Get-TcsField $d 'exitedAtUtc')
        } elseif ($null -ne $waited) {
            $seconds = [math]::Round([double]$waited, 3)
            if ($null -ne $start) { $end = $start.AddSeconds($seconds) }
        }
        if ($null -eq $seconds -or $seconds -le $ThresholdSeconds) { continue }
        $scenarioShim = @()
        if ($ShimLinesByScenario.ContainsKey($key)) { $scenarioShim = @($ShimLinesByScenario[$key]) }
        $logLines = @()
        if ($null -ne $start -and $null -ne $end) {
            $logLines = Get-TcsExitLogLines -AppLines $AppLines -ShimLines (@($scenarioShim) + @($ShimLines)) -FromUtc $start -ToUtc $end
        }
        $over.Add([ordered]@{ test = [string]$pick.test; seconds = $seconds; exited = $exited; logLines = @($logLines) })
    }
    $values = [double[]]$measured.ToArray()
    [ordered]@{
        count = $values.Count
        medianSeconds = $(if ($values.Count) { [math]::Round((Get-TcsMedian $values), 3) } else { $null })
        maxSeconds = $(if ($values.Count) { [math]::Round(($values | Measure-Object -Maximum).Maximum, 3) } else { $null })
        over15s = @($over.ToArray())
    }
}

# commitFreeGbAtStart from runner-preflight.json (written by run-ltc-scenarios.ps1), or $null.
function Get-TcsCommitFreeGbAtStart([string]$ReportDir) {
    $path = Join-Path $ReportDir 'runner-preflight.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-TcsField (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) 'commitFreeGbAtStart'
}

# System commit free in GB (Win32_OperatingSystem.FreeVirtualMemory is in KB: commit limit
# minus commit charge).
function Get-TcsSystemCommitFreeGb {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return [math]::Round([double]$os.FreeVirtualMemory / 1MB, 2)
}

Export-ModuleMember -Function Get-TcsField, ConvertTo-TcsUtc, Get-TcsLogLineUtc, Get-TcsRunStartUtc, Get-TcsProResLoadSummary,
    Get-TcsMedian, Get-TcsExitLogLines, Get-TcsAppExitSummary, Get-TcsCommitFreeGbAtStart, Get-TcsSystemCommitFreeGb
