#requires -Version 7.0
# Aggregations for the LTC runner result (ltc-run-report.ps1, run-result.json schema
# ltc-run-result/1). Added in v0.6.0 stage 5b; the fields are additions only:
#   commitFreeGbAtStart  - system commit free when the runner started (runner-preflight.json)
#   cDriveFreeGbAtStart  - free space on C: when the runner started (runner-preflight.json)
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
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($Name)) { return $Object[$Name] }
        return $null
    }
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

# A value of runner-preflight.json (written by run-ltc-scenarios.ps1: commitFreeGbAtStart,
# cDriveFreeGbAtStart), or $null.
function Get-TcsRunnerPreflightValue([string]$ReportDir, [string]$Name) {
    $path = Join-Path $ReportDir 'runner-preflight.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    return Get-TcsField (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) $Name
}

function Get-TcsCommitFreeGbAtStart([string]$ReportDir) { Get-TcsRunnerPreflightValue $ReportDir 'commitFreeGbAtStart' }

# Free space of a drive in GB (the release gate rule: 20 GB free on C: before a run).
function Get-TcsDriveFreeGb([string]$Drive = 'C:\') {
    return [math]::Round([double]([IO.DriveInfo]::new($Drive).AvailableFreeSpace) / 1GB, 2)
}

# System commit free in GB (Win32_OperatingSystem.FreeVirtualMemory is in KB: commit limit
# minus commit charge).
function Get-TcsSystemCommitFreeGb {
    $os = Get-CimInstance -ClassName Win32_OperatingSystem
    return [math]::Round([double]$os.FreeVirtualMemory / 1MB, 2)
}

# ---- crashes (test infrastructure 2026-10, item 2) ----------------------------------------
# v0.6.6: a rare crash right after a ProRes GPU load (.NET Runtime 1026, c0000005) showed up only
# as one timed-out LTC scenario ("CABLE Output enumeration; ltc=NaN"), and the event log was read
# by hand. The runner now saves the Application-log crash records of the run's time window
# (crash-events.json, Read-TcsCrashEventLog) and ltc-run-report.ps1 turns them into
# run-result.json "crashes", "launches" and "proresGpuFirstLaunches" (Get-TcsCrashSummary).
# One crash fails the run.

$script:TcsCrashAppName = 'TimecodeSyncPlayer.exe'

# One Application-log record (EventLogRecord) as a plain object for crash-events.json:
# { timeCreated (local ISO with offset), provider, id, values (the event properties as strings) }.
function ConvertTo-TcsCrashEventRecord($Event) {
    [ordered]@{
        timeCreated = ([DateTimeOffset]$Event.TimeCreated).ToString('o')
        provider = [string]$Event.ProviderName
        id = [int]$Event.Id
        values = @($Event.Properties | ForEach-Object { [string]$_.Value })
    }
}

# The .NET Runtime 1026 and Application Error 1000 records of the Application log between
# FromUtc and ToUtc (all applications; Get-TcsCrashSummary keeps TimecodeSyncPlayer only).
function Read-TcsCrashEventLog([DateTime]$FromUtc, [DateTime]$ToUtc) {
    $filter = @{
        LogName = 'Application'
        ProviderName = @('.NET Runtime', 'Application Error')
        Id = @(1026, 1000)
        StartTime = $FromUtc.ToLocalTime()
        EndTime = $ToUtc.ToLocalTime()
    }
    $events = @()
    try {
        $events = @(Get-WinEvent -FilterHashtable $filter -ErrorAction Stop)
    } catch {
        if ([string]$_.FullyQualifiedErrorId -like 'NoMatchingEventsFound*') { return @() }
        throw
    }
    @($events | ForEach-Object { ConvertTo-TcsCrashEventRecord $_ })
}

# A crash-events.json record as { atUtc, source ('1026' | '1000'), app, code, address, module,
# offset, pid, exception }, or $null when it is not a crash record of TimecodeSyncPlayer.exe.
#   1026 (.NET Runtime): one property, the message: "Application: <exe>" and
#        "Exception Info: exception code c0000005, exception address 00007FFB77D9088F"
#        (a managed exception has "Exception Info: System.X: ..." instead).
#   1000 (Application Error): properties 0 app, 3 module, 6 code, 7 offset, 8 process id
#        (read by position, so the localized message text does not matter).
function ConvertFrom-TcsCrashEventRecord($Record, [string]$AppName = $script:TcsCrashAppName) {
    if ($null -eq $Record) { return $null }
    $at = ConvertTo-TcsUtc (Get-TcsField $Record 'timeCreated')
    if ($null -eq $at) { return $null }
    $provider = [string](Get-TcsField $Record 'provider')
    $id = [int](Get-TcsField $Record 'id')
    $values = @(Get-TcsField $Record 'values' | ForEach-Object { [string]$_ })
    $item = [ordered]@{ atUtc = $at; source = ''; app = ''; code = $null; address = $null; module = $null;
        offset = $null; pid = $null; exception = $null }
    if ($provider -eq '.NET Runtime' -and $id -eq 1026) {
        $text = $values -join "`n"
        $item.source = '1026'
        if ($text -match '(?m)^Application:\s*(\S+)') { $item.app = $matches[1] }
        if ($text -match 'exception code ([0-9a-fA-F]+), exception address ([0-9a-fA-F]+)') {
            $item.code = $matches[1].ToLowerInvariant()
            $item.address = $matches[2].ToUpperInvariant()
        } elseif ($text -match '(?m)^Exception Info:\s*(.+?)\s*$') {
            $item.exception = $matches[1]
        }
    } elseif ($provider -eq 'Application Error' -and $id -eq 1000) {
        $item.source = '1000'
        if ($values.Count -gt 0) { $item.app = $values[0] }
        if ($values.Count -gt 3) { $item.module = $values[3] }
        if ($values.Count -gt 6) { $item.code = ($values[6] -replace '^0x', '').ToLowerInvariant() }
        if ($values.Count -gt 7) { $item.offset = ($values[7] -replace '^0x', '') }
        if ($values.Count -gt 8 -and $values[8] -match '^(0x)?([0-9a-fA-F]+)$') {
            $item.pid = if ($matches[1]) { [Convert]::ToInt32($matches[2], 16) } else { [int]$values[8] }
        }
    } else {
        return $null
    }
    if (-not [string]::Equals($item.app, $AppName, [StringComparison]::OrdinalIgnoreCase)) { return $null }
    return $item
}

# Launches of the app from its log lines: "=== TimecodeSyncPlayer v<version> 起動" (Information).
# proresGpuFirst: a "FetchMetadata: ... V:proresd3d11dec" line (Information) within
# ProResWindowSeconds of the launch and before the next launch. Only launches between FromUtc
# and ToUtc (when given) are returned. [ { atUtc, version, proresGpuFirst } ] in time order.
function Get-TcsAppLaunches {
    param([string[]]$AppLines = @(), $FromUtc = $null, $ToUtc = $null, [double]$ProResWindowSeconds = 2)
    $launches = New-Object System.Collections.Generic.List[object]
    $fetches = New-Object System.Collections.Generic.List[DateTime]
    foreach ($line in $AppLines) {
        if ($line -match '=== TimecodeSyncPlayer v(\S+) 起動') {
            $version = $matches[1]
            $at = Get-TcsLogLineUtc $line
            if ($null -ne $at) { $launches.Add([pscustomobject]@{ atUtc = $at; version = $version; proresGpuFirst = $false }) }
        } elseif ($line -match 'FetchMetadata: .*\bV:proresd3d11dec\b') {
            $at = Get-TcsLogLineUtc $line
            if ($null -ne $at) { $fetches.Add($at) }
        }
    }
    $sorted = @($launches | Sort-Object atUtc)
    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $start = $sorted[$i].atUtc
        $limit = $start.AddSeconds($ProResWindowSeconds)
        if ($i + 1 -lt $sorted.Count -and $sorted[$i + 1].atUtc -lt $limit) { $limit = $sorted[$i + 1].atUtc }
        foreach ($f in $fetches) {
            if ($f -ge $start -and $f -le $limit) { $sorted[$i].proresGpuFirst = $true; break }
        }
    }
    @($sorted | Where-Object {
        ($null -eq $FromUtc -or $_.atUtc -ge $FromUtc) -and ($null -eq $ToUtc -or $_.atUtc -le $ToUtc)
    })
}

# Tests of a trx with their times: [ { name (last segment), startUtc, endUtc } ].
function Get-TcsTrxTestTimes([string]$TrxPath) {
    if (-not $TrxPath -or -not (Test-Path -LiteralPath $TrxPath)) { return @() }
    [xml]$doc = Get-Content -LiteralPath $TrxPath
    @(foreach ($r in @($doc.SelectNodes("//*[local-name()='UnitTestResult']"))) {
        $start = $r.GetAttribute('startTime'); $end = $r.GetAttribute('endTime')
        if (-not $start -or -not $end) { continue }
        [pscustomobject]@{
            name = ($r.GetAttribute('testName') -split '\.')[-1]
            startUtc = ConvertTo-TcsUtc $start
            endUtc = ConvertTo-TcsUtc $end
        }
    })
}

# End of the test run (trx Times/@finish) as UTC, or $null.
function Get-TcsRunFinishUtc([string]$TrxPath) {
    if (-not $TrxPath -or -not (Test-Path -LiteralPath $TrxPath)) { return $null }
    [xml]$doc = Get-Content -LiteralPath $TrxPath
    $times = $doc.SelectSingleNode("//*[local-name()='Times']")
    if (-not $times -or -not $times.GetAttribute('finish')) { return $null }
    return ConvertTo-TcsUtc $times.GetAttribute('finish')
}

function Format-TcsLocalTime($Utc) {
    if ($null -eq $Utc) { return $null }
    return ([DateTimeOffset]([DateTime]::SpecifyKind($Utc, [DateTimeKind]::Utc))).ToLocalTime().ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
}

# The crash summary of one run.
#   Records    - crash-events.json "events" (all applications, maybe wider than the window)
#   FromUtc/ToUtc - the run's window; records and launches outside it are not counted
#   AppLines   - the app log lines of the run (whole daily files are fine: launches are windowed)
#   Tests      - Get-TcsTrxTestTimes (the scenario a crash happened in)
#   DumpFiles  - the files of ReportDir\dumps (FileInfo or { Name, LastWriteTimeUtc })
# One crash usually leaves a 1026 and a 1000 a moment apart; a 1000 within PairSeconds of a 1026
# is the same crash. A 1000 with no 1026 (a crash the runtime did not see) is a crash of its own.
# Each crash goes to the last launch at or before it (+1 s for the log write order) and to the
# test whose trx start..end holds it. Its dump is tsp-<pid>.dmp (the 1000 gives the pid), else
# the first dump written within DumpSeconds after it.
# Returns @{ crashes = [ordered]...; launches = N; proresGpuFirstLaunches = N }.
function Get-TcsCrashSummary {
    param(
        [object[]]$Records = @(),
        $FromUtc = $null,
        $ToUtc = $null,
        [string[]]$AppLines = @(),
        [object[]]$Tests = @(),
        [object[]]$DumpFiles = @(),
        [string]$DumpDir = '',
        [bool]$Collected = $true,
        [string]$CollectError = '',
        [double]$PairSeconds = 10,
        [double]$DumpSeconds = 120,
        [string]$AppName = 'TimecodeSyncPlayer.exe'
    )
    $from = ConvertTo-TcsUtc $FromUtc
    $to = ConvertTo-TcsUtc $ToUtc
    $events = @(foreach ($r in $Records) {
        $e = ConvertFrom-TcsCrashEventRecord $r $AppName
        if ($null -eq $e) { continue }
        if ($null -ne $from -and $e.atUtc -lt $from) { continue }
        if ($null -ne $to -and $e.atUtc -gt $to) { continue }
        $e
    }) | Sort-Object { $_.atUtc }
    $events = @($events)
    $runtime = @($events | Where-Object { $_.source -eq '1026' })
    $werList = @($events | Where-Object { $_.source -eq '1000' })

    $crashes = New-Object System.Collections.Generic.List[object]
    foreach ($e in $runtime) {
        $crashes.Add([ordered]@{ atUtc = $e.atUtc; sources = @('1026'); code = $e.code; address = $e.address;
            exception = $e.exception; module = $null; offset = $null; pid = $null })
    }
    foreach ($w in $werList) {
        $best = $null; $bestGap = [double]::MaxValue
        foreach ($c in $crashes) {
            if ($c.sources -contains '1000') { continue }
            $gap = [math]::Abs(($w.atUtc - $c.atUtc).TotalSeconds)
            if ($gap -le $PairSeconds -and $gap -lt $bestGap) { $best = $c; $bestGap = $gap }
        }
        if ($null -eq $best) {
            $crashes.Add([ordered]@{ atUtc = $w.atUtc; sources = @('1000'); code = $w.code; address = $null;
                exception = $null; module = $w.module; offset = $w.offset; pid = $w.pid })
        } else {
            $best.sources = @($best.sources) + '1000'
            $best.module = $w.module; $best.offset = $w.offset; $best.pid = $w.pid
            if (-not $best.code) { $best.code = $w.code }
        }
    }

    $allLaunches = @(Get-TcsAppLaunches -AppLines $AppLines)
    $windowLaunches = @($allLaunches | Where-Object {
        ($null -eq $from -or $_.atUtc -ge $from) -and ($null -eq $to -or $_.atUtc -le $to)
    })
    $dumps = @($DumpFiles | Where-Object { $_.Name -like '*.dmp' } | Sort-Object { $_.LastWriteTimeUtc })
    $usedDumps = @{}
    $items = @(foreach ($c in ($crashes | Sort-Object { $_.atUtc })) {
        $launch = @($allLaunches | Where-Object { $_.atUtc -le $c.atUtc.AddSeconds(1) }) | Select-Object -Last 1
        $test = @($Tests | Where-Object { $_.startUtc -le $c.atUtc -and $c.atUtc -le $_.endUtc }) | Select-Object -First 1
        $dump = $null
        if ($null -ne $c.pid) {
            $byPid = @($dumps | Where-Object { $_.Name -ieq ('tsp-' + $c.pid + '.dmp') }) | Select-Object -First 1
            if ($byPid) { $dump = $byPid.Name }
        }
        if (-not $dump) {
            $byTime = @($dumps | Where-Object {
                -not $usedDumps.ContainsKey($_.Name) -and
                $_.LastWriteTimeUtc -ge $c.atUtc.AddSeconds(-5) -and $_.LastWriteTimeUtc -le $c.atUtc.AddSeconds($DumpSeconds)
            }) | Select-Object -First 1
            if ($byTime) { $dump = $byTime.Name }
        }
        if ($dump) { $usedDumps[$dump] = $true }
        [ordered]@{
            time = Format-TcsLocalTime $c.atUtc
            sources = @($c.sources)
            code = $c.code
            address = $c.address
            module = $c.module
            offset = $c.offset
            exception = $c.exception
            pid = $c.pid
            scenario = $(if ($test) { $test.name } else { $null })
            launchTime = $(if ($launch) { Format-TcsLocalTime $launch.atUtc } else { $null })
            launchVersion = $(if ($launch) { $launch.version } else { $null })
            secondsAfterLaunch = $(if ($launch) { [math]::Round(($c.atUtc - $launch.atUtc).TotalSeconds, 3) } else { $null })
            proresGpuFirstLaunch = $(if ($launch) { [bool]$launch.proresGpuFirst } else { $null })
            dump = $dump
        }
    })
    $crashSummary = [ordered]@{
        count = $(if ($Collected) { $items.Count } else { $null })
        collected = $Collected
        error = $(if ($CollectError) { $CollectError } else { $null })
        windowFrom = Format-TcsLocalTime $from
        windowTo = Format-TcsLocalTime $to
        dotnetRuntime1026 = $runtime.Count
        applicationError1000 = $werList.Count
        items = @($items)
        dumps = [ordered]@{ dir = $(if ($DumpDir) { $DumpDir } else { $null }); count = $dumps.Count; names = @($dumps | ForEach-Object { $_.Name }) }
    }
    @{
        crashes = $crashSummary
        launches = $windowLaunches.Count
        proresGpuFirstLaunches = @($windowLaunches | Where-Object { $_.proresGpuFirst }).Count
    }
}

Export-ModuleMember -Function Get-TcsField, ConvertTo-TcsUtc, Get-TcsLogLineUtc, Get-TcsRunStartUtc, Get-TcsProResLoadSummary,
    Get-TcsMedian, Get-TcsExitLogLines, Get-TcsAppExitSummary, Get-TcsCommitFreeGbAtStart, Get-TcsSystemCommitFreeGb,
    Get-TcsRunnerPreflightValue, Get-TcsDriveFreeGb, ConvertTo-TcsCrashEventRecord, Read-TcsCrashEventLog,
    ConvertFrom-TcsCrashEventRecord, Get-TcsAppLaunches, Get-TcsTrxTestTimes, Get-TcsRunFinishUtc, Get-TcsCrashSummary
