# Optional visual-validation helper. Never called by the performance runner.
[CmdletBinding(DefaultParameterSetName='Capture')]
param(
    [Parameter(Mandatory)][string]$OwnedRecord,
    [Parameter(Mandatory,ParameterSetName='Capture')][string]$EvidenceRoot,
    [Parameter(ParameterSetName='Capture')][ValidateRange(1,2)][int]$Samples = 2,
    [Parameter(ParameterSetName='Capture')][ValidateRange(100,5000)][int]$IntervalMilliseconds = 500,
    [Parameter(ParameterSetName='Capture')][ValidateRange(2,15)][int]$TimeoutSeconds = 8,
    [Parameter(ParameterSetName='Capture')][switch]$ShowOwnedWindow,
    [Parameter(Mandatory,ParameterSetName='Worker')][switch]$CaptureWorker,
    [Parameter(Mandatory,ParameterSetName='Worker')][string]$SampleDirectory,
    [Parameter(Mandatory,ParameterSetName='Worker')][long]$WindowHandle
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) { throw 'Windows is required.' }
$evidenceRecordPath = (Resolve-Path -LiteralPath $OwnedRecord).ProviderPath
$evidenceIdentity = Get-Content -LiteralPath $evidenceRecordPath -Raw | ConvertFrom-Json
if ([IO.Path]::GetFileName([string]$evidenceIdentity.exe) -ine 'WinSpoutDXreceiver.exe') { throw 'The owned record is not for official WinSpoutDXreceiver.exe.' }
function Write-EvidenceJson([string]$Path, $Value) {
    $evidenceStream = [IO.File]::Open($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $evidenceWriter = [IO.StreamWriter]::new($evidenceStream,[Text.UTF8Encoding]::new($false))
        try { $evidenceWriter.Write((ConvertTo-Json -InputObject $Value -Depth 10)) }
        finally { $evidenceWriter.Dispose() }
    } finally { $evidenceStream.Dispose() }
}
function Assert-EvidenceOwner {
    $evidenceProcess = Get-Process -Id ([int]$evidenceIdentity.pid) -ErrorAction Stop
    try {
        if ($evidenceProcess.HasExited -or $evidenceProcess.StartTime.ToUniversalTime().Ticks -ne [long]$evidenceIdentity.startTimeUtcTicks -or
            -not [string]::Equals($evidenceProcess.Path, [string]$evidenceIdentity.exe, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Receiver PID, creation time or executable path no longer matches the owned record.'
        }
    } finally { $evidenceProcess.Dispose() }
}
if (-not ('GpuProbeEvidenceNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class GpuProbeEvidenceNative {
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int command);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder title, int capacity);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int left, top, right, bottom; }
    public sealed class Window { public long hwnd; public string title; public Rect bounds; public bool visible, iconic, zoomed; }
    public static bool IsOwner(IntPtr hwnd, uint pid) { uint actual; GetWindowThreadProcessId(hwnd, out actual); return actual == pid; }
    public static Window[] Windows(uint pid) {
        var result = new List<Window>();
        EnumWindows((hwnd, param) => {
            if (IsOwner(hwnd, pid)) {
                Rect rect; GetWindowRect(hwnd, out rect);
                var title = new StringBuilder(1024); GetWindowText(hwnd, title, title.Capacity);
                if (rect.right > rect.left && rect.bottom > rect.top && title.Length > 0)
                    result.Add(new Window { hwnd=hwnd.ToInt64(), title=title.ToString(), bounds=rect, visible=IsWindowVisible(hwnd), iconic=IsIconic(hwnd), zoomed=IsZoomed(hwnd) });
            }
            return true;
        }, IntPtr.Zero);
        return result.ToArray();
    }
}
'@
}
Assert-EvidenceOwner
if ($CaptureWorker) {
    # Only this disposable helper can block in PrintWindow. The receiver and probe
    # are never killed by this script, even if capture times out.
    $evidenceSamplePath = (Resolve-Path -LiteralPath $SampleDirectory).ProviderPath
    $evidenceMeta = [ordered]@{schemaVersion=1;pid=$evidenceIdentity.pid;startTimeUtcTicks=$evidenceIdentity.startTimeUtcTicks;hwnd=$WindowHandle;qpcFrequency=[Diagnostics.Stopwatch]::Frequency;captureStartQpc=$null;captureEndQpc=$null;printWindowSucceeded=$false;pixelContentVerified=$false;sampledUniform=$null;sampledAllBlack=$null;sampledColorCount=0;png=$null;error=$null}
    try {
        Add-Type -AssemblyName System.Drawing
        $evidenceHwnd = [IntPtr]::new($WindowHandle)
        if (-not [GpuProbeEvidenceNative]::IsOwner($evidenceHwnd,[uint32]$evidenceIdentity.pid)) { throw 'Window is no longer owned by the recorded receiver.' }
        $evidenceRect = [GpuProbeEvidenceNative+Rect]::new()
        if (-not [GpuProbeEvidenceNative]::GetClientRect($evidenceHwnd,[ref]$evidenceRect)) { throw 'GetClientRect failed.' }
        $evidenceWidth = $evidenceRect.right-$evidenceRect.left
        $evidenceHeight = $evidenceRect.bottom-$evidenceRect.top
        if ($evidenceWidth -lt 1 -or $evidenceHeight -lt 1 -or $evidenceWidth -gt 8192 -or $evidenceHeight -gt 8192) { throw 'Invalid or excessive client dimensions.' }
        $evidenceMeta['width'] = $evidenceWidth
        $evidenceMeta['height'] = $evidenceHeight
        $evidenceBitmap = [Drawing.Bitmap]::new($evidenceWidth,$evidenceHeight,[Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $evidenceGraphics = [Drawing.Graphics]::FromImage($evidenceBitmap)
            try {
                $evidenceGraphics.Clear([Drawing.Color]::Black)
                $evidenceDc = $evidenceGraphics.GetHdc()
                try {
                    Assert-EvidenceOwner
                    if (-not [GpuProbeEvidenceNative]::IsOwner($evidenceHwnd,[uint32]$evidenceIdentity.pid)) { throw 'Window ownership changed before capture.' }
                    $evidenceMeta.captureStartQpc = [Diagnostics.Stopwatch]::GetTimestamp()
                    $evidenceMeta.printWindowSucceeded = [GpuProbeEvidenceNative]::PrintWindow($evidenceHwnd,$evidenceDc,1) # PW_CLIENTONLY
                    $evidenceMeta.captureEndQpc = [Diagnostics.Stopwatch]::GetTimestamp()
                } finally { $evidenceGraphics.ReleaseHdc($evidenceDc) }
            } finally { $evidenceGraphics.Dispose() }
            $evidenceColors = [Collections.Generic.HashSet[int]]::new()
            $evidenceAllBlack = $true
            for ($evidenceY=0; $evidenceY -lt 18; $evidenceY++) {
                for ($evidenceX=0; $evidenceX -lt 32; $evidenceX++) {
                    $evidenceColor = $evidenceBitmap.GetPixel([Math]::Min($evidenceWidth-1,[int](($evidenceX+0.5)*$evidenceWidth/32)),[Math]::Min($evidenceHeight-1,[int](($evidenceY+0.5)*$evidenceHeight/18)))
                    $null=$evidenceColors.Add($evidenceColor.ToArgb())
                    if ($evidenceColor.R -ne 0 -or $evidenceColor.G -ne 0 -or $evidenceColor.B -ne 0) { $evidenceAllBlack=$false }
                }
            }
            $evidenceMeta.sampledColorCount=$evidenceColors.Count
            $evidenceMeta.sampledUniform=$evidenceColors.Count -le 1
            $evidenceMeta.sampledAllBlack=$evidenceAllBlack
            $evidencePng=Join-Path $evidenceSamplePath 'receiver-client.png'
            $evidenceStream=[IO.File]::Open($evidencePng,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
            try { $evidenceBitmap.Save($evidenceStream,[Drawing.Imaging.ImageFormat]::Png) } finally { $evidenceStream.Dispose() }
            $evidenceMeta.png=$evidencePng
            $evidenceMeta['pngSha256']=(Get-FileHash -LiteralPath $evidencePng -Algorithm SHA256).Hash
            if (-not $evidenceMeta.printWindowSucceeded) { $evidenceMeta.error='PrintWindow returned false. Saved pixels are not valid receiver evidence.' }
            elseif ($evidenceMeta.sampledUniform) { $evidenceMeta.error='Sample grid is uniform; blank/stale capture is possible. Visual inspection required.' }
        } finally { $evidenceBitmap.Dispose() }
    } catch { $evidenceMeta.error=$_.Exception.Message }
    Write-EvidenceJson (Join-Path $evidenceSamplePath 'capture.json') $evidenceMeta
    if ($evidenceMeta.error) { exit 2 }
    exit 0
}

$evidenceRootPath=[IO.Path]::GetFullPath($EvidenceRoot)
$evidenceRun=Join-Path $evidenceRootPath ('receiver-evidence-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceRun | Out-Null
$evidenceWindows=@([GpuProbeEvidenceNative]::Windows([uint32]$evidenceIdentity.pid))
if ($evidenceWindows.Count -ne 1) { throw "Expected exactly one owned receiver top-level window; found $($evidenceWindows.Count). No window was changed." }
$evidenceWindow=$evidenceWindows[0]
$evidenceHwnd=[IntPtr]::new($evidenceWindow.hwnd)
$evidenceResult=[ordered]@{schemaVersion=1;ownedRecord=$evidenceRecordPath;identity=$evidenceIdentity;windowBefore=$evidenceWindow;showRequested=[bool]$ShowOwnedWindow;showPosted=$false;restorePosted=$false;qpcFrequency=[Diagnostics.Stopwatch]::Frequency;startedQpc=[Diagnostics.Stopwatch]::GetTimestamp();samples=@();error=$null;limitations='Optional visual evidence only; PrintWindow success does not prove new image receipt, image ID, physical display or rate. Uniform/black capture may mean capture failed.'}
$evidenceWorker=$null
$evidenceWorkerIdentity=$null
try {
    if ($ShowOwnedWindow -and (-not $evidenceWindow.visible -or $evidenceWindow.iconic)) {
        Assert-EvidenceOwner
        if (-not [GpuProbeEvidenceNative]::IsOwner($evidenceHwnd,[uint32]$evidenceIdentity.pid)) { throw 'Window ownership changed.' }
        $evidenceShow=if ($evidenceWindow.iconic) {4} else {8}
        $evidenceResult.showPosted=[GpuProbeEvidenceNative]::ShowWindowAsync($evidenceHwnd,$evidenceShow) # Show without activation.
        Start-Sleep -Milliseconds 250
    }
    $evidenceShell=(Get-Process -Id $PID).Path
    for ($evidenceIndex=1;$evidenceIndex -le $Samples;$evidenceIndex++) {
        Assert-EvidenceOwner
        $evidenceSample=Join-Path $evidenceRun ('sample-{0:D2}' -f $evidenceIndex)
        New-Item -ItemType Directory -Path $evidenceSample | Out-Null
        $evidenceArguments=@('-NoProfile','-NonInteractive','-File',$PSCommandPath,'-OwnedRecord',$evidenceRecordPath,'-CaptureWorker','-SampleDirectory',$evidenceSample,'-WindowHandle',"$($evidenceWindow.hwnd)")
        $evidenceQuoted=@($evidenceArguments | ForEach-Object { if ($_ -match '[\r\n\x00"]') {throw 'Unsupported argument character.'}; '"'+($_ -replace '(\\+)$','$1$1')+'"' })
        # All inputs/outputs are absolute. Keep the worker cwd at the script folder;
        # deep evidence paths need not become a Win32 process working directory.
        $evidenceWorker=Start-Process -FilePath $evidenceShell -ArgumentList $evidenceQuoted -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $evidenceSample 'worker-stdout.txt') -RedirectStandardError (Join-Path $evidenceSample 'worker-stderr.txt')
        $null=$evidenceWorker.Handle
        $evidenceWorkerIdentity=[ordered]@{pid=$evidenceWorker.Id;startTimeUtcTicks=$evidenceWorker.StartTime.ToUniversalTime().Ticks;exe=$evidenceShell}
        Write-EvidenceJson (Join-Path $evidenceSample 'worker-owned.json') $evidenceWorkerIdentity
        $evidenceCompleted=$evidenceWorker.WaitForExit($TimeoutSeconds*1000)
        $evidenceSampleResult=[ordered]@{directory=$evidenceSample;worker=$evidenceWorkerIdentity;timedOut=(-not $evidenceCompleted);exitCode=$null;observedQpc=[Diagnostics.Stopwatch]::GetTimestamp()}
        if ($evidenceCompleted) { $evidenceSampleResult.exitCode=$evidenceWorker.ExitCode }
        $evidenceResult.samples+=,$evidenceSampleResult
        if (-not $evidenceCompleted) { throw 'PrintWindow helper exceeded timeout. Only the owned capture helper will be terminated; receiver and probe are left running.' }
        $evidenceWorker.Dispose();$evidenceWorker=$null
        if ($evidenceSampleResult.exitCode -ne 0) { throw "Capture failed or may be blank. Inspect $evidenceSample; no further sample attempted." }
        if ($evidenceIndex -lt $Samples) { Start-Sleep -Milliseconds $IntervalMilliseconds }
    }
} catch { $evidenceResult.error=$_.Exception.Message }
finally {
    if ($evidenceWorker) {
        try {
            if (-not $evidenceWorker.HasExited -and $evidenceWorkerIdentity) {
                $evidenceCheck=Get-Process -Id $evidenceWorkerIdentity.pid -ErrorAction SilentlyContinue
                if ($evidenceCheck) {
                    try {
                        if ($evidenceCheck.StartTime.ToUniversalTime().Ticks -eq $evidenceWorkerIdentity.startTimeUtcTicks -and [string]::Equals($evidenceCheck.Path,$evidenceWorkerIdentity.exe,[StringComparison]::OrdinalIgnoreCase)) {
                            $evidenceWorker.Kill();$null=$evidenceWorker.WaitForExit(2000)
                        }
                    } finally { $evidenceCheck.Dispose() }
                }
            }
        } catch { $evidenceResult.error="$($evidenceResult.error) Helper cleanup: $($_.Exception.Message)" }
        finally { $evidenceWorker.Dispose() }
    }
    if ($evidenceResult.showPosted) {
        try {
            Assert-EvidenceOwner
            if ([GpuProbeEvidenceNative]::IsOwner($evidenceHwnd,[uint32]$evidenceIdentity.pid)) {
                $evidenceRestore=if (-not $evidenceWindow.visible) {0} elseif ($evidenceWindow.iconic) {7} elseif ($evidenceWindow.zoomed) {3} else {4}
                $evidenceResult.restorePosted=[GpuProbeEvidenceNative]::ShowWindowAsync($evidenceHwnd,$evidenceRestore)
            }
        } catch { $evidenceResult.error="$($evidenceResult.error) Receiver restore: $($_.Exception.Message)" }
    }
    $evidenceResult['endedQpc']=[Diagnostics.Stopwatch]::GetTimestamp()
    Write-EvidenceJson (Join-Path $evidenceRun 'evidence-result.json') $evidenceResult
}
$evidenceResult | ConvertTo-Json -Depth 10
if ($evidenceResult.error) { throw "Evidence capture incomplete; inspect $evidenceRun." }
