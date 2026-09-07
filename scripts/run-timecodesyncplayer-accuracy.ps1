#requires -Version 7.2
[CmdletBinding()]
param(
    [string]$ReportDirectory,
    [ValidateRange(1, 30)][int]$TimeoutMinutes = 4,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$appPath = [IO.Path]::GetFullPath((Join-Path $repoPath 'src/TimecodeSyncPlayer/bin/Debug/net8.0-windows/TimecodeSyncPlayer.exe'))
$testProject = Join-Path $repoPath 'tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj'
$dotnet = (Get-Command dotnet).Source
$python = (Get-Command python).Source
$null = Get-Command ffmpeg
$null = Get-Command ffprobe
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $repoPath 'TestResults/accuracy' }
$reportRoot = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportDirectory)
$runPath = Join-Path $reportRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $runPath
Write-Host "Sync accuracy reports: $runPath"
if (-not $SkipBuild) {
    & $dotnet build $testProject -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$binaries = foreach ($binary in @($appPath, [IO.Path]::ChangeExtension($appPath, '.dll'), (Join-Path $repoPath 'tests/TimecodeSyncPlayer.Tests/bin/Debug/net8.0-windows/TimecodeSyncPlayer.Tests.dll'))) {
    [pscustomobject]@{ path = $binary; sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash }
}
[pscustomobject]@{ startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); revision = (& git -C $repoPath rev-parse HEAD); binaries = @($binaries); timeoutMinutes = $TimeoutMinutes; idleSeconds = 60; startupSeconds = 120 } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runPath 'environment.json')

$logDirectory = Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'logs'
$logOffsets = @{}
foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) { $logOffsets[$logFile.FullName] = $logFile.Length }
function Save-AppLogDelta {
    $saved = 0
    $hasErrors = $false
    foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) {
        $offset = if ($logOffsets.ContainsKey($logFile.FullName)) { [long]$logOffsets[$logFile.FullName] } else { 0L }
        if ($logFile.Length -eq $offset) { continue }
        $destination = Join-Path $runPath ('app-' + $logFile.Name)
        $source = [IO.File]::Open($logFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            if ($source.Length -ge $offset) { $source.Position = $offset }
            $outputFile = [IO.File]::Create($destination)
            try { $source.CopyTo($outputFile) } finally { $outputFile.Dispose() }
        } finally { $source.Dispose() }
        $saved++
        if (Select-String -LiteralPath $destination -Pattern '\[(ERR|FTL)\]' -Quiet) { $hasErrors = $true }
    }
    if ($saved -eq 0) { throw 'No app log output was collected.' }
    if ($hasErrors) { throw 'App logged ERR/FTL; see app-*.log.' }
}

$info = [Diagnostics.ProcessStartInfo]::new($dotnet)
$info.WorkingDirectory = $repoPath
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($arg in @('test', $testProject, '-c', 'Debug', '--no-build', '--filter', 'Category=Accuracy', '--logger', 'trx;LogFileName=accuracy.trx', '--results-directory', $runPath, '-v', 'minimal')) { $info.ArgumentList.Add($arg) }
$info.Environment['TIMECODE_SYNC_PLAYER_E2E_APP_PATH'] = $appPath
$info.Environment['TIMECODE_ACCURACY_REPORT_DIR'] = $runPath
# Instrument only the app explicitly launched by the test, never dotnet/testhost.
$null = $info.Environment.Remove('TIMECODE_ACCURACY_TRACE')
$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
$clock = [Diagnostics.Stopwatch]::StartNew()
$failure = $null
$success = $false
$started = $false
try {
    $started = $process.Start()
    if (-not $started) { throw 'Could not start test process.' }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $lastLength = -1L
    $progressAt = 0.0
    while (-not $process.WaitForExit(1000)) {
        $journal = Get-Item -LiteralPath (Join-Path $runPath 'harness.jsonl') -ErrorAction SilentlyContinue
        if ($journal -and $journal.Length -ne $lastLength) { $lastLength = $journal.Length; $progressAt = $clock.Elapsed.TotalSeconds }
        $idleLimit = if (Test-Path -LiteralPath (Join-Path $runPath 'app-process.json')) { 60 } else { 120 }
        if ($clock.Elapsed.TotalMinutes -gt $TimeoutMinutes -or $clock.Elapsed.TotalSeconds - $progressAt -gt $idleLimit) {
            throw 'Test exceeded total or journal inactivity timeout.'
        }
    }
    if (-not $stdout.Wait(10000) -or -not $stderr.Wait(10000)) { throw 'Test output did not close.' }
    $stdout.Result | Set-Content -LiteralPath (Join-Path $runPath 'stdout.log')
    $stderr.Result | Set-Content -LiteralPath (Join-Path $runPath 'stderr.log')
    if ($process.ExitCode -ne 0) { throw "Test exited with $($process.ExitCode)." }
    if ([string]::IsNullOrWhiteSpace($stdout.Result)) { throw 'Missing test standard output.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $runPath 'accuracy.trx') -Raw
    $counter = $trx.TestRun.ResultSummary.Counters
    if ([int]$counter.total -ne 1 -or [int]$counter.passed -ne 1) { throw 'Expected exactly one passed Accuracy test, without skips.' }
    $events = @(Get-Content -LiteralPath (Join-Path $runPath 'harness.jsonl') | ConvertFrom-Json)
    if (@($events | Where-Object event -eq 'passed').Count -ne 1) { throw 'Missing harness completion evidence.' }
    $phases = @(Get-Content -LiteralPath (Join-Path $runPath 'phases.jsonl') | ConvertFrom-Json)
    $expectedNames = @('black-sweep', 'freeze-sweep', 'seek-a', 'seek-b', 'seek-c', 'seek-back')
    $starts = @($phases | Where-Object type -eq 'phase-start')
    $ends = @($phases | Where-Object type -eq 'phase-end')
    if ($starts.Count -ne 6 -or $ends.Count -ne 6 -or $phases[-1].type -ne 'completed') { throw 'Incomplete phase journal.' }
    for ($index = 0; $index -lt 6; $index++) {
        if ($starts[$index].name -ne $expectedNames[$index] -or $ends[$index].name -ne $expectedNames[$index] -or $ends[$index].ticks -le $starts[$index].ticks) { throw 'Incorrect phase order or timing.' }
    }
    $tracePath = Join-Path $runPath 'trace.jsonl'
    $trace = @(Get-Content -LiteralPath $tracePath | ConvertFrom-Json)
    if ($trace.Count -lt 4 -or $trace[0].type -ne 'meta' -or $trace[-1].type -ne 'end' -or $trace[-1].dropped -ne 0 -or $trace[-1].errors -ne 0) { throw 'Trace lacks clean startup/shutdown evidence.' }
    if (@($trace | Where-Object type -eq 'frame').Count -lt 1 -or @($trace | Where-Object type -eq 'ltc').Count -lt 1) { throw 'Trace lacks frame or LTC observations.' }
    $success = $true
}
catch { $failure = $_.ToString(); $success = $false }
finally {
    try {
        if ($started -and -not $process.HasExited) {
            $process.Kill($true)
            if (-not $process.WaitForExit(10000)) { throw 'Test process survived termination.' }
            $success = $false
        }
        if ($started -and $stdout -and $stdout.Wait(5000)) { $stdout.Result | Set-Content -LiteralPath (Join-Path $runPath 'stdout.log') }
        if ($started -and $stderr -and $stderr.Wait(5000)) { $stderr.Result | Set-Content -LiteralPath (Join-Path $runPath 'stderr.log') }
    } catch { $failure = "$failure Process cleanup failed: $_"; $success = $false }
    $markerPath = Join-Path $runPath 'app-process.json'
    try {
        if (Test-Path -LiteralPath $markerPath) {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $owned = Get-Process -Id $marker.processId -ErrorAction SilentlyContinue
            if ($owned -and $owned.StartTime.ToUniversalTime().Ticks -eq [long]$marker.startTimeUtcTicks -and $owned.Path -eq $appPath) {
                $null = $owned.CloseMainWindow()
                if (-not $owned.WaitForExit(5000)) { $owned.Kill($true); if (-not $owned.WaitForExit(5000)) { throw 'App survived termination.' } }
                $success = $false
                $failure = "$failure App required supervisor cleanup."
            }
            if ($owned) { $owned.Dispose() }
        }
    } catch { $success = $false; $failure = "$failure App cleanup failed: $_" }
    try { Save-AppLogDelta } catch { $success = $false; $failure = "$failure $_" }
    $process.Dispose()
}

# Analyze even a partial trace: the analyzer records incompleteness instead of hiding it.
$analysisInputs = @('trace.jsonl', 'fixture.json', 'phases.jsonl')
if (@($analysisInputs | Where-Object { -not (Test-Path -LiteralPath (Join-Path $runPath $_)) }).Count -eq 0) {
    $analysisInfo = [Diagnostics.ProcessStartInfo]::new($python)
    $analysisInfo.UseShellExecute = $false
    $analysisInfo.CreateNoWindow = $true
    $analysisInfo.RedirectStandardOutput = $true
    $analysisInfo.RedirectStandardError = $true
    foreach ($arg in @((Join-Path $PSScriptRoot 'analyze-sync-accuracy.py'), '--trace', (Join-Path $runPath 'trace.jsonl'), '--fixture', (Join-Path $runPath 'fixture.json'), '--phases', (Join-Path $runPath 'phases.jsonl'), '--output', $runPath)) { $analysisInfo.ArgumentList.Add($arg) }
    $analysis = [Diagnostics.Process]::new()
    $analysis.StartInfo = $analysisInfo
    try {
        $null = $analysis.Start()
        $analysisOut = $analysis.StandardOutput.ReadToEndAsync()
        $analysisErr = $analysis.StandardError.ReadToEndAsync()
        if (-not $analysis.WaitForExit(60000)) { $analysis.Kill($true); $analysis.WaitForExit(); throw 'Analyzer timed out.' }
        if (-not $analysisOut.Wait(5000) -or -not $analysisErr.Wait(5000)) { throw 'Analyzer output did not close.' }
        $analysisOut.Result | Set-Content -LiteralPath (Join-Path $runPath 'analysis-stdout.log')
        $analysisErr.Result | Set-Content -LiteralPath (Join-Path $runPath 'analysis-stderr.log')
        if ($analysis.ExitCode -ne 0) { throw "Analyzer reported incomplete measurement ($($analysis.ExitCode))." }
        foreach ($artifact in @('accuracy-summary.json', 'accuracy-samples.csv', 'accuracy-report.md')) {
            $item = Get-Item -LiteralPath (Join-Path $runPath $artifact)
            if ($item.Length -eq 0) { throw "Empty analysis artifact: $artifact" }
        }
    } catch { $success = $false; $failure = "$failure Analysis failed: $_" }
    finally { $analysis.Dispose() }
} else { $success = $false; $failure = "$failure Missing analyzer inputs." }
[pscustomobject]@{ success = $success; failure = $failure; elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2); report = $runPath } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'supervisor.json')
Write-Host "Success=$success $failure"
Write-Host "Reports: $runPath"
if (-not $success) { exit 1 }
