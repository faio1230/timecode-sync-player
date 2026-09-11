# One explicitly requested native run. Do not wrap this script in an unattended matrix.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppExe,
    [Parameter(Mandatory)][string]$ReceiverExe,
    [ValidateSet('official','none')][string]$ReceiverMode = 'official',
    [Parameter(Mandatory)][ValidateSet('common','split')][string]$Mode,
    [Parameter(Mandatory)][ValidateSet('fullscreen','spout','both')][string]$Output,
    [ValidateRange(16,8192)][int]$Width = 1920,
    [ValidateRange(16,8192)][int]$Height = 1080,
    [ValidateRange(1,240)][double]$Fps = 60,
    [ValidateRange(0,8)][int]$MutexWaitMs = 0,
    [ValidateRange(0,1)][int]$PresentWaitMs = 0,
    [ValidateSet('fixed','abba','baab')][string]$PresentWaitPlan = 'fixed',
    [ValidateSet('tick','ready','vsync','vblank')][string]$DisplayPacing = 'tick',
    [ValidateRange(0.5,8)][double]$PresentMarginMs = 3,
    [ValidateSet('off','vblank')][string]$ComposeAlign = 'off',
    [ValidateRange(0.5,8)][double]$ComposeLeadMs = 1.5,
    [ValidateSet('off','signal')][string]$CopyRetry = 'off',
    [ValidateSet('keyed','fence')][string]$SourceSync = 'keyed',
    [ValidateSet('pattern','contract-fake')][string]$Source = 'pattern',
    [string]$SourceSize = '',
    [double]$SendPhaseMs = 0,
    [ValidateRange(1,600)][int]$Seconds = 12,
    [ValidateRange(0,60)][int]$Warmup = 2,
    [ValidateRange(0,32)][int]$MonitorIndex = 0,
    [switch]$Windowed,
    [Parameter(Mandatory)][string]$LogRoot,
    [ValidateRange(5,120)][int]$ExitGraceSeconds = 30
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$ReceiverMode = $ReceiverMode.ToLowerInvariant()
$PresentWaitPlan = $PresentWaitPlan.ToLowerInvariant()
$DisplayPacing = $DisplayPacing.ToLowerInvariant()
$ComposeAlign = $ComposeAlign.ToLowerInvariant()
$CopyRetry = $CopyRetry.ToLowerInvariant()
$SourceSync = $SourceSync.ToLowerInvariant()
$Source = $Source.ToLowerInvariant()
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { throw 'This runner requires Windows PowerShell or PowerShell on Windows.' }
if (2 * $Warmup -ge $Seconds) { throw 'Seconds must be greater than twice Warmup.' }
if ([double]::IsNaN($SendPhaseMs) -or [double]::IsInfinity($SendPhaseMs) -or $SendPhaseMs -lt 0 -or $SendPhaseMs -ge 1000.0/$Fps) { throw 'SendPhaseMs must be finite and satisfy 0 <= phase < 1000/Fps.' }
if ($SendPhaseMs -ne 0 -and ($Mode -ne 'split' -or $Output -eq 'fullscreen')) { throw 'Nonzero SendPhaseMs requires split mode with Spout output.' }
if ($ReceiverMode -eq 'none' -and $Output -eq 'fullscreen') { throw 'ReceiverMode none is a sender control and requires Spout output.' }
if ($PresentWaitMs -ne 0 -and $Output -eq 'spout') { throw 'Nonzero PresentWaitMs requires fullscreen or both output.' }
if ($PresentWaitPlan -ne 'fixed' -and ($Output -eq 'spout' -or $PresentWaitMs -ne 0 -or $Seconds/4.0 -le 2*$Warmup)) { throw 'A nonfixed PresentWaitPlan requires display output, PresentWaitMs 0, and Seconds/4 > 2*Warmup.' }
if ($DisplayPacing -eq 'ready' -and ($Mode -ne 'split' -or $Output -ne 'both' -or $PresentWaitMs -ne 0 -or $PresentWaitPlan -ne 'fixed')) { throw 'DisplayPacing ready requires split/both, PresentWaitMs 0, and PresentWaitPlan fixed.' }
if ($DisplayPacing -eq 'vsync' -and ($Output -eq 'spout' -or $PresentWaitMs -ne 0 -or $PresentWaitPlan -ne 'fixed')) { throw 'DisplayPacing vsync requires fullscreen or both output, PresentWaitMs 0, and PresentWaitPlan fixed.' }
if ($DisplayPacing -eq 'vblank' -and ($Output -eq 'spout' -or $PresentWaitMs -ne 0 -or $PresentWaitPlan -ne 'fixed')) { throw 'DisplayPacing vblank requires fullscreen or both output, PresentWaitMs 0, and PresentWaitPlan fixed.' }
if ($PresentMarginMs -ne 3 -and $DisplayPacing -ne 'vblank') { throw 'A non-default PresentMarginMs requires DisplayPacing vblank.' }
if ($ComposeAlign -eq 'vblank' -and ($DisplayPacing -ne 'vblank' -or $Output -eq 'spout')) { throw 'ComposeAlign vblank requires DisplayPacing vblank with fullscreen or both output.' }
if ($ComposeLeadMs -ne 1.5 -and $ComposeAlign -ne 'vblank') { throw 'A non-default ComposeLeadMs requires ComposeAlign vblank.' }
if ($CopyRetry -eq 'signal' -and ($Mode -ne 'split' -or $Output -eq 'fullscreen')) { throw 'CopyRetry signal requires split mode with Spout output.' }
if ($SourceSync -eq 'fence' -and ($Mode -ne 'split' -or $Output -eq 'fullscreen')) { throw 'SourceSync fence requires split mode with Spout output.' }
if ($SourceSync -eq 'fence' -and $CopyRetry -eq 'signal') { throw 'SourceSync fence has no keyed mutex to retry; use CopyRetry off.' }
$probeAppPath = (Resolve-Path -LiteralPath $AppExe).ProviderPath
$probeReceiverPath = (Resolve-Path -LiteralPath $ReceiverExe).ProviderPath
if ([IO.Path]::GetFileName($probeReceiverPath) -ine 'WinSpoutDXreceiver.exe') {
    throw 'ReceiverExe must point to the official WinSpoutDXreceiver.exe. A filename is not provenance verification; record/check the supplied binary hash.'
}
foreach ($probePath in @($probeAppPath, $probeReceiverPath)) {
    if (-not (Test-Path -LiteralPath $probePath -PathType Leaf)) { throw "Not a file: $probePath" }
}
$probeRoot = [IO.Path]::GetFullPath($LogRoot)
$probeRunName = '{0}-{1}-{2}-{3}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'), $Mode, $Output, [Guid]::NewGuid().ToString('N')
$probeRun = Join-Path $probeRoot $probeRunName
# New-Item without Force refuses reuse; all writes below target only this newly owned run.
New-Item -ItemType Directory -Path $probeRun | Out-Null
$probeAppLogs = Join-Path $probeRun 'app'
$probeReceiverWork = Join-Path $probeRun 'receiver-work'
New-Item -ItemType Directory -Path $probeReceiverWork | Out-Null
$probeSender = 'GPUProbe-' + [Guid]::NewGuid().ToString('N').Substring(0,16)
function Write-ProbeJson([string]$Path, $Value) {
    $probeJson = ConvertTo-Json -InputObject $Value -Depth 12
    $probeStream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        $probeWriter = [IO.StreamWriter]::new($probeStream, [Text.UTF8Encoding]::new($false))
        try { $probeWriter.Write($probeJson) } finally { $probeWriter.Dispose() }
    } finally { $probeStream.Dispose() }
}
function Get-ProbeIdentity([Diagnostics.Process]$Process, [string]$Path) {
    $null = $Process.Handle # Retain the process handle, including after exit.
    return [ordered]@{ pid=$Process.Id; startTimeUtcTicks=$Process.StartTime.ToUniversalTime().Ticks; startUtc=$Process.StartTime.ToUniversalTime().ToString('o'); observedAliveQpc=[Diagnostics.Stopwatch]::GetTimestamp(); exe=$Path }
}
function Test-ProbeOwnedAlive([Diagnostics.Process]$Process, $Identity) {
    if ($Process.HasExited) { return $false }
    # Never target a name match or a PID reused by an unrelated process.
    $probeCheck = Get-Process -Id $Identity.pid -ErrorAction SilentlyContinue
    if (-not $probeCheck) { return $false }
    try { return $probeCheck.StartTime.ToUniversalTime().Ticks -eq $Identity.startTimeUtcTicks }
    finally { $probeCheck.Dispose() }
}
if (-not ('GpuProbeHarnessWindows' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
public static class GpuProbeHarnessWindows {
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
    public sealed class Window { public long hwnd; public string title; public Rect bounds; public bool visible; }
    public static Window[] Snapshot(uint pid) {
        var windows = new List<Window>();
        EnumWindows((hwnd, param) => {
            uint actual; GetWindowThreadProcessId(hwnd, out actual);
            if (actual == pid) {
                var title = new StringBuilder(1024);
                GetWindowText(hwnd, title, title.Capacity);
                Rect bounds; GetWindowRect(hwnd, out bounds);
                windows.Add(new Window { hwnd=hwnd.ToInt64(), title=title.ToString(), bounds=bounds, visible=IsWindowVisible(hwnd) });
            }
            return true;
        }, IntPtr.Zero);
        return windows.ToArray();
    }
    public static int CloseOwned(uint pid) {
        int count = 0;
        EnumWindows((hwnd, param) => {
            uint actual; GetWindowThreadProcessId(hwnd, out actual);
            if (actual == pid && PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero)) count++;
            return true;
        }, IntPtr.Zero);
        return count;
    }
}
'@
}
$probeArgs = @('--mode',$Mode,'--output',$Output,'--width',"$Width",'--height',"$Height",'--fps',$Fps.ToString([Globalization.CultureInfo]::InvariantCulture),'--mutex-wait-ms',"$MutexWaitMs",'--present-wait-ms',"$PresentWaitMs",'--present-wait-plan',$PresentWaitPlan,'--display-pacing',$DisplayPacing,'--present-margin-ms',$PresentMarginMs.ToString([Globalization.CultureInfo]::InvariantCulture),'--compose-align',$ComposeAlign,'--compose-lead-ms',$ComposeLeadMs.ToString([Globalization.CultureInfo]::InvariantCulture),'--copy-retry',$CopyRetry,'--source-sync',$SourceSync,'--source',$Source,'--source-size',$SourceSize,'--send-phase-ms',$SendPhaseMs.ToString([Globalization.CultureInfo]::InvariantCulture),'--seconds',"$Seconds",'--warmup',"$Warmup",'--monitor-index',"$MonitorIndex",'--log-dir',$probeAppLogs,'--sender',$probeSender)
if ($SourceSize -eq '') { $i = [Array]::IndexOf($probeArgs, '--source-size'); $probeArgs = @($probeArgs[0..($i-1)] + $probeArgs[($i+2)..($probeArgs.Length-1)]) } elseif ($SourceSize -notmatch '^[0-9]{1,4}x[0-9]{1,4}$') { throw 'SourceSize must be WxH.' }
if ($Windowed) { $probeArgs += '--windowed' }
# Start-Process joins arguments. These arguments are simple tokens or a file path;
# quote each token using Windows CRT quoting, without invoking another shell.
$probeQuotedArgs = @($probeArgs | ForEach-Object {
    if ($_ -match '[\r\n\x00"]') { throw 'Unsupported quote or control character in argument.' }
    '"' + ($_ -replace '(\\+)$','$1$1') + '"'
})
$probeResult = [ordered]@{ schemaVersion=1; startedUtc=[DateTime]::UtcNow.ToString('o'); runDirectory=$probeRun; app=$null; receiverMode=$ReceiverMode; receiver=$null; appExit=$null; appStillRunning=$false; timedOut=$false; receiverExit=$null; receiverForced=$false; receiverCloseMessages=0; error=$null; completedNormally=$false; receiverBindingVerified=$false; receiverLimitation='Official sample has no sender CLI; binding, unique image receipt and physical display are not verified by this runner.' }
if ($ReceiverMode -eq 'none') { $probeResult.receiverLimitation='No owned receiver is launched. This does not prove other applications are disconnected and is not receiver performance validation.' }
$probeApp = $null
$probeReceiver = $null
$probeReceiverSamples = [Collections.Generic.List[object]]::new()
$probeRunnerMutex = $null
$probeRunnerMutexOwned = $false
try {
    $probeRunnerMutex = [Threading.Mutex]::new($false, 'Local\TimecodeSyncPlayerGpuOutputProbeHarness')
    try { $probeRunnerMutexOwned = $probeRunnerMutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $probeRunnerMutexOwned = $true }
    if (-not $probeRunnerMutexOwned) { throw 'Another GPU probe runner is active. Nothing started.' }
    $probeHashPaths = @($probeAppPath, [IO.Path]::ChangeExtension($probeAppPath,'.dll'), (Join-Path (Split-Path $probeAppPath) 'SpoutDX.dll'), $probeReceiverPath, (Join-Path (Split-Path $probeReceiverPath) 'SpoutDX.dll')) | Select-Object -Unique
    $probeHashes = @($probeHashPaths | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | ForEach-Object {Get-FileHash -LiteralPath $_ -Algorithm SHA256 | Select-Object Path,Hash,Algorithm})
    $probePreflight = [ordered]@{ utc=[DateTime]::UtcNow.ToString('o'); computer=$env:COMPUTERNAME; powerShell=$PSVersionTable.PSVersion.ToString(); processes=@(Get-Process | Select-Object Id,ProcessName); videoControllers=@(); monitors=@(); errors=@() }
    try { $probePreflight.videoControllers = @(Get-CimInstance Win32_VideoController | Select-Object Name,PNPDeviceID,DriverVersion,DriverDate,VideoModeDescription,CurrentHorizontalResolution,CurrentVerticalResolution,CurrentRefreshRate,Status) } catch { $probePreflight.errors += $_.Exception.Message }
    try { $probePreflight.monitors = @(Get-CimInstance Win32_DesktopMonitor | Select-Object Name,PNPDeviceID,ScreenWidth,ScreenHeight,Status) } catch { $probePreflight.errors += $_.Exception.Message }
    Write-ProbeJson (Join-Path $probeRun 'preflight.json') $probePreflight
    Write-ProbeJson (Join-Path $probeRun 'inputs.json') $probeHashes
    Write-ProbeJson (Join-Path $probeRun 'command.json') ([ordered]@{executable=$probeAppPath;arguments=$probeArgs;workingDirectory=(Split-Path $probeAppPath);receiverMode=$ReceiverMode;receiverExecutable=$probeReceiverPath;receiverArguments=@();receiverWorkingDirectory=$probeReceiverWork;sender=$probeSender;timeoutSeconds=($Seconds+$ExitGraceSeconds);mutexWaitMs=$MutexWaitMs;presentWaitMs=$PresentWaitMs;presentWaitPlan=$PresentWaitPlan;displayPacing=$DisplayPacing;presentMarginMs=$PresentMarginMs;composeAlign=$ComposeAlign;composeLeadMs=$ComposeLeadMs;copyRetry=$CopyRetry;sourceSync=$SourceSync;source=$Source;sourceSize=$SourceSize;sendPhaseMs=$SendPhaseMs;windowed=[bool]$Windowed})
    $probeExisting = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($probeAppPath)) -ErrorAction SilentlyContinue)
    foreach ($probeExistingProcess in $probeExisting) {
        try {
            $probeExistingPath = $probeExistingProcess.Path
            if (-not $probeExistingPath -or [string]::Equals($probeExistingPath, $probeAppPath, [StringComparison]::OrdinalIgnoreCase)) {
                throw 'This probe executable is already running (or an existing namesake path is inaccessible). Nothing started; finish or inspect the earlier trial first.'
            }
        } finally { $probeExistingProcess.Dispose() }
    }
    # This is the requested interactive probe: its responsive force-exit UI must remain accessible.
    $probeApp = Start-Process -FilePath $probeAppPath -ArgumentList $probeQuotedArgs -WorkingDirectory (Split-Path $probeAppPath) -WindowStyle Normal -PassThru -RedirectStandardOutput (Join-Path $probeRun 'app-stdout.txt') -RedirectStandardError (Join-Path $probeRun 'app-stderr.txt')
    $probeResult.app = Get-ProbeIdentity $probeApp $probeAppPath
    Write-ProbeJson (Join-Path $probeRun 'app-owned.json') $probeResult.app
    $probeDeadline = [Diagnostics.Stopwatch]::StartNew()
    if ($Output -ne 'fullscreen' -and $ReceiverMode -eq 'official') {
        # Start the receiver after the probe has had an opportunity to register. This
        # does not prove sender selection; no global active-sender setting is changed.
        $probeStartup = [Diagnostics.Stopwatch]::StartNew()
        while ($probeStartup.Elapsed.TotalSeconds -lt 1 -and -not $probeApp.HasExited) { Start-Sleep -Milliseconds 100 }
        if ($probeApp.HasExited) { throw 'Probe exited before receiver startup.' }
        $probeReceiver = Start-Process -FilePath $probeReceiverPath -WorkingDirectory $probeReceiverWork -WindowStyle Hidden -PassThru
        $probeResult.receiver = Get-ProbeIdentity $probeReceiver $probeReceiverPath
        Write-ProbeJson (Join-Path $probeRun 'receiver-owned.json') $probeResult.receiver
        $probeReceiverSamples.Add([ordered]@{qpc=[Diagnostics.Stopwatch]::GetTimestamp();alive=$true;windows=@([GpuProbeHarnessWindows]::Snapshot([uint32]$probeReceiver.Id))})
    }
    $probeLastSampleSeconds = -1.0
    while (-not $probeApp.HasExited -and $probeDeadline.Elapsed.TotalSeconds -lt ($Seconds + $ExitGraceSeconds)) {
        if ($probeReceiver -and $probeReceiver.HasExited) { throw 'Owned receiver exited while probe was still running; run is invalid.' }
        if ($probeReceiver -and $probeDeadline.Elapsed.TotalSeconds - $probeLastSampleSeconds -ge 1) {
            $probeReceiverSamples.Add([ordered]@{qpc=[Diagnostics.Stopwatch]::GetTimestamp();alive=$true;windows=@([GpuProbeHarnessWindows]::Snapshot([uint32]$probeReceiver.Id))})
            $probeLastSampleSeconds = $probeDeadline.Elapsed.TotalSeconds
        }
        Start-Sleep -Milliseconds 200
    }
    if (-not $probeApp.HasExited) {
        $probeResult.timedOut = $true
        $probeResult.appStillRunning = $true
        throw "Probe exceeded deadline; it has NOT been killed. Use its own force-exit UI. PID $($probeResult.app.pid). Do not start another trial."
    }
    $probeResult.appExit = $probeApp.ExitCode
    if ($probeResult.appExit -ne 0) { throw "Probe exit code $($probeResult.appExit); stop and inspect logs before another trial." }
} catch { $probeResult.error = $_.Exception.Message }
finally {
    if ($probeReceiver) {
        try {
            $probeReceiverSamples.Add([ordered]@{qpc=[Diagnostics.Stopwatch]::GetTimestamp();alive=(-not $probeReceiver.HasExited);windows=@()})
            if (Test-ProbeOwnedAlive $probeReceiver $probeResult.receiver) {
                $probeResult.receiverCloseMessages = [GpuProbeHarnessWindows]::CloseOwned([uint32]$probeResult.receiver.pid)
                if (-not $probeReceiver.WaitForExit(5000)) {
                    if (Test-ProbeOwnedAlive $probeReceiver $probeResult.receiver) {
                        $probeResult.receiverForced = $true
                        $probeReceiver.Kill() # Only original owned process, after PID + creation-time check.
                        $null = $probeReceiver.WaitForExit(5000)
                    }
                }
            }
            if ($probeReceiver.HasExited) { $probeResult.receiverExit = $probeReceiver.ExitCode }
        } catch { $probeResult.error = "$($probeResult.error) Receiver cleanup: $($_.Exception.Message)" }
        finally { $probeReceiver.Dispose() }
    }
    if ($probeApp) {
        $probeResult.appStillRunning = -not $probeApp.HasExited
        if ($probeApp.HasExited) { $probeResult.appExit = $probeApp.ExitCode }
        $probeApp.Dispose() # Releases our handle only. Never kills the app.
    }
    $probeResult.completedNormally = (-not $probeResult.error -and -not $probeResult.receiverForced -and -not $probeResult.appStillRunning -and $probeResult.appExit -eq 0 -and ($Output -eq 'fullscreen' -or $ReceiverMode -eq 'none' -or $probeResult.receiverExit -eq 0))
    $probeResult['endedUtc'] = [DateTime]::UtcNow.ToString('o')
    try {
        Write-ProbeJson (Join-Path $probeRun 'receiver-samples.json') @($probeReceiverSamples.ToArray())
        Write-ProbeJson (Join-Path $probeRun 'runner-result.json') $probeResult
    } finally {
        if ($probeRunnerMutex) {
            if ($probeRunnerMutexOwned) { $probeRunnerMutex.ReleaseMutex() }
            $probeRunnerMutex.Dispose()
        }
    }
}
$probeResult | ConvertTo-Json -Depth 6
if (-not $probeResult.completedNormally) { throw "Trial did not complete normally. See $probeRun. No later trial was started." }
