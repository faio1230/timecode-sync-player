[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('SenderOnly','ExistingObs')][string]$Condition = 'SenderOnly',
    [int]$ObservedObsPid = 0,
    [string]$ExpectedObsExe,
    [ValidateRange(35,120)][int]$WatchdogSeconds = 45,
    [switch]$Preflight
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Output directory already exists; raw evidence will not be overwritten.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$probe = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'bin/Debug/net8.0-windows/SpoutTransportProbe.exe'))
$profile = Join-Path $PSScriptRoot 'SpoutGpu.wprp'
$instance = 'TimecodeSpout_' + [Guid]::NewGuid().ToString('N')
$cleanupErrors = [Collections.Generic.List[string]]::new()
$result = [ordered]@{ condition=$Condition; preflight=[bool]$Preflight; instance=$instance; startedUtc=[DateTime]::UtcNow.ToString('o'); recordingStarted=$false; traceSaved=$false; senderExit=$null; watchdog=$false; senderStillRunning=$false; error=$null; cleanupErrors=$cleanupErrors }
$owned = $null
function Write-Json([string]$Name, $Value) {
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $output $Name) -Encoding UTF8
}
try {
    $administrator = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $result['administrator'] = $administrator
    $processes = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(obs64|obs32|SpoutTransportProbe|SpoutReceiverProbePixels|SpoutReceiverProbe|TimecodeSyncPlayer|PureSpoutSender|LtcProbe|LtcGenerator)\.exe$' } | Select-Object ProcessId,Name,ExecutablePath,CreationDate)
    Write-Json 'processes-before.json' $processes
    $status = @(& wpr.exe -status 2>&1)
    $statusExit = $LASTEXITCODE
    $status | Set-Content -LiteralPath (Join-Path $output 'wpr-status-before.txt')
    $detail = @(& wpr.exe -profiledetails ($profile + '!SpoutGpu') 2>&1)
    $detailExit = $LASTEXITCODE
    $detail | Set-Content -LiteralPath (Join-Path $output 'wpr-profile.txt')
    if ($detailExit -ne 0) { throw 'WPR rejected the profile; no native process started.' }
    $hashPaths = @($probe, [IO.Path]::ChangeExtension($probe,'.dll'), (Join-Path (Split-Path $probe) 'TimecodeSyncPlayer.dll'), (Join-Path (Split-Path $probe) 'SpoutDX.dll'), $profile, $PSCommandPath)
    Write-Json 'binaries-before.json' @($hashPaths | ForEach-Object { Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Path,Hash })
    if ($Condition -eq 'SenderOnly') {
        if ($processes.Count) { throw 'SenderOnly requires no existing OBS/player/probe/audio test process.' }
    } else {
        if ($ObservedObsPid -le 0 -or -not $ExpectedObsExe) { throw 'ExistingObs requires the observed PID and exact executable path.' }
        $expected = (Resolve-Path -LiteralPath $ExpectedObsExe).Path
        $obs = @($processes | Where-Object { $_.ProcessId -eq $ObservedObsPid -and $_.Name -eq 'obs64.exe' -and $_.ExecutablePath -eq $expected })
        if ($obs.Count -ne 1 -or $processes.Count -ne 1) { throw 'Observed OBS identity or process conditions do not match.' }
        Write-Json 'observed-obs.json' $obs[0]
        # Observation only: this script never closes, configures, or installs plugins in OBS.
    }
    $result['profileValidated'] = $true
    $result['canRecord'] = $administrator -and $statusExit -eq 0 -and (($status -join "`n") -match 'WPR is not recording')
    if ($Preflight) { return }
    if (-not $administrator) { throw 'GPU/CPU scheduler capture requires an elevated PowerShell. No elevation or policy change was attempted.' }
    if (-not $result['canRecord']) { throw 'WPR is active or its idle state could not be confirmed. Existing recordings were not changed.' }
    $start = @(& wpr.exe -start ($profile + '!SpoutGpu') -instancename $instance 2>&1)
    $startExit = $LASTEXITCODE
    # Establish ownership before any log I/O which might throw.
    $result['recordingStarted'] = $startExit -eq 0
    $start | Set-Content -LiteralPath (Join-Path $output 'wpr-start.txt')
    if ($startExit -ne 0) { throw 'WPR start failed; no native process started.' }
    $senderOutput = Join-Path $output 'sender.jsonl'
    if ($senderOutput -match '["\r\n]') { throw 'Unsupported output path characters.' }
    $owned = Start-Process -FilePath $probe -ArgumentList ('"' + $senderOutput + '"') -WorkingDirectory (Split-Path $probe) -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'sender-stdout.txt') -RedirectStandardError (Join-Path $output 'sender-stderr.txt')
    $null = $owned.Handle
    Write-Json 'owned-sender.json' ([ordered]@{pid=$owned.Id; startUtc=$owned.StartTime.ToUniversalTime().ToString('o'); expectedExe=$probe; qpc=[Diagnostics.Stopwatch]::GetTimestamp(); frequency=[Diagnostics.Stopwatch]::Frequency})
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (-not $owned.HasExited -and $timer.Elapsed.TotalSeconds -lt $WatchdogSeconds) { Start-Sleep -Milliseconds 100; $owned.Refresh() }
    if (-not $owned.HasExited) { $result['watchdog']=$true; $owned.Kill() }
    if (-not $owned.WaitForExit(10000)) { $result['senderStillRunning']=$true; throw 'Owned sender did not exit after termination request.' }
    $result['senderExit'] = $owned.ExitCode
} catch {
    $result['error'] = $_.Exception.ToString()
    throw
} finally {
    try {
        if ($owned) {
            try {
                if (-not $owned.HasExited) {
                    $owned.Kill()
                    if (-not $owned.WaitForExit(10000)) { $result['senderStillRunning']=$true; $cleanupErrors.Add('Owned sender still running after bounded termination wait.') }
                }
                if ($owned.HasExited) { $result['senderExit']=$owned.ExitCode; $result['senderStillRunning']=$false }
            } catch { $result['senderStillRunning']=$true; $cleanupErrors.Add('Sender cleanup: ' + $_.Exception.ToString()) }
            finally { try { $owned.Dispose() } catch { $cleanupErrors.Add('Sender handle: ' + $_.Exception.ToString()) } }
        }
        if ($result['recordingStarted']) {
            try {
                # Stop only the unique instance whose start succeeded. Never use global cancel.
                $stop = @(& wpr.exe -stop (Join-Path $output 'gpu.etl') 'Spout failure QPC correlation' -skipPdbGen -instancename $instance 2>&1)
                $stopExit = $LASTEXITCODE
                $result['wprStopExit'] = $stopExit
                $result['traceSaved'] = $stopExit -eq 0 -and (Test-Path -LiteralPath (Join-Path $output 'gpu.etl'))
                if (-not $result['traceSaved']) { $cleanupErrors.Add('WPR trace save failed; inspect this instance and retained buffers.') }
                $stop | Set-Content -LiteralPath (Join-Path $output 'wpr-stop.txt')
            } catch { $cleanupErrors.Add('WPR stop/save: ' + $_.Exception.ToString()) }
            # A failed merge is retained for inspection; do not discard buffers with cancel.
        }
    } finally {
        $result['endedUtc'] = [DateTime]::UtcNow.ToString('o')
        try { Write-Json 'result.json' $result }
        catch { Write-Warning ('Could not save final result: ' + $_.Exception.Message) }
    }
}
if (-not $result['traceSaved'] -or $result['watchdog'] -or $result['senderStillRunning'] -or $result['senderExit'] -ne 0 -or $cleanupErrors.Count) { exit 1 }
