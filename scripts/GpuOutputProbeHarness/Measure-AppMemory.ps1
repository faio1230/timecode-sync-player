# V6 soak: sample WorkingSet/PrivateBytes of the trial app every -SampleSeconds and write
# <run>\memory-samples.csv. Run it as a background job before Invoke-AppGpuTrial.ps1; it exits
# by itself when the sampled process (PID + start time) is gone.
[CmdletBinding()]
param(
    # Empty defaults resolve to this repository, like Invoke-AppGpuTrial.ps1.
    [string]$LogRoot = '',
    [Parameter(Mandatory)][string]$Label,
    [string]$ProcessName = 'TimecodeSyncPlayer',
    [int]$SampleSeconds = 60,
    [int]$StartupTimeoutSeconds = 900,
    [int]$RunDirSearchSeconds = 120,
    # Explicit output directory for tests; empty = find the newest run dir named *-<Label>.
    [string]$RunDirectory = ''
)
$ErrorActionPreference = 'Stop'
if (-not $LogRoot) { $LogRoot = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'TestResults\gpu-app' }
$startedAt = Get-Date

$proc = $null
$deadline = (Get-Date).AddSeconds($StartupTimeoutSeconds)
while (-not $proc -and (Get-Date) -lt $deadline) {
    $proc = Get-Process -Name $ProcessName -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $proc) { Start-Sleep -Seconds 1 }
}
if (-not $proc) { Write-Output "MEMORY-ERROR process '$ProcessName' not seen within $StartupTimeoutSeconds s"; exit 1 }

$appPid = $proc.Id
$startUtc = $proc.StartTime.ToUniversalTime().ToString('o')

if (-not $RunDirectory) {
    if (-not (Test-Path $LogRoot)) { Write-Output "MEMORY-ERROR log root not found: $LogRoot"; exit 1 }
    $deadline = (Get-Date).AddSeconds($RunDirSearchSeconds)
    while (-not $RunDirectory -and (Get-Date) -lt $deadline) {
        # The trial creates the run dir before it starts the app, so a dir newer than this
        # process start always belongs to the current run.
        $cand = Get-ChildItem -Path $LogRoot -Directory -Filter ('*-' + $Label) -ErrorAction SilentlyContinue |
            Where-Object { $_.LastWriteTime -ge $startedAt.AddSeconds(-5) } |
            Sort-Object LastWriteTime | Select-Object -Last 1
        if ($cand) { $RunDirectory = $cand.FullName } else { Start-Sleep -Seconds 1 }
    }
}
if (-not $RunDirectory) { $RunDirectory = $LogRoot }
$csv = Join-Path $RunDirectory 'memory-samples.csv'
[IO.File]::WriteAllText($csv, 'utc,pid,workingSetBytes,privateBytes' + [Environment]::NewLine, [Text.UTF8Encoding]::new($false))
$utf8 = [Text.UTF8Encoding]::new($false)
$rows = 0
while ($true) {
    $p = Get-Process -Id $appPid -ErrorAction SilentlyContinue
    if (-not $p) { break }
    try {
        if ($p.StartTime.ToUniversalTime().ToString('o') -ne $startUtc) { break }
        $line = '{0},{1},{2},{3}' -f (Get-Date).ToUniversalTime().ToString('o'), $appPid, $p.WorkingSet64, $p.PrivateMemorySize64
        [IO.File]::AppendAllText($csv, $line + [Environment]::NewLine, $utf8)
    } catch { break }
    $rows++
    Start-Sleep -Seconds $SampleSeconds
}
Write-Output "MEMORY-CSV $csv"
Write-Output "MEMORY-ROWS $rows"
