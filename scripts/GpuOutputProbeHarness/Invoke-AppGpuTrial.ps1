# Parent-run trial of the main app with OutputBackend=Gpu. One explicitly requested run; serial; owned processes only.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$MediaPath,
    [Parameter(Mandatory)][string]$Label,
    [int]$Seconds = 32,
    [string]$DisplayDeviceName = '\\.\DISPLAY2',
    # Empty defaults resolve to this repository (see below). The old absolute paths
    # pointed at worktrees deleted in the 2026-09-16 cleanup.
    [string]$AppExe = '',
    [string]$LogRoot = '',
    [switch]$NoFullscreen,
    [int]$KillReceiverAfterSeconds = 0,
    [ValidateSet('Mpv','Gstreamer')][string]$PlayerBackend = 'Mpv',
    [switch]$NoSpout,
    [string]$ProjectPath = '',
    [int]$ScreenshotAtSeconds = 0,
    [int]$TestCardOnAtSeconds = 0,
    [int]$TestCardOffAtSeconds = 0,
    [switch]$ClickPlay,
    [ValidateSet('None','Normal','Force')][string]$ExitDialog = 'Normal',
    [string]$SimulateDeviceLoss = '',
    [int]$GpuRetryAtSeconds = 0,
    # V11: decodeMode を settings.json へ入れる（空 = 既存の settings 生成のまま = hardware 既定）。
    [ValidateSet('', 'hardware', 'software')][string]$DecodeMode = '',
    # V2 (audio). MuteAtSeconds/SpeedAtSeconds take a comma list of seconds; both
    # controls are toggles, so "10,20" mutes at 10 s and unmutes at 20 s.
    # VolumeAtSeconds takes "seconds:value" pairs, e.g. "12:50,20:100" (0..100).
    [string]$MuteAtSeconds = '',
    [string]$VolumeAtSeconds = '',
    [string]$SpeedAtSeconds = '',
    # Record the default render endpoint with WASAPI loopback for the whole run
    # and write audio-rms.csv / audio-probe.txt into the run directory.
    # V5/V6: build a playlist (--open MediaPath --playlist p1 p2 ...) and drive it.
    # NextTrackAtSeconds / PrevTrackAtSeconds take a comma list of seconds.
    # SeekAtSeconds takes "seconds:position" pairs where position is the SeekBar value.
    # Semicolon separated, NOT an array: array parameters do not survive
    # "powershell -File" invocation (same trap as Run-V1Matrix's -Only).
    [string]$PlaylistPaths = '',
    [string]$NextTrackAtSeconds = '',
    [string]$PrevTrackAtSeconds = '',
    [string]$SeekAtSeconds = '',
    # V10: a project without Canvas opens CanvasSelectDialog before the main window is usable.
    [ValidateSet('', 'Ok', 'Cancel')][string]$CanvasDialog = '',
    [switch]$AudioProbe,
    [string]$AudioProbeExe = (Join-Path $PSScriptRoot '..\AudioLoopbackProbe\bin\Debug\net8.0-windows\AudioLoopbackProbe.exe')
)
$ErrorActionPreference = 'Stop'

# Repo-relative defaults: any worktree runs its own build and writes its own TestResults.
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $AppExe) { $AppExe = Join-Path $repoRoot 'src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe' }
if (-not $LogRoot) { $LogRoot = Join-Path $repoRoot 'TestResults\gpu-app' }
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('AppTrialNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class AppTrialNative {
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
    public static long Qpc() { long v; QueryPerformanceCounter(out v); return v; }
    public static int CloseOwned(uint pid) {
        int count = 0;
        EnumWindows((hwnd, param) => { uint actual; GetWindowThreadProcessId(hwnd, out actual); if (actual == pid && PostMessage(hwnd, 0x10, IntPtr.Zero, IntPtr.Zero)) count++; return true; }, IntPtr.Zero);
        return count;
    }
}
'@
}
# V11: アプリの stdout / stderr を QPC タイムスタンプ付きで保存する。
# shim の LOG（decodeMode のフォールバック警告など）は stderr にしか出ないため。
if (-not ('AppOutputCapture' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
public static class AppOutputCapture {
    private static readonly object Gate = new object();
    private static StreamWriter OutputWriter;
    private static StreamWriter ErrorWriter;
    [DllImport("kernel32.dll")] private static extern bool QueryPerformanceCounter(out long value);
    private static long Qpc() { long v; QueryPerformanceCounter(out v); return v; }
    public static void Attach(Process process, string outputPath, string errorPath) {
        OutputWriter = new StreamWriter(outputPath, false);
        OutputWriter.AutoFlush = true;
        ErrorWriter = new StreamWriter(errorPath, false);
        ErrorWriter.AutoFlush = true;
        process.OutputDataReceived += (s, e) => Write(OutputWriter, "out", e.Data);
        process.ErrorDataReceived  += (s, e) => Write(ErrorWriter, "err", e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
    private static void Write(StreamWriter writer, string stream, string line) {
        if (line == null) { return; }
        string row = Qpc().ToString() + "\t" + stream + "\t" + line;
        lock (Gate) { writer.WriteLine(row); }
    }
}
'@
}
$receiverExe = Join-Path $env:USERPROFILE 'Downloads\Spout-SDK-examples_2-007-017\Spout-SDK-examples\Examples_2-007-017\SpoutDX\WinSpoutDXreceiver.exe'
$session = (query session 2>$null | Select-String '>console') -ne $null
if (-not $session) { throw 'Not a console session; refusing to run a display test.' }
$busy = Get-Process | Where-Object { $_.ProcessName -match '^(TimecodeSyncPlayer|GpuOutputProbe|WinSpoutDXreceiver|gst-launch-1.0|tcs-shim-test)$' }
if ($busy) { throw "GPU test process already running: $($busy.ProcessName -join ',')" }
$run = Join-Path $LogRoot ('{0}-{1}' -f [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'), $Label)
New-Item -ItemType Directory -Path $run | Out-Null
$trace = Join-Path $run 'app'
$settings = Join-Path $run 'settings.json'
$sender = 'TCSParent-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$escapedDevice = $DisplayDeviceName.Replace('\', '\\')  # JSON: one backslash -> two
$backendValue = if ($PlayerBackend -eq 'Gstreamer') { 1 } else { 0 }  # PlayerBackend enum: Mpv=0, Gstreamer=1
$json = '{"outputBackend":1,"backend":' + $backendValue + ',"fullscreenDisplayDeviceName":"' + $escapedDevice + '"'
if ($DecodeMode) { $json += ',"decodeMode":"' + $DecodeMode + '"' }
$json += '}'
[IO.File]::WriteAllText($settings, $json, [Text.UTF8Encoding]::new($false))
$hashes = @($AppExe, $ProjectPath, (Join-Path (Split-Path $AppExe) 'TimecodeSyncPlayer.dll'), (Join-Path (Split-Path $AppExe) 'libmpv-2.dll'), (Join-Path (Split-Path $AppExe) 'SpoutDX.dll'), (Join-Path (Split-Path $AppExe) 'tcs_gstreamer.dll'), $receiverExe, $MediaPath) |
    Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { Get-FileHash $_ -Algorithm SHA256 | Select-Object Path, Hash }
$hashes | ConvertTo-Json | Set-Content (Join-Path $run 'inputs.json') -Encoding UTF8
$result = [ordered]@{ receiverKilledDeliberately=$false; label=$Label; playerBackend=$PlayerBackend; spout=(-not $NoSpout); project=$ProjectPath; exitDialog=$ExitDialog; simulateDeviceLoss=$SimulateDeviceLoss; decodeMode=$(if ($DecodeMode) { $DecodeMode } else { $null }); media=$MediaPath; seconds=$Seconds; sender=$sender; display=$DisplayDeviceName; startedUtc=[DateTime]::UtcNow.ToString('o'); app=$null; receiver=$null; appExit=$null; receiverExit=$null; receiverForced=$false; error=$null; cpuSeconds=$null; audioProbe=$null; completedNormally=$false; receiverMode=$(if ($NoSpout) { 'none' } else { 'official' }); steps=@() }
$app = $null; $recv = $null
function Find-Button([int]$processId, [string]$automationId, [int]$timeoutSec) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $processId)
        # The process owns several top-level windows once fullscreen is open; search each for the button.
        $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($win in $wins) {
            $bc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $automationId)
            $btn = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $bc)
            if ($btn -and $btn.Current.IsEnabled) { return @{ Window=$win; Button=$btn } }
        }
        Start-Sleep -Milliseconds 300
    }
    throw "UI element not found or disabled: $automationId"
}
function Invoke-Button($found) { ($found.Button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke() }
# Stamp UI actions with the same QPC clock events.jsonl uses, so a step can be located
# in the trace exactly instead of guessing from wall-clock text.
$script:actionStamps = @()
function Add-ActionStamp([string]$name) {
    $script:actionStamps += [ordered]@{ action = $name; qpc = [AppTrialNative]::Qpc(); at = (Get-Date).ToString('HH:mm:ss.fff') }
}
# analyze_probe.py refuses a run unless the owned receiver is observed alive across the
# whole analysis window, so record QPC-stamped liveness samples next to the trace.
$script:receiverSamples = @()
function Add-ReceiverSample($proc, $startedUtc) {
    if (-not $proc) { return $null }
    $qpc = [AppTrialNative]::Qpc()
    $live = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
    $alive = [bool]($live -and $live.StartTime.ToUniversalTime().ToString('o') -eq $startedUtc)
    $script:receiverSamples += [ordered]@{ qpc = $qpc; alive = $alive }
    return $qpc
}
# VolumeSlider is a Slider, not a Button; Find-Button locates any element by AutomationId.
function Set-Slider($found, [double]$value) {
    ($found.Button.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)).SetValue($value)
}
$audio = $null
try {
    if ($AudioProbe) {
        if (-not (Test-Path $AudioProbeExe)) { throw "AudioLoopbackProbe not built: $AudioProbeExe" }
        $audioCsv = Join-Path $run 'audio-rms.csv'
        $audioTxt = Join-Path $run 'audio-probe.txt'
        # Cover startup, the measured window and shutdown.
        $audio = Start-Process -FilePath $AudioProbeExe -ArgumentList @([string]($Seconds + 40), ('"' + $audioCsv + '"')) -PassThru -NoNewWindow -RedirectStandardOutput $audioTxt
        $result.steps += "AudioLoopbackProbe started at $((Get-Date).ToString('HH:mm:ss.fff'))"
    }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $AppExe
    if ($ProjectPath) {
        $psi.Arguments = '--load-project "' + $ProjectPath + '"'
    } elseif ($PlaylistPaths) {
        $list = @($PlaylistPaths -split ';' | Where-Object { $_.Trim() } | ForEach-Object { '"' + $_.Trim() + '"' })
        $psi.Arguments = '--open "' + $MediaPath + '" --playlist ' + ($list -join ' ')
    } else {
        $psi.Arguments = '--open "' + $MediaPath + '"'
    }
    $psi.WorkingDirectory = Split-Path $AppExe; $psi.UseShellExecute = $false
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
    $psi.Environment['TIMECODE_SYNC_PLAYER_SETTINGS_PATH'] = $settings
    $psi.Environment['TIMECODE_SYNC_PLAYER_SPOUT_NAME'] = $sender
    $psi.Environment['TIMECODE_SYNC_PLAYER_OUTPUT_TRACE'] = $trace
    if ($SimulateDeviceLoss) { $psi.Environment['TIMECODE_SYNC_PLAYER_SIMULATE_DEVICE_LOSS'] = $SimulateDeviceLoss }
$app = [System.Diagnostics.Process]::Start($psi)
$null = $app.Handle
[AppOutputCapture]::Attach($app, (Join-Path $run 'app-stdout.txt'), (Join-Path $run 'app-stderr.txt'))
    $result.app = [ordered]@{ pid=$app.Id; startUtc=$app.StartTime.ToUniversalTime().ToString('o'); exe=$AppExe }
    $t0 = Get-Date
    if ($CanvasDialog) {
        # The dialog is modal and blocks the main window, so answer it first.
        $dlgId = if ($CanvasDialog -eq 'Ok') { 'CanvasDialogOk' } else { 'CanvasDialogCancel' }
        $dlg = Find-Button $app.Id $dlgId 40
        Invoke-Button $dlg
        $result.steps += "$dlgId invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
    }
    $spout = Find-Button $app.Id 'BtnSpout' 40
    $result.steps += "window+spout button ready after $([int]((Get-Date)-$t0).TotalMilliseconds) ms"
    Start-Sleep -Seconds 2
    if ($NoSpout) {
        $result.steps += "Spout left OFF (-NoSpout); no receiver started"
        Start-Sleep -Seconds 3
    } else {
        Invoke-Button $spout; $result.steps += "BtnSpout invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
        Start-Sleep -Seconds 1
        $recv = Start-Process -FilePath $receiverExe -WorkingDirectory $run -WindowStyle Hidden -PassThru
        $null = $recv.Handle
        $result.receiver = [ordered]@{ pid=$recv.Id; startUtc=$recv.StartTime.ToUniversalTime().ToString('o') }
        Start-Sleep -Seconds 2
        $result.receiver.observedAliveQpc = Add-ReceiverSample $recv $result.receiver.startUtc
    }
    if (-not $NoFullscreen) {
        $fs = Find-Button $app.Id 'BtnFullscreen' 10
        Invoke-Button $fs; $result.steps += "BtnFullscreen invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
    }
    if ($KillReceiverAfterSeconds -gt 0 -and $KillReceiverAfterSeconds -lt $Seconds) {
        Start-Sleep -Seconds $KillReceiverAfterSeconds
        # Deliberate receiver crash (owned PID + start time verified) to exercise the abandoned-mutex path.
        $check = Get-Process -Id $recv.Id -ErrorAction SilentlyContinue
        if ($check -and $check.StartTime.ToUniversalTime().ToString('o') -eq $result.receiver.startUtc) { $recv.Kill(); $recv.WaitForExit(5000) | Out-Null }
        $result.steps += "receiver killed deliberately at $((Get-Date).ToString('HH:mm:ss.fff'))"
        $result.receiverKilledDeliberately = $true
        Start-Sleep -Seconds ($Seconds - $KillReceiverAfterSeconds)
    } else {
        if ($ClickPlay) { $play = Find-Button $app.Id 'BtnPlay' 10; Invoke-Button $play; $result.steps += "BtnPlay invoked at $((Get-Date).ToString('HH:mm:ss.fff'))" }
        $elapsed = 0
        $marks = @()
        if ($ScreenshotAtSeconds -gt 0) { $marks += @{ at=$ScreenshotAtSeconds; kind='shot' } }
        if ($TestCardOnAtSeconds -gt 0) { $marks += @{ at=$TestCardOnAtSeconds; kind='cardOn' } }
        if ($TestCardOffAtSeconds -gt 0) { $marks += @{ at=$TestCardOffAtSeconds; kind='cardOff' } }
        if ($GpuRetryAtSeconds -gt 0) { $marks += @{ at=$GpuRetryAtSeconds; kind='gpuRetry' } }
        foreach ($tok in ($MuteAtSeconds -split ',')) { if ($tok.Trim()) { $marks += @{ at=[int]$tok.Trim(); kind='mute' } } }
        foreach ($tok in ($NextTrackAtSeconds -split ',')) { if ($tok.Trim()) { $marks += @{ at=[int]$tok.Trim(); kind='nextTrack' } } }
        foreach ($tok in ($PrevTrackAtSeconds -split ',')) { if ($tok.Trim()) { $marks += @{ at=[int]$tok.Trim(); kind='prevTrack' } } }
        foreach ($tok in ($SeekAtSeconds -split ',')) {
            if ($tok.Trim()) {
                $pair = $tok.Trim() -split ':'
                if ($pair.Count -ne 2) { throw "SeekAtSeconds wants 'seconds:position' pairs, got '$tok'" }
                $marks += @{ at=[int]$pair[0]; kind='seek'; value=[double]$pair[1] }
            }
        }
        foreach ($tok in ($SpeedAtSeconds -split ',')) { if ($tok.Trim()) { $marks += @{ at=[int]$tok.Trim(); kind='speed' } } }
        foreach ($tok in ($VolumeAtSeconds -split ',')) {
            if ($tok.Trim()) {
                $pair = $tok.Trim() -split ':'
                if ($pair.Count -ne 2) { throw "VolumeAtSeconds wants 'seconds:value' pairs, got '$tok'" }
                $marks += @{ at=[int]$pair[0]; kind='volume'; value=[double]$pair[1] }
            }
        }
        foreach ($m in ($marks | Sort-Object { $_.at })) {
            if ($m.at -le $elapsed -or $m.at -ge $Seconds) { continue }
            Start-Sleep -Seconds ($m.at - $elapsed); $elapsed = $m.at
            if ($recv) { [void](Add-ReceiverSample $recv $result.receiver.startUtc) }
            if ($m.kind -eq 'shot') {
                Add-Type -AssemblyName System.Windows.Forms, System.Drawing
                $scr = [System.Windows.Forms.Screen]::AllScreens | Where-Object { $_.DeviceName -eq $DisplayDeviceName } | Select-Object -First 1
                if ($scr) {
                    $bmp = New-Object System.Drawing.Bitmap $scr.Bounds.Width, $scr.Bounds.Height
                    $g = [System.Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($scr.Bounds.Location, [System.Drawing.Point]::Empty, $scr.Bounds.Size); $g.Dispose()
                    $bmp.Save((Join-Path $run ('display-{0}s.png' -f $m.at)), [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
                    $result.steps += "screenshot of $DisplayDeviceName at $((Get-Date).ToString('HH:mm:ss.fff'))"
                } else { $result.steps += "screenshot skipped: display not found" }
            } elseif ($m.kind -eq 'gpuRetry') {
                try { $retry = Find-Button $app.Id 'BtnGpuRetry' 10; Invoke-Button $retry; $result.steps += "BtnGpuRetry invoked at $((Get-Date).ToString('HH:mm:ss.fff'))" }
                catch { $result.steps += "BtnGpuRetry not available: $($_.Exception.Message)" }
            } elseif ($m.kind -eq 'mute') {
                $mute = Find-Button $app.Id 'BtnMute' 10; Invoke-Button $mute
                $result.steps += "BtnMute invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'nextTrack') {
                $nt = Find-Button $app.Id 'BtnNextTrack' 10; Invoke-Button $nt
                Add-ActionStamp 'BtnNextTrack'
                $result.steps += "BtnNextTrack invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'prevTrack') {
                $pt = Find-Button $app.Id 'BtnPreviousTrack' 10; Invoke-Button $pt
                Add-ActionStamp 'BtnPreviousTrack'
                $result.steps += "BtnPreviousTrack invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'seek') {
                $sb = Find-Button $app.Id 'SeekBar' 10; Set-Slider $sb $m.value
                $result.steps += "SeekBar set to $($m.value) at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'speed') {
                $spd = Find-Button $app.Id 'BtnSpeed' 10; Invoke-Button $spd
                $result.steps += "BtnSpeed invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'volume') {
                $vol = Find-Button $app.Id 'VolumeSlider' 10; Set-Slider $vol $m.value
                $result.steps += "VolumeSlider set to $($m.value) at $((Get-Date).ToString('HH:mm:ss.fff'))"
            } elseif ($m.kind -eq 'cardOn' -or $m.kind -eq 'cardOff') {
                $card = Find-Button $app.Id 'BtnTestCard' 10; Invoke-Button $card; $result.steps += "BtnTestCard ($($m.kind)) invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
            }
        }
        Start-Sleep -Seconds ($Seconds - $elapsed)
    }
    if ($recv) { [void](Add-ReceiverSample $recv $result.receiver.startUtc) }
    if ($app.HasExited) { throw "App exited early with code $($app.ExitCode)" }
    if (-not $NoFullscreen) {
        $fs2 = Find-Button $app.Id 'BtnFullscreen' 10
        Invoke-Button $fs2; $result.steps += "BtnFullscreen (exit) invoked at $((Get-Date).ToString('HH:mm:ss.fff'))"
        Start-Sleep -Seconds 2
    }
    $result.cpuSeconds = [math]::Round($app.TotalProcessorTime.TotalSeconds, 3)
    $mainFound = Find-Button $app.Id 'BtnSpout' 10
    $hwnd = [IntPtr]$mainFound.Window.Current.NativeWindowHandle
    [void][AppTrialNative]::PostMessage($hwnd, 0x10, [IntPtr]::Zero, [IntPtr]::Zero)
    $result.steps += "WM_CLOSE posted at $((Get-Date).ToString('HH:mm:ss.fff'))"
    if ($ExitDialog -ne 'None') {
        # Stage 5: closing shows ExitDialog (cancel is default). Choose normal or forced exit via UIA.
        $btnId = if ($ExitDialog -eq 'Force') { 'BtnExitForce' } else { 'BtnExitNormal' }
        try { $exitBtn = Find-Button $app.Id $btnId 10; Invoke-Button $exitBtn; $result.steps += "$btnId invoked at $((Get-Date).ToString('HH:mm:ss.fff'))" }
        catch { $result.steps += "exit dialog button not found: $($_.Exception.Message)" }
    }
    if (-not $app.WaitForExit(60000)) { $result.error = 'App did not exit within 60 s after WM_CLOSE (not killed).' }
    else { $result.appExit = $app.ExitCode; $result.steps += "app exited code $($app.ExitCode) at $((Get-Date).ToString('HH:mm:ss.fff'))" }
} catch { $result.error = $_.Exception.Message }
finally {
    if ($recv) {
        try {
            if (-not $recv.HasExited) {
                $check = Get-Process -Id $recv.Id -ErrorAction SilentlyContinue
                if ($check -and $check.StartTime.ToUniversalTime().ToString('o') -eq $result.receiver.startUtc) {
                    [void][AppTrialNative]::CloseOwned([uint32]$recv.Id)
                    if (-not $recv.WaitForExit(5000)) { $result.receiverForced = $true; $recv.Kill(); $recv.WaitForExit(5000) | Out-Null }
                }
            }
            if ($recv.HasExited) { $result.receiverExit = $recv.ExitCode }
        } catch { $result.error = "$($result.error) receiver cleanup: $($_.Exception.Message)" }
    }
    if ($audio) {
        try {
            if (-not $audio.WaitForExit(60000)) { $audio.Kill(); $audio.WaitForExit(5000) | Out-Null }
            $txt = Get-Content (Join-Path $run 'audio-probe.txt') -ErrorAction SilentlyContinue
            if ($txt) { $result.audioProbe = ($txt -join ' | ') }
        } catch { $result.error = "$($result.error) audio probe: $($_.Exception.Message)" }
    }
    # ReceiverMode none must still leave an (empty) array so the analyzer can tell
    # "control run without a receiver" from "metadata missing".
    ConvertTo-Json -InputObject @($script:receiverSamples) -Depth 4 |
        Set-Content (Join-Path $run 'receiver-samples.json') -Encoding UTF8
    ConvertTo-Json -InputObject @($script:actionStamps) -Depth 4 |
        Set-Content (Join-Path $run 'action-stamps.json') -Encoding UTF8
    $result.completedNormally = ($null -eq $result.error) -and ($null -ne $result.appExit)
    $result.endedUtc = [DateTime]::UtcNow.ToString('o')
    $logDir = Join-Path (Split-Path $AppExe) 'logs'
    $latest = Get-ChildItem $logDir -Filter 'timecodesyncplayer-*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($latest) { Get-Content $latest.FullName -Tail 400 | Set-Content (Join-Path $run 'app-log-tail.txt') -Encoding UTF8 }
    $result | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $run 'runner-result.json') -Encoding UTF8
}
Write-Output ("RUN " + $run)
$result | ConvertTo-Json -Depth 6
