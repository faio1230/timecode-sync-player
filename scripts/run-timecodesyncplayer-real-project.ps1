#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$ProjectPaths,
    [string]$ReportDirectory,
    [ValidateRange(1, 30)][int]$TimeoutMinutes = 3,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$appPath = [IO.Path]::GetFullPath((Join-Path $repoPath 'src/TimecodeSyncPlayer/bin/Debug/net8.0-windows/TimecodeSyncPlayer.exe'))
$project = Join-Path $repoPath 'tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj'
$dotnet = (Get-Command dotnet).Source
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $repoPath 'TestResults/real-project' }
$runPath = Join-Path $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportDirectory) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $runPath
if (-not $SkipBuild) {
    & $dotnet build $project -c Debug -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
}
$binaries = foreach ($binary in @($appPath, [IO.Path]::ChangeExtension($appPath, '.dll'), (Join-Path $repoPath 'tests/TimecodeSyncPlayer.Tests/bin/Debug/net8.0-windows/TimecodeSyncPlayer.Tests.dll'))) {
    [pscustomobject]@{ path = $binary; sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash }
}
[pscustomobject]@{ startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); revision = (& git -C $repoPath rev-parse HEAD); binaries = @($binaries); timeoutMinutes = $TimeoutMinutes } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runPath 'environment.json')
$logDirectory = Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'logs'

function Save-AppLogDelta {
    param([string]$Directory, [hashtable]$Offsets)
    $hasErrors = $false
    foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) {
        $offset = if ($Offsets.ContainsKey($logFile.FullName)) { [long]$Offsets[$logFile.FullName] } else { 0L }
        if ($logFile.Length -eq $offset) { continue }
        $destination = Join-Path $Directory ('app-' + $logFile.Name)
        $source = [IO.File]::Open($logFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            if ($source.Length -ge $offset) { $source.Position = $offset }
            $outputFile = [IO.File]::Create($destination)
            try { $source.CopyTo($outputFile) } finally { $outputFile.Dispose() }
        } finally { $source.Dispose() }
        if (Select-String -LiteralPath $destination -Pattern '\[(ERR|FTL)\]' -Quiet) { $hasErrors = $true }
    }
    return $hasErrors
}
$results = [Collections.Generic.List[object]]::new()
$index = 0
foreach ($original in $ProjectPaths) {
    $original = (Resolve-Path -LiteralPath $original).Path
    $index++
    $report = Join-Path $runPath ("{0:D2}-{1}" -f $index, [IO.Path]::GetFileNameWithoutExtension($original))
    $null = New-Item -ItemType Directory -Path $report
    $originalHash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    Write-Host "Checking $original; reports: $report"
    $info = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $info.WorkingDirectory = $repoPath
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($arg in @('test', $project, '-c', 'Debug', '--no-build', '--filter', 'Category=RealProject', '--logger', 'trx;LogFileName=real-project.trx', '--results-directory', $report, '-v', 'minimal')) { $info.ArgumentList.Add($arg) }
    $info.Environment['TIMECODE_SYNC_PLAYER_E2E_APP_PATH'] = $appPath
    $info.Environment['TIMECODE_REAL_PROJECT_PATH'] = $original
    $info.Environment['TIMECODE_REAL_PROJECT_REPORT_DIR'] = $report
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $success = $false
    $logOffsets = @{}
    foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) {
        $logOffsets[$logFile.FullName] = $logFile.Length
    }
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $lastLength = -1L
        $progressAt = 0.0
        while (-not $process.WaitForExit(1000)) {
            $journal = Get-Item -LiteralPath (Join-Path $report 'real-project.jsonl') -ErrorAction SilentlyContinue
            if ($journal -and $journal.Length -ne $lastLength) { $lastLength = $journal.Length; $progressAt = $clock.Elapsed.TotalSeconds }
            if ($clock.Elapsed.TotalMinutes -gt $TimeoutMinutes -or $clock.Elapsed.TotalSeconds - $progressAt -gt 60) {
                $failure = 'Test exceeded total or journal inactivity timeout.'
                $process.Kill($true)
                if (-not $process.WaitForExit(10000)) { throw 'Test process did not terminate.' }
                break
            }
        }
        if (-not $stdout.Wait(10000) -or -not $stderr.Wait(10000)) { throw 'Test output did not close.' }
        $stdout.Result | Set-Content -LiteralPath (Join-Path $report 'stdout.log')
        $stderr.Result | Set-Content -LiteralPath (Join-Path $report 'stderr.log')
        if (-not $failure -and $process.ExitCode -eq 0) {
            [xml]$trx = Get-Content -LiteralPath (Join-Path $report 'real-project.trx') -Raw
            $counter = $trx.TestRun.ResultSummary.Counters
            $events = @(Get-Content -LiteralPath (Join-Path $report 'real-project.jsonl') | ConvertFrom-Json)
            $success = ([int]$counter.total -eq 1 -and [int]$counter.passed -eq 1 -and @($events | Where-Object event -eq 'passed').Count -eq 1)
        }
        if (-not $success -and -not $failure) { $failure = 'Test failed, skipped, or lacks completion evidence.' }
    }
    catch { $failure = $_.ToString(); $success = $false }
    finally {
        try { if (-not $process.HasExited) { $process.Kill($true); if (-not $process.WaitForExit(10000)) { throw 'Test process survived termination.' } } } catch { $failure = "Process cleanup failed: $_"; $success = $false }
        $markerPath = Join-Path $report 'app-process.json'
        try { if (Test-Path -LiteralPath $markerPath) {
            $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
            $owned = Get-Process -Id $marker.processId -ErrorAction SilentlyContinue
            if ($owned -and $owned.StartTime.ToUniversalTime().Ticks -eq [long]$marker.startTimeUtcTicks -and $owned.Path -eq $appPath) {
                $owned.Kill($true)
                if (-not $owned.WaitForExit(5000)) { throw 'App survived forced termination.' }
                $success = $false
                $failure = "$failure App required forced cleanup."
            }
            if ($owned) { $owned.Dispose() }
        } } catch { $success = $false; $failure = "$failure App cleanup failed: $_" }
        try {
            if (Save-AppLogDelta $report $logOffsets) { $success = $false; $failure = "$failure App logged ERR/FTL; see app-*.log." }
        } catch { $success = $false; $failure = "$failure App log collection failed: $_" }
        $process.Dispose()
    }
    $finalHash = (Get-FileHash -LiteralPath $original -Algorithm SHA256).Hash
    if ($originalHash -ne $finalHash) { $success = $false; $failure = 'Original project changed.' }
    $result = [pscustomobject]@{ project = $original; success = $success; failure = $failure; originalHash = $originalHash; finalHash = $finalHash; elapsedSeconds = [Math]::Round($clock.Elapsed.TotalSeconds, 2); report = $report }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $report 'supervisor.json')
    $results.Add($result)
    $results.ToArray() | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $runPath 'runs.json')
    Write-Host "Success=$success $failure"
    if (-not $success) { exit 1 }
}
Write-Host "Reports: $runPath"
