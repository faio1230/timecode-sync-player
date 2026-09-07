#requires -Version 7.2
# Runs only explicitly enabled monkey tests. Each seed gets an isolated report directory.
[CmdletBinding()]
param(
    [int[]]$Seeds = @(4242, 1701, 9001),
    [ValidateRange(1, 100000)][int]$Actions = 250,
    [ValidateRange(1, 1440)][int]$TimeoutMinutes = 15,
    [ValidateRange(20, 600)][int]$IdleTimeoutSeconds = 60,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [string]$ReportDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoPath = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$appPath = [IO.Path]::GetFullPath((Join-Path $repoPath "src/TimecodeSyncPlayer/bin/$Configuration/net8.0-windows/TimecodeSyncPlayer.exe"))
$testProject = Join-Path $repoPath 'tests/TimecodeSyncPlayer.Tests/TimecodeSyncPlayer.Tests.csproj'
$dotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
if ($Seeds.Count -eq 0) { throw 'At least one seed is required.' }
if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repoPath 'TestResults/monkey'
}
$runDirectory = Join-Path ([IO.Path]::GetFullPath($ReportDirectory)) ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
$null = New-Item -ItemType Directory -Path $runDirectory

if (-not $SkipBuild) {
    & $dotnetPath build $testProject -c $Configuration -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: exit $LASTEXITCODE" }
}
if (-not (Test-Path -LiteralPath $appPath)) { throw "App not found: $appPath" }
$gitRevision = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $gitRevision = & git -C $repoPath rev-parse HEAD
}
$binaryEvidence = @()
foreach ($binary in @(
    $appPath,
    (Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'TimecodeSyncPlayer.dll'),
    (Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'libmpv-2.dll'),
    (Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'mpv-2.dll'),
    (Join-Path $repoPath "tests/TimecodeSyncPlayer.Tests/bin/$Configuration/net8.0-windows/TimecodeSyncPlayer.Tests.dll")
)) {
    if (Test-Path -LiteralPath $binary) {
        $binaryEvidence += [pscustomobject]@{ path = $binary; sha256 = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash }
    }
}
[pscustomobject]@{
    startedUtc = [DateTimeOffset]::UtcNow.ToString('O'); gitRevision = $gitRevision
    configuration = $Configuration; seeds = $Seeds; actions = $Actions
    timeoutMinutes = $TimeoutMinutes; idleTimeoutSeconds = $IdleTimeoutSeconds; binaries = $binaryEvidence
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'environment.json')

function Stop-OwnedApp {
    param([string]$Directory)
    $identityPath = Join-Path $Directory 'app-process.json'
    if (-not (Test-Path -LiteralPath $identityPath)) { return }
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    $ownedApp = Get-Process -Id $identity.processId -ErrorAction SilentlyContinue
    if ($null -eq $ownedApp) { return }
    try {
        if ($ownedApp.StartTime.ToUniversalTime().Ticks -ne [long]$identity.startTimeUtcTicks) { return }
        if (-not [string]::Equals($ownedApp.Path, $appPath, [StringComparison]::OrdinalIgnoreCase)) { return }
        $ownedApp.Kill($true)
        if (-not $ownedApp.WaitForExit(5000)) { throw 'Owned app survived forced termination.' }
        throw 'Owned app required forced cleanup instead of confirmed normal exit.'
    }
    finally { $ownedApp.Dispose() }
}

function Save-AppLogDelta {
    param([string]$Directory, [hashtable]$Offsets)
    $hasErrors = $false
    $logDirectory = Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'logs'
    foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) {
        $offset = if ($Offsets.ContainsKey($logFile.FullName)) { [long]$Offsets[$logFile.FullName] } else { 0L }
        if ($logFile.Length -eq $offset) { continue }
        $destination = Join-Path $Directory ("app-" + $logFile.Name)
        $source = [IO.File]::Open($logFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            if ($source.Length -ge $offset) { $source.Position = $offset }
            $outputFile = [IO.File]::Create($destination)
            try { $source.CopyTo($outputFile) } finally { $outputFile.Dispose() }
        }
        finally { $source.Dispose() }
        if (Select-String -LiteralPath $destination -Pattern '\[(ERR|FTL)\]' -Quiet) { $hasErrors = $true }
    }
    return $hasErrors
}

$runs = [Collections.Generic.List[object]]::new()
$runIndex = 0
foreach ($seed in $Seeds) {
    $runIndex++
    $seedDirectory = Join-Path $runDirectory ("{0:D2}-seed-{1}" -f $runIndex, $seed)
    $null = New-Item -ItemType Directory -Path $seedDirectory
    Write-Host "Seed $seed / $Actions operations. Reports: $seedDirectory"
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $dotnetPath
    $startInfo.WorkingDirectory = $repoPath
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('test', $testProject, '-c', $Configuration, '--no-build', '--filter', 'Category=Monkey', '--logger', 'trx;LogFileName=monkey.trx', '--results-directory', $seedDirectory, '-v', 'minimal')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $startInfo.Environment['TIMECODE_SYNC_PLAYER_E2E_APP_PATH'] = $appPath
    $startInfo.Environment['TIMECODE_MONKEY_ENABLED'] = '1'
    $startInfo.Environment['TIMECODE_MONKEY_SEED'] = [string]$seed
    $startInfo.Environment['TIMECODE_MONKEY_ACTIONS'] = [string]$Actions
    $startInfo.Environment['TIMECODE_MONKEY_REPORT_DIR'] = $seedDirectory
    $testProcess = [Diagnostics.Process]::new()
    $testProcess.StartInfo = $startInfo
    $reason = $null
    $exitCode = -1
    $success = $false
    $logOffsets = @{}
    $logDirectory = Join-Path ([IO.Path]::GetDirectoryName($appPath)) 'logs'
    foreach ($logFile in @(Get-ChildItem -LiteralPath $logDirectory -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue)) {
        $logOffsets[$logFile.FullName] = $logFile.Length
    }
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    try {
        if (-not $testProcess.Start()) { throw 'Could not start test process.' }
        $stdout = $testProcess.StandardOutput.ReadToEndAsync()
        $stderr = $testProcess.StandardError.ReadToEndAsync()
        $journalPath = Join-Path $seedDirectory 'monkey.jsonl'
        $lastJournalLength = -1L
        $lastProgressSeconds = 0.0
        while (-not $testProcess.WaitForExit(1000)) {
            if ($stopwatch.Elapsed.TotalMinutes -gt $TimeoutMinutes) {
                $reason = "Total timeout ($TimeoutMinutes minutes)"
                break
            }
            if (Test-Path -LiteralPath $journalPath) {
                $journalLength = (Get-Item -LiteralPath $journalPath).Length
                if ($journalLength -ne $lastJournalLength) {
                    $lastJournalLength = $journalLength
                    $lastProgressSeconds = $stopwatch.Elapsed.TotalSeconds
                }
                $idle = $stopwatch.Elapsed.TotalSeconds - $lastProgressSeconds
                if ($idle -gt $IdleTimeoutSeconds) {
                    $reason = "No journal progress for $IdleTimeoutSeconds seconds (possible hang)"
                    break
                }
            }
            elseif ($stopwatch.Elapsed.TotalSeconds -gt 120) {
                $reason = 'No journal created within startup allowance (120 seconds)'
                break
            }
        }
        if ($reason) {
            $testProcess.Kill($true)
            if (-not $testProcess.WaitForExit(10000)) { throw 'Test process did not exit after termination.' }
        }
        $exitCode = $testProcess.ExitCode
        # Streams are drained concurrently, so a full output pipe cannot deadlock the test.
        if ($stdout.Wait(10000)) { $stdout.Result | Set-Content -LiteralPath (Join-Path $seedDirectory 'stdout.log') }
        else { $reason = 'Timed out collecting test stdout' }
        if ($stderr.Wait(10000)) { $stderr.Result | Set-Content -LiteralPath (Join-Path $seedDirectory 'stderr.log') }
        else { $reason = 'Timed out collecting test stderr' }

        $trxPath = Join-Path $seedDirectory 'monkey.trx'
        $summaryPath = Join-Path $seedDirectory 'summary.json'
        if (-not $reason -and $exitCode -eq 0 -and (Test-Path -LiteralPath $trxPath) -and (Test-Path -LiteralPath $summaryPath)) {
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
            $success = ([int]$counters.total -gt 0 -and [int]$counters.passed -eq [int]$counters.total -and $summary.success -eq $true -and [int]$summary.seed -eq $seed -and [int]$summary.requestedActions -eq $Actions -and [int]$summary.completedActions -eq $Actions -and [int]$summary.executedActions -gt 0)
        }
        if (-not $success -and -not $reason) { $reason = 'Test failed, skipped, or did not produce complete passing evidence.' }
    }
    catch {
        $reason = $_.ToString()
        try { if (-not $testProcess.HasExited) { $testProcess.Kill($true); $null = $testProcess.WaitForExit(10000) } } catch { }
    }
    finally {
        try { Stop-OwnedApp $seedDirectory } catch { $success = $false; $reason = "Owned app cleanup failed: $_" }
        try {
            if (Save-AppLogDelta $seedDirectory $logOffsets) {
                $success = $false
                $reason = "$reason Application logged ERR/FTL; see app-*.log.".Trim()
            }
        }
        catch { $success = $false; $reason = "$reason App log collection failed: $_".Trim() }
        $testProcess.Dispose()
        $stopwatch.Stop()
    }
    $result = [pscustomobject]@{
        seed = $seed; actions = $Actions; success = $success; exitCode = $exitCode
        elapsedSeconds = [Math]::Round($stopwatch.Elapsed.TotalSeconds, 2)
        reason = $reason; directory = $seedDirectory
    }
    $result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $seedDirectory 'supervisor.json')
    $runs.Add($result)
    $runs.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'runs.json')
    Write-Host "Seed $seed success=$success elapsed=$($result.elapsedSeconds)s $reason"
    if (-not $success) { break }
}
Write-Host "Reports: $runDirectory"
if (@($runs | Where-Object { -not $_.success }).Count -gt 0) { exit 1 }
exit 0
