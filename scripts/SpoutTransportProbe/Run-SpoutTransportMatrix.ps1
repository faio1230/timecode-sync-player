[CmdletBinding()]
param(
    [ValidateRange(1, 1000)][int]$Runs = 20,
    [string]$SenderExe,
    [string]$ReceiverExe,
    [string]$ResultsRoot,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label = 'transport-matrix',
    [ValidateRange(35, 120)][int]$SenderTimeoutSeconds = 45,
    [ValidateRange(1, 30)][int]$BetweenRunsSeconds = 2,
    [switch]$StopOnReceiverMismatch,
    [string[]]$AdditionalConflictProcessNames = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Resolve script-relative defaults in the body instead of depending on the
# caller/module-autoload context during parameter binding.
if ([string]::IsNullOrWhiteSpace($SenderExe)) {
    $SenderExe = Join-Path $PSScriptRoot 'bin/Debug/net8.0-windows/SpoutTransportProbe.exe'
}
if ([string]::IsNullOrWhiteSpace($ResultsRoot)) {
    $ResultsRoot = Join-Path $PSScriptRoot '../../TestResults/obs-clean/runs'
}

function Write-NewJson([string]$Path, $Value) {
    $json = ConvertTo-Json -InputObject $Value -Depth 30
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try {
        $writer = [System.IO.StreamWriter]::new($stream, [System.Text.UTF8Encoding]::new($false))
        try { $writer.WriteLine($json) } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-NoConflictingProcesses {
    $names = @('TimecodeSyncPlayer', 'SpoutTransportProbe', 'PureSpoutSender',
        [System.IO.Path]::GetFileNameWithoutExtension($SenderExe)) + $AdditionalConflictProcessNames
    if ($ReceiverExe) { $names += [System.IO.Path]::GetFileNameWithoutExtension($ReceiverExe) }
    $conflicts = @(Get-Process | Where-Object { $_.ProcessName -in $names })
    if ($conflicts.Count -gt 0) {
        $description = ($conflicts | ForEach-Object { '{0} (PID {1})' -f $_.ProcessName, $_.Id }) -join ', '
        throw "Conflicting process exists; no existing process will be stopped: $description"
    }
}

function Get-BinaryHashes {
    $senderDirectory = Split-Path -Parent $SenderExe
    $paths = @($SenderExe, (Join-Path $senderDirectory 'SpoutTransportProbe.dll'),
        (Join-Path $senderDirectory 'TimecodeSyncPlayer.dll'), (Join-Path $senderDirectory 'SpoutDX.dll'))
    if ($ReceiverExe) { $paths += $ReceiverExe }
    foreach ($path in $paths) {
        $item = Get-Item -LiteralPath $path
        [pscustomobject]@{ path = $item.FullName; length = $item.Length;
            lastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o');
            sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash }
    }
}

function Start-OwnedProbe([string]$Executable, [string[]]$Arguments, [string]$Prefix) {
    # Windows file names cannot contain quotes. Reject control characters as well
    # before constructing the command line required by Windows PowerShell 5.1.
    foreach ($argument in $Arguments) {
        if ($argument -match '["\r\n]') { throw 'Unsupported character in probe argument.' }
    }
    $quoted = ($Arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
    $process = Start-Process -FilePath $Executable -ArgumentList $quoted -WorkingDirectory (Split-Path -Parent $Executable) `
        -WindowStyle Hidden -PassThru -RedirectStandardOutput "$Prefix-stdout.log" -RedirectStandardError "$Prefix-stderr.log"
    # Retain the process handle so exit status remains available after teardown.
    $null = $process.Handle
    return $process
}

function Wait-OwnedProcess($Process, [int]$TimeoutSeconds) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while (-not $Process.HasExited) {
        if ($timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) { return $false }
        Start-Sleep -Milliseconds 100
        $Process.Refresh()
    }
    $Process.WaitForExit()
    return $true
}

function Stop-OwnedProcess($Process) {
    if ($null -eq $Process) { return }
    $Process.Refresh()
    if (-not $Process.HasExited) {
        # Use only the Process object returned by our own Start-Process call.
        $Process.Kill()
        if (-not $Process.WaitForExit(5000)) { throw "Owned process $($Process.Id) did not stop within 5 seconds." }
    }
}

function Read-CompletedJsonl([string]$Path) {
    $rows = @(Get-Content -LiteralPath $Path -Encoding UTF8 | ForEach-Object { ConvertFrom-Json -InputObject $_ })
    $summaries = @($rows | Where-Object { $_.event -eq 'summary' })
    if ($rows.Count -eq 0 -or $summaries.Count -ne 1 -or $rows[-1].event -ne 'summary') {
        throw "Missing, duplicate or non-final summary: $Path"
    }
    [pscustomobject]@{ rows = $rows; summary = $summaries[0] }
}

$SenderExe = (Resolve-Path -LiteralPath $SenderExe).Path
if ($ReceiverExe) { $ReceiverExe = (Resolve-Path -LiteralPath $ReceiverExe).Path }
Assert-NoConflictingProcesses
$baselineHashes = @(Get-BinaryHashes)
$resultsPath = [System.IO.Path]::GetFullPath($ResultsRoot)
[void][System.IO.Directory]::CreateDirectory($resultsPath)
$batchDirectory = Join-Path $resultsPath ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $Label + '-' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $batchDirectory)
$batch = [ordered]@{ startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = $null;
    mode = $(if ($ReceiverExe) { 'independent-receiver' } else { 'sender-only' });
    plannedRuns = $Runs; completedRuns = 0; senderTimeoutSeconds = $SenderTimeoutSeconds;
    receiverDurationSeconds = 50; receiverPollMs = 8; betweenRunsSeconds = $BetweenRunsSeconds;
    stopOnReceiverMismatch = [bool]$StopOnReceiverMismatch;
    binaries = $baselineHashes; runs = @(); error = $null; succeeded = $false }
Write-NewJson (Join-Path $batchDirectory 'manifest.json') $batch
Write-Host "Results: $batchDirectory"
$batchFailed = $false
try {
    for ($index = 1; $index -le $Runs; $index++) {
        Assert-NoConflictingProcesses
        $currentHashes = @(Get-BinaryHashes)
        if (($currentHashes.sha256 -join ',') -ne ($baselineHashes.sha256 -join ',')) {
            throw 'Binaries changed since the batch started; fixed-condition execution stopped.'
        }
        $runDirectory = Join-Path $batchDirectory ('run-{0:D3}' -f $index)
        [void](New-Item -ItemType Directory -Path $runDirectory)
        $record = [ordered]@{ index = $index; startedUtc = [DateTime]::UtcNow.ToString('o'); finishedUtc = $null;
            binaries = $currentHashes; senderPid = $null; receiverPid = $null;
            senderExit = $null; receiverExit = $null; senderTimedOut = $false; receiverTimedOut = $false;
            senderSummary = $null; receiverSummary = $null; receiverSamples = $null;
            diagnosticWarningCount = 0; diagnosticErrorCount = 0;
            receiverMixedOrUnknownSamples = $null; succeeded = $false; error = $null }
        Write-NewJson (Join-Path $runDirectory 'manifest.json') $record
        $senderProcess = $null
        $receiverProcess = $null
        $stopPath = Join-Path $runDirectory 'receiver.stop'
        try {
            if ($ReceiverExe) {
                $receiverProcess = Start-OwnedProbe $ReceiverExe @('--sender', 'TimecodeSyncPlayer', '--output',
                    (Join-Path $runDirectory 'receiver.jsonl'), '--duration', '50', '--poll-ms', '8', '--stop-file', $stopPath) `
                    (Join-Path $runDirectory 'receiver')
                $record.receiverPid = $receiverProcess.Id
            }
            $senderProcess = Start-OwnedProbe $SenderExe @((Join-Path $runDirectory 'sender.jsonl')) (Join-Path $runDirectory 'sender')
            $record.senderPid = $senderProcess.Id
            if (-not (Wait-OwnedProcess $senderProcess $SenderTimeoutSeconds)) {
                $record.senderTimedOut = $true
                throw 'Sender watchdog expired.'
            }
            $record.senderExit = $senderProcess.ExitCode
            if ($receiverProcess) {
                [System.IO.File]::WriteAllText($stopPath, '')
                if (-not (Wait-OwnedProcess $receiverProcess 10)) {
                    $record.receiverTimedOut = $true
                    throw 'Receiver teardown watchdog expired.'
                }
                $record.receiverExit = $receiverProcess.ExitCode
            }
            $senderData = Read-CompletedJsonl (Join-Path $runDirectory 'sender.jsonl')
            $record.senderSummary = $senderData.summary
            $summary = $senderData.summary
            $record.diagnosticWarningCount = @($summary.diagnosticLogs | Where-Object { $_.level -eq 'Warning' }).Count
            $record.diagnosticErrorCount = @($summary.diagnosticLogs | Where-Object { $_.level -in @('Error', 'Fatal') }).Count
            if ($record.senderExit -ne 0 -or $summary.exitCode -ne 0 -or -not $summary.completed -or
                -not $summary.disposed -or $summary.error -or $summary.unavailableAfterSend -ne 0) {
                throw 'Sender failed; inspect sender.jsonl, stdout and stderr.'
            }
            $sends = @($senderData.rows | Where-Object { $_.event -eq 'send' })
            if ($summary.attemptedFrames -ne $sends.Count -or ($summary.attemptedFrames + $summary.missedScheduledSlots) -ne 1800) {
                throw 'Sender summary frame accounting is incomplete.'
            }
            if ($record.diagnosticErrorCount -gt 0) { throw 'Sender diagnostics contain Error/Fatal; batch stopped for investigation.' }
            if ($receiverProcess) {
                $receiverData = Read-CompletedJsonl (Join-Path $runDirectory 'receiver.jsonl')
                $record.receiverSummary = $receiverData.summary
                $frames = @($receiverData.rows | Where-Object { $_.event -eq 'frame' })
                $record.receiverSamples = $frames.Count
                if ($record.receiverExit -ne 0 -or $frames.Count -eq 0 -or $receiverData.summary.samples -ne $frames.Count -or
                    @($receiverData.rows | Where-Object { $_.event -eq 'error' }).Count -gt 0) {
                    throw 'Receiver failed or frame accounting is incomplete; inspect receiver.jsonl.'
                }
                foreach ($counter in @('errors', 'droppedLogEvents')) {
                    if ($receiverData.summary.PSObject.Properties.Name -contains $counter -and $receiverData.summary.$counter -ne 0) {
                        throw "Receiver summary has nonzero $counter; inspect receiver.jsonl."
                    }
                }
                $mixed = @($frames | Where-Object { $_.sampleBgrHex -notmatch '^(?:40){243}$|^(?:80){243}$|^(?:c0){243}$' })
                $record.receiverMixedOrUnknownSamples = $mixed.Count
                if ($StopOnReceiverMismatch -and $mixed.Count -gt 0) { throw 'Receiver sampled mixed or unknown pixels; strict receiver mode stopped the batch.' }
            }
            $record.succeeded = $true
        } catch {
            $record.error = $_.Exception.ToString()
        } finally {
            foreach ($ownedProcess in @($senderProcess, $receiverProcess)) {
                try { Stop-OwnedProcess $ownedProcess } catch {
                    $record.succeeded = $false
                    $record.error = "$($record.error)`nCleanup: $($_.Exception)"
                }
            }
            if ($senderProcess -and $senderProcess.HasExited) { $record.senderExit = $senderProcess.ExitCode }
            if ($receiverProcess -and $receiverProcess.HasExited) { $record.receiverExit = $receiverProcess.ExitCode }
            $record.finishedUtc = [DateTime]::UtcNow.ToString('o')
            Write-NewJson (Join-Path $runDirectory 'result.json') $record
            if ($senderProcess) { $senderProcess.Dispose() }
            if ($receiverProcess) { $receiverProcess.Dispose() }
        }
        $batch.runs += [pscustomobject]$record
        if (-not $record.succeeded) { throw "Run $index failed: $($record.error)" }
        $batch.completedRuns++
        Write-Host "Run $index/$Runs completed; schedule misses=$($record.senderSummary.missedScheduledSlots), max send ms=$($record.senderSummary.sendMaxMs)"
        if ($index -lt $Runs) { Start-Sleep -Seconds $BetweenRunsSeconds }
    }
    $finalHashes = @(Get-BinaryHashes)
    Write-NewJson (Join-Path $batchDirectory 'final-binaries.json') $finalHashes
    if (($finalHashes.sha256 -join ',') -ne ($baselineHashes.sha256 -join ',')) { throw 'Binaries changed during the batch.' }
    $batch.succeeded = $true
} catch {
    $batchFailed = $true
    $batch.error = $_.Exception.ToString()
    Write-Warning $batch.error
} finally {
    $batch.finishedUtc = [DateTime]::UtcNow.ToString('o')
    Write-NewJson (Join-Path $batchDirectory 'batch-result.json') $batch
}
if ($batchFailed) { exit 1 }
exit 0
