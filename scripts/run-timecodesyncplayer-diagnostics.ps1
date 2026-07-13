# TimecodeSyncPlayer diagnostics runner.
# Runs build/tests, analyzes the latest TimecodeSyncPlayer log, and writes a Markdown report.

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$ReportDirectory,

    [string]$LogPath,

    [switch]$SkipBuild,
    [switch]$SkipNonE2E,
    [switch]$SkipE2E,
    [switch]$TreatWarningsAsFailure,

    [int]$BurstWindowSeconds = 2,
    [int]$LoadFileBurstThreshold = 2,
    [int]$SyncSeekBurstThreshold = 3,
    [int]$PerfContextWindowSeconds = 3,
    [int]$PlaybackPerfWarningThreshold = 50,
    [int]$SampleLimit = 20
)

$ErrorActionPreference = "Stop"
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$repoRootPath = $repoRoot.Path
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repoRootPath "artifacts\diagnostics"
}
$projectPath = Join-Path $repoRootPath "src\TimecodeSyncPlayer\TimecodeSyncPlayer.csproj"
$testProjectPath = Join-Path $repoRootPath "tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj"
$logDirectories = @(
    (Join-Path $repoRootPath "tests\TimecodeSyncPlayer.Tests\bin\$Configuration\net8.0-windows\logs"),
    (Join-Path $repoRootPath "src\TimecodeSyncPlayer\bin\$Configuration\net8.0-windows\logs")
)
$startedAt = Get-Date

function New-CommandResult {
    param(
        [string]$Name,
        [bool]$Skipped,
        [int]$ExitCode,
        [string[]]$Output,
        [datetime]$Started,
        [datetime]$Ended
    )

    [pscustomobject]@{
        Name = $Name
        Skipped = $Skipped
        ExitCode = $ExitCode
        Output = $Output
        Started = $Started
        Ended = $Ended
        DurationSeconds = [math]::Round(($Ended - $Started).TotalSeconds, 2)
    }
}

function Invoke-DiagnosticCommand {
    param(
        [string]$Name,
        [string]$FileName,
        [string[]]$Arguments
    )

    Write-Host "==> $Name"
    $commandStarted = Get-Date
    $output = & $FileName @Arguments 2>&1 | ForEach-Object { $_.ToString() }
    $exitCode = if ($null -eq $LASTEXITCODE) { 0 } else { $LASTEXITCODE }
    $commandEnded = Get-Date

    foreach ($line in $output) {
        Write-Host $line
    }

    New-CommandResult `
        -Name $Name `
        -Skipped $false `
        -ExitCode $exitCode `
        -Output $output `
        -Started $commandStarted `
        -Ended $commandEnded
}

function New-SkippedCommandResult {
    param([string]$Name)

    $now = Get-Date
    New-CommandResult -Name $Name -Skipped $true -ExitCode 0 -Output @("Skipped by parameter.") -Started $now -Ended $now
}

function Get-LatestLogPath {
    param(
        [string]$ExplicitLogPath,
        [string[]]$DefaultLogDirectories
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitLogPath)) {
        if (-not (Test-Path -LiteralPath $ExplicitLogPath)) {
            throw "Log file was not found: $ExplicitLogPath"
        }
        return (Resolve-Path -LiteralPath $ExplicitLogPath).Path
    }

    $latest = $DefaultLogDirectories |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter "timecodesyncplayer-*.log" } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $latest) {
        return $null
    }

    return $latest.FullName
}

function Get-LogTimestamp {
    param([string]$Line)

    if ($Line -match "^(?<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})") {
        return [datetime]::ParseExact(
            $Matches["ts"],
            "yyyy-MM-dd HH:mm:ss.fff",
            [System.Globalization.CultureInfo]::InvariantCulture)
    }

    return $null
}

function Select-LogLinesForRun {
    param(
        [string[]]$Lines,
        [datetime]$Started,
        [bool]$FilterToRun
    )

    if (-not $FilterToRun) {
        return $Lines
    }

    $threshold = $Started.AddSeconds(-2)
    $selected = @()
    $includeCurrentRecord = $false

    foreach ($line in $Lines) {
        $timestamp = Get-LogTimestamp -Line $line
        if ($null -ne $timestamp) {
            $includeCurrentRecord = $timestamp -ge $threshold
        }

        if ($includeCurrentRecord) {
            $selected += $line
        }
    }

    return $selected
}

function Get-PatternSummary {
    param(
        [string[]]$Lines,
        [string]$Name,
        [string]$Pattern,
        [int]$Limit
    )

    $matches = @($Lines | Select-String -Pattern $Pattern)
    [pscustomobject]@{
        Name = $Name
        Count = $matches.Count
        Samples = @($matches | Select-Object -First $Limit | ForEach-Object { $_.Line })
    }
}

function Get-PreviousReportPath {
    param([string]$Directory)

    if (-not (Test-Path -LiteralPath $Directory)) {
        return $null
    }

    $previous = Get-ChildItem -LiteralPath $Directory -Filter "timecodesyncplayer-diagnostics-*.md" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($null -eq $previous) {
        return $null
    }

    return $previous.FullName
}

function Read-LogSummaryCounts {
    param([string]$Path)

    $counts = @{}
    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $counts
    }

    $inSection = $false
    foreach ($line in (Get-Content -Encoding UTF8 -LiteralPath $Path)) {
        if ($line -match "^## Log Summary\s*$") {
            $inSection = $true
            continue
        }

        if ($inSection -and $line -match "^##\s+") {
            break
        }

        if (-not $inSection) {
            continue
        }

        if ($line -match "^\|\s*(?<pattern>[^|]+?)\s*\|\s*(?<count>-?\d+)\s*\|") {
            $pattern = $Matches["pattern"].Trim()
            if ($pattern -eq "Pattern") {
                continue
            }

            $counts[$pattern] = [int]$Matches["count"]
        }
    }

    return $counts
}

function Get-LogSummaryRows {
    param(
        [object[]]$PatternSummaries,
        [object[]]$BurstSummaries
    )

    $rows = @()
    foreach ($summary in $PatternSummaries) {
        $rows += [pscustomobject]@{
            Pattern = $summary.Name
            Count = $summary.Count
        }
    }

    foreach ($summary in $BurstSummaries) {
        $rows += [pscustomobject]@{
            Pattern = "$($summary.Name) events"
            Count = $summary.EventCount
        }
        $rows += [pscustomobject]@{
            Pattern = "$($summary.Name) bursts"
            Count = $summary.BurstCount
        }
    }

    return $rows
}

function Get-LogSummaryDeltas {
    param(
        [object[]]$CurrentRows,
        [hashtable]$PreviousCounts
    )

    $rows = @()
    $seen = @{}
    foreach ($row in $CurrentRows) {
        $previous = if ($PreviousCounts.ContainsKey($row.Pattern)) { $PreviousCounts[$row.Pattern] } else { 0 }
        $current = [int]$row.Count
        $rows += [pscustomobject]@{
            Pattern = $row.Pattern
            Previous = $previous
            Current = $current
            Delta = $current - $previous
        }
        $seen[$row.Pattern] = $true
    }

    foreach ($pattern in $PreviousCounts.Keys) {
        if ($seen.ContainsKey($pattern)) {
            continue
        }

        $previous = [int]$PreviousCounts[$pattern]
        $rows += [pscustomobject]@{
            Pattern = $pattern
            Previous = $previous
            Current = 0
            Delta = -1 * $previous
        }
    }

    return $rows
}

function Get-PlaybackPerfWarningClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $warnings = @($Lines | Select-String -Pattern "Playback perf warning" | ForEach-Object { $_.Line })
    $displayedFps = @($warnings | Where-Object { $_ -match "displayed FPS is below source FPS" })
    $slowerClock = @($warnings | Where-Object { $_ -match "playback clock is slower than realtime" })
    $other = @($warnings | Where-Object {
        $_ -notmatch "displayed FPS is below source FPS" -and
        $_ -notmatch "playback clock is slower than realtime"
    })

    return @(
        [pscustomobject]@{
            Name = "Displayed FPS below source FPS"
            Count = $displayedFps.Count
            Samples = @($displayedFps | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Playback clock slower than realtime"
            Count = $slowerClock.Count
            Samples = @($slowerClock | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Other playback perf warning"
            Count = $other.Count
            Samples = @($other | Select-Object -First $Limit)
        }
    )
}

function Get-PlaybackPerfWarningOriginClassification {
    param(
        [string[]]$Lines,
        [string]$AnalyzedLogPath,
        [int]$Limit
    )

    $warnings = @($Lines | Select-String -Pattern "Playback perf warning" | ForEach-Object { $_.Line })
    $normalizedPath = $AnalyzedLogPath.Replace("/", "\")
    $isAutomatedE2ELog = $normalizedPath -match "\\tests\\TimecodeSyncPlayer\.Tests\\bin\\"

    $e2eLikely = @()
    $actionable = @()

    foreach ($warning in $warnings) {
        if ($isAutomatedE2ELog) {
            $e2eLikely += $warning
        } else {
            $actionable += $warning
        }
    }

    return @(
        [pscustomobject]@{
            Name = "Automated E2E / vo-null likely"
            Count = $e2eLikely.Count
            Samples = @($e2eLikely | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "App or hardware log"
            Count = $actionable.Count
            Samples = @($actionable | Select-Object -First $Limit)
        }
    )
}

function Get-PlaybackPerfWarningContextClassification {
    param(
        [string[]]$Lines,
        [int]$WindowSeconds,
        [int]$Limit
    )

    $trackSwitchEvents = @()
    $gapExitEvents = @()
    $warnings = @()

    foreach ($line in $Lines) {
        $timestamp = Get-LogTimestamp -Line $line
        if ($null -eq $timestamp) {
            continue
        }

        if ($line -match "Playlist track loaded|Continue mode: switching to track") {
            $trackSwitchEvents += $timestamp
        }

        if ($line -match "Continue mode: exiting gap") {
            $gapExitEvents += $timestamp
        }

        if ($line -match "Playback perf warning") {
            $warnings += [pscustomobject]@{
                Timestamp = $timestamp
                Line = $line
            }
        }
    }

    $trackSwitch = @()
    $gapExit = @()
    $normalPlayback = @()

    foreach ($warning in $warnings) {
        $nearTrackSwitch = @($trackSwitchEvents | Where-Object {
            $_ -le $warning.Timestamp -and
            ($warning.Timestamp - $_).TotalSeconds -le $WindowSeconds
        }).Count -gt 0

        $nearGapExit = @($gapExitEvents | Where-Object {
            $_ -le $warning.Timestamp -and
            ($warning.Timestamp - $_).TotalSeconds -le $WindowSeconds
        }).Count -gt 0

        if ($nearTrackSwitch) {
            $trackSwitch += $warning.Line
        } elseif ($nearGapExit) {
            $gapExit += $warning.Line
        } else {
            $normalPlayback += $warning.Line
        }
    }

    return @(
        [pscustomobject]@{
            Name = "Track switch aftermath"
            Count = $trackSwitch.Count
            Samples = @($trackSwitch | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Gap exit aftermath"
            Count = $gapExit.Count
            Samples = @($gapExit | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Normal playback"
            Count = $normalPlayback.Count
            Samples = @($normalPlayback | Select-Object -First $Limit)
        }
    )
}

function Get-PlaybackPerfTimingClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $perfLines = @($Lines | Select-String -Pattern "Playback perf elapsed=" | ForEach-Object { $_.Line })
    $spoutEnabled = @($perfLines | Where-Object { $_ -match "spoutEnabled=True" })
    $largeFrames = @($perfLines | Where-Object {
        $_ -match "size=(?<width>\d+)x(?<height>\d+)" -and
        ([int]$Matches["width"] * [int]$Matches["height"]) -ge (1920 * 1080)
    })
    $renderHeavy = @($perfLines | Where-Object {
        $_ -match "avgRenderMs=(?<value>\d+(?:\.\d+)?)" -and [double]$Matches["value"] -ge 8.0
    })
    $bitmapHeavy = @($perfLines | Where-Object {
        $_ -match "avgBitmapMs=(?<value>\d+(?:\.\d+)?)" -and [double]$Matches["value"] -ge 4.0
    })
    $spoutHeavy = @($perfLines | Where-Object {
        $_ -match "avgSpoutMs=(?<value>\d+(?:\.\d+)?)" -and [double]$Matches["value"] -ge 2.0
    })

    return @(
        [pscustomobject]@{
            Name = "Playback perf samples"
            Count = $perfLines.Count
            Samples = @($perfLines | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout enabled"
            Count = $spoutEnabled.Count
            Samples = @($spoutEnabled | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Large frame size"
            Count = $largeFrames.Count
            Samples = @($largeFrames | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Render-heavy avgRenderMs>=8"
            Count = $renderHeavy.Count
            Samples = @($renderHeavy | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Bitmap-heavy avgBitmapMs>=4"
            Count = $bitmapHeavy.Count
            Samples = @($bitmapHeavy | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout-heavy avgSpoutMs>=2"
            Count = $spoutHeavy.Count
            Samples = @($spoutHeavy | Select-Object -First $Limit)
        }
    )
}

function Get-LoadFileClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $groups = @{}
    foreach ($line in $Lines) {
        if ($line -notmatch "LoadFile path=(?<path>.+?) start=(?<start>\S+)") {
            continue
        }

        $path = $Matches["path"].Trim()
        $start = $Matches["start"].Trim()
        $fileName = [System.IO.Path]::GetFileName($path)
        $key = "$fileName start=$start"

        if (-not $groups.ContainsKey($key)) {
            $groups[$key] = [pscustomobject]@{
                Key = $key
                Count = 0
                Samples = @()
            }
        }

        $groups[$key].Count++
        if ($groups[$key].Samples.Count -lt $Limit) {
            $groups[$key].Samples += $line
        }
    }

    return @($groups.Values | Sort-Object -Property Count, Key -Descending | Select-Object -First $Limit)
}

function Get-SpoutOutputClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $spoutLines = @($Lines | Select-String -Pattern "SpoutOutput:" | ForEach-Object { $_.Line })
    $initialized = @($spoutLines | Where-Object { $_ -match "sender='" -and $_ -notmatch "pitch=" })
    $dllMissing = @($spoutLines | Where-Object { $_ -match "SpoutDX\.dll" })
    $initFailure = @($spoutLines | Where-Object {
        $_ -match "spoutDX|OpenDirectX11|SetSenderName" -and
        $_ -notmatch "sender='" -and
        $_ -notmatch "pitch="
    })
    $sendStarted = @($spoutLines | Where-Object { $_ -match "pitch=.*sender='" })
    $sendFalse = @($spoutLines | Where-Object { $_ -match "SendImage" -and $_ -match "false" })
    $sendException = @($spoutLines | Where-Object { $_ -match "SendFrame" -and $_ -notmatch "SendImage" })

    return @(
        [pscustomobject]@{
            Name = "Spout initialized"
            Count = $initialized.Count
            Samples = @($initialized | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "SpoutDX missing"
            Count = $dllMissing.Count
            Samples = @($dllMissing | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout init failure"
            Count = $initFailure.Count
            Samples = @($initFailure | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout send started"
            Count = $sendStarted.Count
            Samples = @($sendStarted | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout SendImage false"
            Count = $sendFalse.Count
            Samples = @($sendFalse | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Spout send exception"
            Count = $sendException.Count
            Samples = @($sendException | Select-Object -First $Limit)
        }
    )
}

function Get-LtcInputClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $audioStats = @($Lines | Select-String -Pattern "LTC audio stats" | ForEach-Object { $_.Line })
    $decodedZero = @()
    $decodedPositive = @()
    foreach ($line in $audioStats) {
        if ($line -match "decodedFrames=(?<frames>\d+)") {
            if ([int]$Matches["frames"] -eq 0) {
                $decodedZero += $line
            } else {
                $decodedPositive += $line
            }
        }
    }

    $diagnosticLines = @($Lines | Select-String -Pattern "^\d{4}-\d{2}-\d{2} .* \[[A-Z]{3}\] LTC frame diagnostic status=" | ForEach-Object { $_.Line })
    $invalid = @($diagnosticLines | Where-Object { $_ -match "status=Invalid" })
    $reverse = @($diagnosticLines | Where-Object { $_ -match "status=Reverse" })
    $jump = @($diagnosticLines | Where-Object { $_ -match "status=Jump" })
    $duplicate = @($diagnosticLines | Where-Object { $_ -match "status=Duplicate" })
    $skipped = @($Lines | Select-String -Pattern "Timecode sync skipped" | ForEach-Object { $_.Line })

    return @(
        [pscustomobject]@{
            Name = "LTC audio stats"
            Count = $audioStats.Count
            Samples = @($audioStats | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC decodedFrames=0"
            Count = $decodedZero.Count
            Samples = @($decodedZero | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC decodedFrames>0"
            Count = $decodedPositive.Count
            Samples = @($decodedPositive | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC diagnostic Invalid"
            Count = $invalid.Count
            Samples = @($invalid | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC diagnostic Reverse"
            Count = $reverse.Count
            Samples = @($reverse | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC diagnostic Jump"
            Count = $jump.Count
            Samples = @($jump | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "LTC diagnostic Duplicate"
            Count = $duplicate.Count
            Samples = @($duplicate | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Timecode sync skipped"
            Count = $skipped.Count
            Samples = @($skipped | Select-Object -First $Limit)
        }
    )
}

function Get-ContinueSyncClassification {
    param(
        [string[]]$Lines,
        [int]$Limit
    )

    $syncSeek = @($Lines | Select-String -Pattern "Continue mode: sync seek" | ForEach-Object { $_.Line })
    $syncSeekSuccess = @($syncSeek | Where-Object { $_ -match "success=True" })
    $syncSeekFailure = @($syncSeek | Where-Object { $_ -match "success=False" })
    $pending = @($Lines | Select-String -Pattern "Timecode sync pending" | ForEach-Object { $_.Line })
    $pendingTimedOut = @($pending | Where-Object { $_ -match "Timecode sync pending TimedOut" })
    $pendingSettled = @($pending | Where-Object { $_ -match "Timecode sync pending Settled" })
    $loadStabilityWait = @($Lines | Select-String -Pattern "Continue mode: waiting for file load stability" | ForEach-Object { $_.Line })

    return @(
        [pscustomobject]@{
            Name = "Continue sync seek"
            Count = $syncSeek.Count
            Samples = @($syncSeek | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Continue sync seek success"
            Count = $syncSeekSuccess.Count
            Samples = @($syncSeekSuccess | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Continue sync seek failure"
            Count = $syncSeekFailure.Count
            Samples = @($syncSeekFailure | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Timecode sync pending"
            Count = $pending.Count
            Samples = @($pending | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Timecode sync pending TimedOut"
            Count = $pendingTimedOut.Count
            Samples = @($pendingTimedOut | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Timecode sync pending Settled"
            Count = $pendingSettled.Count
            Samples = @($pendingSettled | Select-Object -First $Limit)
        }
        [pscustomobject]@{
            Name = "Continue load stability wait"
            Count = $loadStabilityWait.Count
            Samples = @($loadStabilityWait | Select-Object -First $Limit)
        }
    )
}

function Get-BurstSummary {
    param(
        [string[]]$Lines,
        [string]$Name,
        [string]$Pattern,
        [int]$WindowSeconds,
        [int]$Threshold,
        [int]$Limit
    )

    $events = @()
    foreach ($line in $Lines) {
        if ($line -notmatch $Pattern) {
            continue
        }

        $timestamp = Get-LogTimestamp -Line $line
        if ($null -ne $timestamp) {
            $events += [pscustomobject]@{ Timestamp = $timestamp; Line = $line }
        }
    }

    $events = @($events | Sort-Object Timestamp)
    $bursts = @()
    $i = 0
    while ($i -lt $events.Count) {
        $start = $events[$i].Timestamp
        $end = $start.AddSeconds($WindowSeconds)
        $window = @()
        $j = $i
        while ($j -lt $events.Count -and $events[$j].Timestamp -le $end) {
            $window += $events[$j]
            $j++
        }

        if ($window.Count -gt $Threshold) {
            $bursts += [pscustomobject]@{
                Start = $start
                End = $end
                Count = $window.Count
                FirstLine = $window[0].Line
                LastLine = $window[$window.Count - 1].Line
            }
            $i = $j
        } else {
            $i++
        }
    }

    [pscustomobject]@{
        Name = $Name
        EventCount = $events.Count
        BurstCount = $bursts.Count
        Threshold = $Threshold
        WindowSeconds = $WindowSeconds
        Samples = @($bursts | Select-Object -First $Limit)
    }
}

function Format-SampleLines {
    param([string[]]$Lines)

    if ($Lines.Count -eq 0) {
        return @("- none")
    }

    $bt = [string][char]96
    $codeFence = $bt + $bt
    return @($Lines | ForEach-Object {
        $safe = $_.Replace($bt, $codeFence)
        "- " + $codeFence + $safe + $codeFence
    })
}

function Format-BurstSamples {
    param($Bursts)

    $burstList = @($Bursts)
    if ($burstList.Count -eq 0) {
        return @("- none")
    }

    $bt = [string][char]96
    $codeFence = $bt + $bt
    return @($burstList | ForEach-Object {
        $safe = $_.FirstLine.Replace($bt, $codeFence)
        $prefix = "- {0}-{1} count={2}: " -f $_.Start.ToString("HH:mm:ss.fff"), $_.End.ToString("HH:mm:ss.fff"), $_.Count
        $prefix + $codeFence + $safe + $codeFence
    })
}

function Write-Report {
    param(
        [string]$Path,
        [object[]]$Commands,
        [string]$AnalyzedLogPath,
        [object[]]$LogSummaryRows,
        [string]$PreviousReportPath,
        [object[]]$LogSummaryDeltas,
        [object[]]$PatternSummaries,
        [object[]]$BurstSummaries,
        [object[]]$PlaybackPerfWarningClassifications,
        [object[]]$PlaybackPerfWarningOriginClassifications,
        [object[]]$PlaybackPerfWarningContextClassifications,
        [object[]]$PlaybackPerfTimingClassifications,
        [object[]]$LoadFileClassifications,
        [object[]]$SpoutOutputClassifications,
        [object[]]$LtcInputClassifications,
        [object[]]$ContinueSyncClassifications,
        [string]$Verdict,
        [string[]]$Reasons,
        [datetime]$Started,
        [datetime]$Ended
    )

    $lines = [System.Collections.Generic.List[string]]::new()
    $lines.Add("# TimecodeSyncPlayer Diagnostics Report")
    $lines.Add("")
    $lines.Add("- Started: $($Started.ToString("yyyy-MM-dd HH:mm:ss zzz"))")
    $lines.Add("- Ended: $($Ended.ToString("yyyy-MM-dd HH:mm:ss zzz"))")
    $lines.Add("- Verdict: **$Verdict**")
    $lines.Add("- Analyzed log: ``$AnalyzedLogPath``")
    $lines.Add("")
    $lines.Add("## Command Results")
    $lines.Add("")
    $lines.Add("| Command | Result | Duration |")
    $lines.Add("| --- | --- | ---: |")

    foreach ($command in $Commands) {
        $result = if ($command.Skipped) { "SKIPPED" } elseif ($command.ExitCode -eq 0) { "PASS" } else { "FAIL ($($command.ExitCode))" }
        $lines.Add("| $($command.Name) | $result | $($command.DurationSeconds)s |")
    }

    $lines.Add("")
    $lines.Add("## Log Summary")
    $lines.Add("")
    $lines.Add("| Pattern | Count |")
    $lines.Add("| --- | ---: |")

    foreach ($row in $LogSummaryRows) {
        $lines.Add("| $($row.Pattern) | $($row.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Log Summary Delta")
    $lines.Add("")
    if ([string]::IsNullOrWhiteSpace($PreviousReportPath)) {
        $lines.Add("- Previous report: none")
    } else {
        $lines.Add("- Previous report: ``$PreviousReportPath``")
        $lines.Add("")
        $lines.Add("| Pattern | Previous | Current | Delta |")
        $lines.Add("| --- | ---: | ---: | ---: |")
        foreach ($delta in $LogSummaryDeltas) {
            $deltaText = if ($delta.Delta -gt 0) { "+$($delta.Delta)" } else { "$($delta.Delta)" }
            $lines.Add("| $($delta.Pattern) | $($delta.Previous) | $($delta.Current) | $deltaText |")
        }
    }

    $lines.Add("")
    $lines.Add("## Playback Perf Warning Classification")
    $lines.Add("")
    $lines.Add("| Category | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $PlaybackPerfWarningClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Playback Perf Warning Origin")
    $lines.Add("")
    $lines.Add("| Origin | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $PlaybackPerfWarningOriginClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Playback Perf Warning Context")
    $lines.Add("")
    $lines.Add("| Context | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $PlaybackPerfWarningContextClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Playback Perf Timing")
    $lines.Add("")
    $lines.Add("| Category | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $PlaybackPerfTimingClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Spout Output Health")
    $lines.Add("")
    $lines.Add("| Category | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $SpoutOutputClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## LTC Input Health")
    $lines.Add("")
    $lines.Add("| Category | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $LtcInputClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## Continue Sync Health")
    $lines.Add("")
    $lines.Add("| Category | Count |")
    $lines.Add("| --- | ---: |")
    foreach ($classification in $ContinueSyncClassifications) {
        $lines.Add("| $($classification.Name) | $($classification.Count) |")
    }

    $lines.Add("")
    $lines.Add("## LoadFile Classification")
    $lines.Add("")
    if ($LoadFileClassifications.Count -eq 0) {
        $lines.Add("- none")
    } else {
        $lines.Add("| Path/Start | Count |")
        $lines.Add("| --- | ---: |")
        foreach ($classification in $LoadFileClassifications) {
            $lines.Add("| $($classification.Key) | $($classification.Count) |")
        }
    }

    $lines.Add("")
    $lines.Add("## Reasons")
    $lines.Add("")
    if ($Reasons.Count -eq 0) {
        $lines.Add("- No test failure or critical log pattern was detected.")
    } else {
        foreach ($reason in $Reasons) {
            $lines.Add("- $reason")
        }
    }

    foreach ($summary in $PatternSummaries) {
        $lines.Add("")
        $lines.Add("## Samples: $($summary.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $summary.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($summary in $BurstSummaries) {
        $lines.Add("")
        $lines.Add("## Burst Samples: $($summary.Name)")
        $lines.Add("")
        foreach ($line in (Format-BurstSamples -Bursts $summary.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $PlaybackPerfWarningClassifications) {
        $lines.Add("")
        $lines.Add("## Playback Perf Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $PlaybackPerfWarningOriginClassifications) {
        $lines.Add("")
        $lines.Add("## Playback Perf Origin Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $PlaybackPerfWarningContextClassifications) {
        $lines.Add("")
        $lines.Add("## Playback Perf Context Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $PlaybackPerfTimingClassifications) {
        $lines.Add("")
        $lines.Add("## Playback Perf Timing Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $LoadFileClassifications) {
        $lines.Add("")
        $lines.Add("## LoadFile Samples: $($classification.Key)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $SpoutOutputClassifications) {
        $lines.Add("")
        $lines.Add("## Spout Output Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $LtcInputClassifications) {
        $lines.Add("")
        $lines.Add("## LTC Input Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    foreach ($classification in $ContinueSyncClassifications) {
        $lines.Add("")
        $lines.Add("## Continue Sync Samples: $($classification.Name)")
        $lines.Add("")
        foreach ($line in (Format-SampleLines -Lines $classification.Samples)) {
            $lines.Add($line)
        }
    }

    $lines.Add("")
    $lines.Add("## Suggested Follow-Up")
    $lines.Add("")
    $lines.Add("- If ``LoadFile bursts`` is greater than 0, inspect track-switch load command paths.")
    $lines.Add("- If ``Sync seek bursts`` is greater than 0, inspect load-stability wait and pending seek settlement.")
    $lines.Add("- If ``Continue sync seek bursts`` is greater than 0, inspect Continue mode load stability and LTC jump filtering.")
    $lines.Add("- Use ``LoadFile Classification`` to distinguish repeated same path/start loads from intentional track changes.")
    $lines.Add("- Use ``Spout Output Health`` to distinguish missing optional SpoutDX from sender initialization/send failures.")
    $lines.Add("- Use ``LTC Input Health`` to separate no-input/low-decode periods from diagnostic frame filtering.")
    $lines.Add("- Use ``Continue Sync Health`` to confirm Continue sync seeks settle without repeated failure or timeout.")
    $lines.Add("- Use ``Playback Perf Timing`` to separate render, bitmap update, Spout, and resolution-related load.")
    $lines.Add("- If ``Gap freeze timeout`` or ``Frame-step not yet reflected`` increases, inspect Freeze capture path/time-pos validation.")
    $lines.Add("- If ``Playback perf warning`` exceeds the threshold, inspect render, bitmap update, and Spout timings.")

    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -Encoding UTF8 -LiteralPath $Path -Value $lines
}

$commands = @()

if ($SkipBuild) {
    $commands += New-SkippedCommandResult -Name "build"
} else {
    $commands += Invoke-DiagnosticCommand -Name "build" -FileName "dotnet" -Arguments @("build", $projectPath, "-c", $Configuration, "-v", "minimal")
}

if ($SkipNonE2E) {
    $commands += New-SkippedCommandResult -Name "non-e2e"
} else {
    $commands += Invoke-DiagnosticCommand -Name "non-e2e" -FileName "dotnet" -Arguments @("test", $testProjectPath, "-c", $Configuration, "-v", "minimal", "--filter", "Category!=E2E")
}

if ($SkipE2E) {
    $commands += New-SkippedCommandResult -Name "e2e"
} else {
    $commands += Invoke-DiagnosticCommand -Name "e2e" -FileName "dotnet" -Arguments @("test", $testProjectPath, "-c", $Configuration, "-v", "minimal", "--filter", "Category=E2E", "--no-build")
}

$resolvedLogPath = Get-LatestLogPath -ExplicitLogPath $LogPath -DefaultLogDirectories $logDirectories
$logLines = @()
if ($null -ne $resolvedLogPath) {
    $logLines = @(Get-Content -Encoding UTF8 -LiteralPath $resolvedLogPath)
}
$ranCommand = @($commands | Where-Object { -not $_.Skipped }).Count -gt 0
$logLines = @(Select-LogLinesForRun -Lines $logLines -Started $startedAt -FilterToRun $ranCommand)

$previousReportPath = Get-PreviousReportPath -Directory $ReportDirectory

$patternSummaries = @(
    Get-PatternSummary -Lines $logLines -Name "Critical errors" -Pattern "\[(ERR|FTL)\]|Exception|success=false" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Gap freeze timeout" -Pattern "gap freeze final-frame capture timed out" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Frame-step not yet reflected" -Pattern "frame-step not yet reflected" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Playback perf warning" -Pattern "Playback perf warning" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Load stability wait" -Pattern "Continue mode: waiting for file load stability" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "LTC audio stats" -Pattern "LTC audio stats" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "LTC frame diagnostic" -Pattern "^\d{4}-\d{2}-\d{2} .* \[[A-Z]{3}\] LTC frame diagnostic status=" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Timecode sync skipped" -Pattern "Timecode sync skipped" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Continue sync seek" -Pattern "Continue mode: sync seek" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Timecode sync pending" -Pattern "Timecode sync pending" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Spout output warning" -Pattern "SpoutOutput: .*?(SpoutDX\.dll|spoutDX|OpenDirectX11|SetSenderName|SendImage)" -Limit $SampleLimit
    Get-PatternSummary -Lines $logLines -Name "Spout output exception" -Pattern "SpoutOutput: .*?SendFrame" -Limit $SampleLimit
)

$burstSummaries = @(
    Get-BurstSummary -Lines $logLines -Name "LoadFile bursts" -Pattern "LoadFile|loadfile" -WindowSeconds $BurstWindowSeconds -Threshold $LoadFileBurstThreshold -Limit $SampleLimit
    Get-BurstSummary -Lines $logLines -Name "Sync seek bursts" -Pattern "Timecode sync seek" -WindowSeconds $BurstWindowSeconds -Threshold $SyncSeekBurstThreshold -Limit $SampleLimit
    Get-BurstSummary -Lines $logLines -Name "Continue sync seek bursts" -Pattern "Continue mode: sync seek" -WindowSeconds $BurstWindowSeconds -Threshold $SyncSeekBurstThreshold -Limit $SampleLimit
)

$logSummaryRows = @(Get-LogSummaryRows -PatternSummaries $patternSummaries -BurstSummaries $burstSummaries)
$previousLogSummaryCounts = Read-LogSummaryCounts -Path $previousReportPath
$logSummaryDeltas = @(Get-LogSummaryDeltas -CurrentRows $logSummaryRows -PreviousCounts $previousLogSummaryCounts)
$playbackPerfWarningClassifications = @(Get-PlaybackPerfWarningClassification -Lines $logLines -Limit $SampleLimit)
$playbackPerfWarningOriginClassifications = @(Get-PlaybackPerfWarningOriginClassification -Lines $logLines -AnalyzedLogPath $(if ($null -eq $resolvedLogPath) { "" } else { $resolvedLogPath }) -Limit $SampleLimit)
$playbackPerfWarningContextClassifications = @(Get-PlaybackPerfWarningContextClassification -Lines $logLines -WindowSeconds $PerfContextWindowSeconds -Limit $SampleLimit)
$playbackPerfTimingClassifications = @(Get-PlaybackPerfTimingClassification -Lines $logLines -Limit $SampleLimit)
$loadFileClassifications = @(Get-LoadFileClassification -Lines $logLines -Limit $SampleLimit)
$spoutOutputClassifications = @(Get-SpoutOutputClassification -Lines $logLines -Limit $SampleLimit)
$ltcInputClassifications = @(Get-LtcInputClassification -Lines $logLines -Limit $SampleLimit)
$continueSyncClassifications = @(Get-ContinueSyncClassification -Lines $logLines -Limit $SampleLimit)

$reasons = [System.Collections.Generic.List[string]]::new()
$failedCommands = @($commands | Where-Object { -not $_.Skipped -and $_.ExitCode -ne 0 })
foreach ($command in $failedCommands) {
    $reasons.Add("$($command.Name) failed with exit code $($command.ExitCode).")
}

$criticalErrorSummary = $patternSummaries | Where-Object { $_.Name -eq "Critical errors" } | Select-Object -First 1
$gapTimeoutSummary = $patternSummaries | Where-Object { $_.Name -eq "Gap freeze timeout" } | Select-Object -First 1
$perfWarningSummary = $patternSummaries | Where-Object { $_.Name -eq "Playback perf warning" } | Select-Object -First 1
$loadBurstSummary = $burstSummaries | Where-Object { $_.Name -eq "LoadFile bursts" } | Select-Object -First 1
$syncSeekBurstSummary = $burstSummaries | Where-Object { $_.Name -eq "Sync seek bursts" } | Select-Object -First 1
$continueSyncSeekBurstSummary = $burstSummaries | Where-Object { $_.Name -eq "Continue sync seek bursts" } | Select-Object -First 1

$criticalErrors = if ($null -eq $criticalErrorSummary) { 0 } else { $criticalErrorSummary.Count }
$gapTimeouts = if ($null -eq $gapTimeoutSummary) { 0 } else { $gapTimeoutSummary.Count }
$perfWarnings = if ($null -eq $perfWarningSummary) { 0 } else { $perfWarningSummary.Count }
$loadBursts = if ($null -eq $loadBurstSummary) { 0 } else { $loadBurstSummary.BurstCount }
$syncSeekBursts = if ($null -eq $syncSeekBurstSummary) { 0 } else { $syncSeekBurstSummary.BurstCount }
$continueSyncSeekBursts = if ($null -eq $continueSyncSeekBurstSummary) { 0 } else { $continueSyncSeekBurstSummary.BurstCount }

if ($null -eq $resolvedLogPath) {
    $reasons.Add("No log file was found for analysis.")
}

if ($criticalErrors -gt 0) {
    $reasons.Add("Critical log patterns detected: $criticalErrors.")
}

if ($gapTimeouts -gt 0) {
    $reasons.Add("Gap Freeze timeouts detected: $gapTimeouts.")
}

if ($perfWarnings -gt $PlaybackPerfWarningThreshold) {
    $reasons.Add("Playback perf warnings exceeded threshold $PlaybackPerfWarningThreshold`: $perfWarnings.")
}

if ($TreatWarningsAsFailure) {
    if ($loadBursts -gt 0) {
        $reasons.Add("LoadFile bursts detected: $loadBursts windows.")
    }
    if ($syncSeekBursts -gt 0) {
        $reasons.Add("Timecode sync seek bursts detected: $syncSeekBursts windows.")
    }
    if ($continueSyncSeekBursts -gt 0) {
        $reasons.Add("Continue mode sync seek bursts detected: $continueSyncSeekBursts windows.")
    }
}

$verdict = if ($reasons.Count -eq 0) { "OK" } elseif ($failedCommands.Count -gt 0 -or $criticalErrors -gt 0 -or $gapTimeouts -gt 0) { "FAIL" } else { "WARN" }
$endedAt = Get-Date
$reportName = "timecodesyncplayer-diagnostics-{0}.md" -f $startedAt.ToString("yyyyMMdd-HHmmss")
$reportPath = Join-Path $ReportDirectory $reportName

Write-Report `
    -Path $reportPath `
    -Commands $commands `
    -AnalyzedLogPath $(if ($null -eq $resolvedLogPath) { "(not found)" } else { $resolvedLogPath }) `
    -LogSummaryRows $logSummaryRows `
    -PreviousReportPath $previousReportPath `
    -LogSummaryDeltas $logSummaryDeltas `
    -PatternSummaries $patternSummaries `
    -BurstSummaries $burstSummaries `
    -PlaybackPerfWarningClassifications $playbackPerfWarningClassifications `
    -PlaybackPerfWarningOriginClassifications $playbackPerfWarningOriginClassifications `
    -PlaybackPerfWarningContextClassifications $playbackPerfWarningContextClassifications `
    -PlaybackPerfTimingClassifications $playbackPerfTimingClassifications `
    -LoadFileClassifications $loadFileClassifications `
    -SpoutOutputClassifications $spoutOutputClassifications `
    -LtcInputClassifications $ltcInputClassifications `
    -ContinueSyncClassifications $continueSyncClassifications `
    -Verdict $verdict `
    -Reasons $reasons.ToArray() `
    -Started $startedAt `
    -Ended $endedAt

Write-Host ""
Write-Host "Diagnostics report: $reportPath"
Write-Host "Verdict: $verdict"

if ($verdict -eq "FAIL" -or ($TreatWarningsAsFailure -and $verdict -eq "WARN")) {
    exit 1
}

exit 0
