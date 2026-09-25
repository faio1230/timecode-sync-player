# Run the LTC E2E scenarios against one app (installed or a local Debug build)
# with a single command, and leave the evidence in one report directory.
#
#   powershell -File scripts\run-ltc-scenarios.ps1 -AppExe <path to TimecodeSyncPlayer.exe>
#   powershell -File scripts\run-ltc-scenarios.ps1 -Filter "FullyQualifiedName~NoSuchTest"   # dry run
#   powershell -File scripts\run-ltc-scenarios.ps1 -MediaDir <real media folder> [-Media M1,M3,M5] [-KeepProject]
#       (-Media picks tracks by symbol: M<n> is the n-th video of the folder in name order;
#        -MediaInOffsetSeconds N starts every track N seconds into its video)
#       (the project .tsp is generated under the report directory, never in the
#        media folder, and is removed after the run unless -KeepProject is set)
#   powershell -File scripts\run-ltc-scenarios.ps1 -FollowSeconds 60 -FollowTracks A,B,C -FollowWindowSeconds 2
#       (L-1 continuous-follow audit: seconds / tracks / window length / settling
#        exclusion via -FollowSettlingSeconds / follow-start gate bound via
#        -FollowStartGateSeconds; the default filter includes L-1. To run only the
#        previous 22 scenarios:
#        -Filter 'FullyQualifiedName~LtcScenarioE2ETests&FullyQualifiedName!~L1_')
#       -SegmentSeconds N raises the per-track used length above the 20 s default;
#       L-1 needs >= 34 s used per track (60 s follow rounds down to used - 4).
#
# Prerequisites: VB-CABLE (CABLE Input / Output active), ffmpeg, .NET SDK, the
# target exe with tcs_gstreamer.dll, and a GStreamer runtime (bundled
# gstreamer\bin next to the exe, GSTREAMER_1_0_ROOT_MSVC_X86_64, Program Files,
# or PATH). D18 makes the bundled runtime work without the environment variable.
#
# Exit codes: 0 = no failures, 1 = test failures, 2 = prerequisite failure.
#
# NOTE: keep this file ASCII-only and BOM-less, like the other scripts in this
# repo. Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page, so
# non-ASCII comments break parsing. Backslash- and control-character traps of
# ".ps1" are checked by an empty run before use.
[CmdletBinding()]
param(
    [string]$AppExe = '',
    [string]$ReportDir = '',
    [int]$Cycles = 0,
    [string]$Filter = '',
    [string]$MediaDir = '',
    [string[]]$Media = @(),
    [double]$MediaInOffsetSeconds = 0,
    # Gap between tracks on the timeline (default 5 s; 0 makes them adjacent).
    [double]$GapSeconds = 5,
    [double]$SegmentSeconds = 0,
    [int]$FollowSeconds = 0,
    [string]$FollowTracks = '',
    [double]$FollowWindowSeconds = 0,
    [double]$FollowSettlingSeconds = 0,
    [double]$FollowStartGateSeconds = 0,
    [switch]$EnableSpout,
    [switch]$EnableExternalSpoutAudit,
    [string]$SpoutReceiverExe = '',
    [int]$SpoutPixelSampleMilliseconds = 33,
    [double]$MaxFrameDeficitSeconds = 0.5,
    [double]$MaxPositionStallSeconds = 0.5,
    [double]$MaxSpoutReceiverGapMilliseconds = 500,
    # UI Automation reads are synchronous and can perturb v0.4.7 at 50 ms.
    [double]$L2ProbeIntervalMilliseconds = 250,
    [double]$MaxLoadMilliseconds = 5000,
    [double]$MaxPrivateGrowthMbPerHour = 0,
    [double]$MaxHandleGrowthPerHour = 0,
    [double]$HealthSampleSeconds = 10,
    [double]$ProductionRehearsalHours = 4,
    [double]$ProductionBreakHours = 2,
    [double]$ProductionShowHours = 4,
    [double]$ProductionPostIdleHours = 2,
    # Fixed LTC rate used by the E2E signal generator and the app selector.
    # 29.97 is excluded until the generator emits DF numbering to match the app.
    [ValidateSet(24, 25, 30)]
    [double]$LtcFps = 25,
    [switch]$KeepProject,
    # Each scenario only runs its preflight checks and then skips (dry run; TCS_PREFLIGHT_ONLY=1).
    [switch]$PreflightOnly,
    # Keep output-trace even for a passed run (default: delete it when failed=0 and invalid=0).
    [switch]$KeepTrace,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testProj = Join-Path $repoRoot 'tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj'

$appExeGiven = -not [string]::IsNullOrWhiteSpace($AppExe)
if (-not $AppExe) {
    $AppExe = Join-Path $repoRoot 'src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe'
}
if (-not (Test-Path -LiteralPath $AppExe)) {
    throw "App exe not found: $AppExe (pass -AppExe)"
}
$AppExe = (Resolve-Path -LiteralPath $AppExe).Path
$appDir = Split-Path $AppExe -Parent

if (-not $ReportDir) {
    $ReportDir = Join-Path $repoRoot ('TestResults\ltc-scenarios\' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ'))
}

# D23-b: the media folder and the report directory must not contain each other.
# The runner deletes files under ReportDir\media; if that path were the media
# folder itself (or ReportDir sat inside the media folder), a real media file
# could be removed or the media folder written to. Checked before anything is
# created, and exits with the prerequisite code.
if ($MediaDir) {
    if (-not (Test-Path -LiteralPath $MediaDir -PathType Container)) {
        Write-Output ('PREREQ-ERROR MediaDir not found: ' + $MediaDir)
        Write-Output 'SUMMARY prereq_failed=1'
        exit 2
    }
    $mediaGuard = (Resolve-Path -LiteralPath $MediaDir).ProviderPath.TrimEnd([char]'\') + '\'
    $reportGuard = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportDir).TrimEnd([char]'\') + '\'
    if ($mediaGuard.StartsWith($reportGuard, [StringComparison]::OrdinalIgnoreCase) -or
        $reportGuard.StartsWith($mediaGuard, [StringComparison]::OrdinalIgnoreCase)) {
        Write-Output 'PREREQ-ERROR MediaDir and ReportDir must not contain each other (pass a separate -ReportDir)'
        Write-Output 'SUMMARY prereq_failed=1'
        exit 2
    }
}

New-Item -ItemType Directory -Force -Path $ReportDir | Out-Null
$ReportDir = (Resolve-Path -LiteralPath $ReportDir).Path

# Raw reports can contain media paths. If the report is inside any Git worktree,
# require Git to ignore the directory before any evidence is written there.
$reportFull = [IO.Path]::GetFullPath($ReportDir)
$previousPreference = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    $reportGitRootText = (& git -C $ReportDir rev-parse --show-toplevel 2>$null | Select-Object -First 1)
    $reportGitRootExit = $LASTEXITCODE
} finally {
    $ErrorActionPreference = $previousPreference
}
if ($reportGitRootExit -eq 0 -and $reportGitRootText) {
    $reportGitRoot = [IO.Path]::GetFullPath([string]$reportGitRootText).TrimEnd([char]'\')
    if ([string]::Equals($reportFull.TrimEnd([char]'\'), $reportGitRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ReportDir must not be a Git worktree root'
    }
    $reportRelative = $reportFull.Substring($reportGitRoot.Length).TrimStart([char]'\')
    & git -C $reportGitRoot check-ignore -q -- $reportRelative
    if ($LASTEXITCODE -ne 0) {
        throw 'ReportDir is inside a Git worktree but is not ignored; use an ignored or non-Git directory'
    }
}

Write-Output "app=$AppExe"
Write-Output "report=$ReportDir"

# ---- hard links (D23) ------------------------------------------------------
# D23(a): Windows PowerShell 5.1 wildcard-expands the -Target of
# New-Item -ItemType HardLink, so media names containing brackets fail.
# Call kernel32 directly and report GetLastError on failure.
if (-not ('Tcs.HardLink' -as [type])) {
    Add-Type -Namespace Tcs -Name HardLink -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ByHandleFileInformation {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation info);
'@
}

# D23-b: number of names (hard links) of a file. A file whose only name is under
# ReportDir\media is not a link to media and must never be deleted.
function Get-HardLinkCount([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $info = New-Object Tcs.HardLink+ByHandleFileInformation
        if (-not [Tcs.HardLink]::GetFileInformationByHandle($stream.SafeFileHandle, [ref]$info)) {
            $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw ('GetFileInformationByHandle failed path=' + $Path + ' win32=' + $code)
        }
        return [int]$info.NumberOfLinks
    } finally {
        $stream.Dispose()
    }
}

function New-HardLink([string]$LinkPath, [string]$TargetPath) {
    if (-not [Tcs.HardLink]::CreateHardLinkW($LinkPath, $TargetPath, [IntPtr]::Zero)) {
        $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
        $message = (New-Object System.ComponentModel.Win32Exception($code)).Message
        throw ('CreateHardLinkW failed link=' + $LinkPath + ' target=' + $TargetPath +
            ' win32=' + $code + ' (' + $message + ')')
    }
}

# D23(b): every link created by this run is tracked so the finally cleanup does
# not depend on the project having been generated. The cleanup also sweeps any
# file left under ReportDir\media by an earlier interrupted run (same method,
# non-recursive, only under ReportDir\media).
$createdHardLinks = @()

function Remove-LinkedMediaArtifacts {
    $mediaRoot = Join-Path $ReportDir 'media'
    if (-not (Test-Path -LiteralPath $mediaRoot)) { return }
    $mediaRootFull = [IO.Path]::GetFullPath($mediaRoot).TrimEnd([char]'\') + '\'
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($link in $createdHardLinks) { $paths.Add([string]$link) }
    foreach ($file in @(Get-ChildItem -LiteralPath $mediaRoot -File -ErrorAction SilentlyContinue)) {
        $paths.Add($file.FullName)
    }
    foreach ($path in $paths) {
        $full = [IO.Path]::GetFullPath($path)
        if (-not $full.StartsWith($mediaRootFull, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        # D23-b: delete only a name that is one of several links to the same data.
        if ((Get-HardLinkCount $full) -lt 2) {
            Write-Output ('cleanup_kept_non_link=' + $full)
            continue
        }
        [IO.File]::Delete($full)
    }
    if (@(Get-ChildItem -LiteralPath $mediaRoot -Force -ErrorAction SilentlyContinue).Count -eq 0) {
        [IO.Directory]::Delete($mediaRoot, $false)
    }
}

# Leftovers from an interrupted earlier run in the same report directory.
Remove-LinkedMediaArtifacts

# ---- prerequisites ---------------------------------------------------------
$problems = @()
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { $problems += 'dotnet is not on PATH' }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) { $problems += 'ffmpeg is not on PATH' }
if ($MaxFrameDeficitSeconds -le 0) { $problems += 'MaxFrameDeficitSeconds must be greater than zero' }
if ($MaxPositionStallSeconds -le 0) { $problems += 'MaxPositionStallSeconds must be greater than zero' }
if ($MaxSpoutReceiverGapMilliseconds -le 0) { $problems += 'MaxSpoutReceiverGapMilliseconds must be greater than zero' }
if ($SpoutPixelSampleMilliseconds -lt 16 -or $SpoutPixelSampleMilliseconds -gt 60000) { $problems += 'SpoutPixelSampleMilliseconds must be between 16 and 60000' }
if ($L2ProbeIntervalMilliseconds -lt 10 -or $L2ProbeIntervalMilliseconds -gt 1000) { $problems += 'L2ProbeIntervalMilliseconds must be between 10 and 1000' }
if ($MaxLoadMilliseconds -le 0) { $problems += 'MaxLoadMilliseconds must be greater than zero' }
if ($HealthSampleSeconds -le 0) { $problems += 'HealthSampleSeconds must be greater than zero' }
if ($MaxPrivateGrowthMbPerHour -lt 0) { $problems += 'MaxPrivateGrowthMbPerHour must be zero or greater' }
if ($MaxHandleGrowthPerHour -lt 0) { $problems += 'MaxHandleGrowthPerHour must be zero or greater' }
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'tcs_gstreamer.dll'))) {
    $problems += "tcs_gstreamer.dll is missing next to the exe (run build-shim): $appDir"
}
if ($EnableSpout -and -not (Test-Path -LiteralPath (Join-Path $appDir 'SpoutDX.dll'))) {
    $problems += 'SpoutDX.dll is missing next to the exe while EnableSpout is set'
}
if ($EnableExternalSpoutAudit -and -not $EnableSpout) {
    $problems += 'EnableExternalSpoutAudit requires EnableSpout'
}
if ($EnableExternalSpoutAudit) {
    if (-not $SpoutReceiverExe) {
        $SpoutReceiverExe = Join-Path $repoRoot 'scripts\SpoutContinuityProbe\bin\Debug\x64\SpoutContinuityProbe.exe'
    }
    if (-not (Test-Path -LiteralPath $SpoutReceiverExe -PathType Leaf)) {
        $problems += 'Spout continuity probe is missing (run scripts\SpoutContinuityProbe\build.ps1 or pass SpoutReceiverExe)'
    } else {
        $SpoutReceiverExe = (Resolve-Path -LiteralPath $SpoutReceiverExe).Path
    }
}

# D19: use Core Audio (MMDevice) names for the VB-CABLE check; the PnP endpoint
# names are localized on some machines (for example a speaker-friendly name that
# does not contain "CABLE Input").
# MMDevices registry: FriendlyName {a45c254e-df1c-4efd-8020-67d146a850e0},2,
# DeviceState 1 = Active.
function Get-MmDeviceActiveNames([string]$flow) {
    $names = @()
    $root = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\$flow"
    if (-not (Test-Path -LiteralPath $root)) { return $names }
    foreach ($endpoint in @(Get-ChildItem -LiteralPath $root -ErrorAction SilentlyContinue)) {
        $state = (Get-ItemProperty -LiteralPath $endpoint.PSPath -ErrorAction SilentlyContinue).DeviceState
        if ($state -ne 1) { continue }
        $propsPath = Join-Path $endpoint.PSPath 'Properties'
        $props = Get-ItemProperty -LiteralPath $propsPath -ErrorAction SilentlyContinue
        if (-not $props) { continue }
        $friendly = $props.PSObject.Properties['{a45c254e-df1c-4efd-8020-67d146a850e0},2']
        if ($friendly -and $friendly.Value) { $names += [string]$friendly.Value }
    }
    return $names
}

$renderCable = @(Get-MmDeviceActiveNames 'Render' | Where-Object { $_ -match 'CABLE Input' })
$captureCable = @(Get-MmDeviceActiveNames 'Capture' | Where-Object { $_ -match 'CABLE Output' })
if ($renderCable.Count -eq 0 -or $captureCable.Count -eq 0) {
    $problems += 'VB-CABLE endpoints are not active in Core Audio (need Render "CABLE Input" and Capture "CABLE Output")'
}

$pnpCableNames = @()
if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
    $pnpCableNames = @(Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue |
        Where-Object { $_.Status -eq 'OK' -and $_.FriendlyName -match 'VB-Audio|CABLE' } |
        ForEach-Object { $_.FriendlyName })
}
$pnpOrdered = @($pnpCableNames |
    Sort-Object @{Expression = { if ($_ -match 'CABLE') { 0 } else { 1 } }}, @{Expression = { $_ } })
$pnpText = (($pnpOrdered | Select-Object -First 8) -join '; ')
if ($pnpCableNames.Count -gt 8) { $pnpText = $pnpText + '; ...(+' + ($pnpCableNames.Count - 8) + ')' }

$gstSource = ''
$bundledBin = Join-Path $appDir 'gstreamer\bin'
if (Test-Path -LiteralPath (Join-Path $bundledBin 'gstreamer-1.0-0.dll')) {
    $gstSource = "bundled ($bundledBin)"
} elseif ($env:GSTREAMER_1_0_ROOT_MSVC_X86_64 -and
    (Test-Path -LiteralPath (Join-Path $env:GSTREAMER_1_0_ROOT_MSVC_X86_64 'bin\gstreamer-1.0-0.dll'))) {
    $gstSource = "env ($env:GSTREAMER_1_0_ROOT_MSVC_X86_64)"
} elseif (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'gstreamer\1.0\msvc_x86_64\bin\gstreamer-1.0-0.dll')) {
    $gstSource = 'Program Files'
} elseif (Get-Command gst-launch-1.0 -ErrorAction SilentlyContinue) {
    $gstSource = 'PATH'
}
if (-not $gstSource) {
    $problems += 'GStreamer runtime not found (bundled gstreamer\bin, GSTREAMER_1_0_ROOT_MSVC_X86_64, Program Files, or PATH)'
}

if (-not $SkipBuild -and -not (Test-Path -LiteralPath $testProj)) {
    $problems += "test project not found: $testProj"
}

$makeProject = Join-Path $PSScriptRoot 'make-ltc-scenario-project.ps1'
if ($MediaDir -and -not (Test-Path -LiteralPath $makeProject)) {
    $problems += "make-ltc-scenario-project.ps1 is not merged yet (removal team). Drop -MediaDir or run after it is merged."
}
if ($MediaDir -and (Test-Path -LiteralPath $MediaDir)) {
    $mediaFull = (Resolve-Path -LiteralPath $MediaDir).Path
    if (-not [string]::Equals([IO.Path]::GetPathRoot($mediaFull), [IO.Path]::GetPathRoot($ReportDir), [StringComparison]::OrdinalIgnoreCase)) {
        $problems += 'MediaDir and ReportDir must be on the same volume (hard links); pass -ReportDir on the MediaDir volume'
    }
}

Write-Output ('prereqs: cable_mm=[render: ' + ($renderCable -join '; ') + ' | capture: ' + ($captureCable -join '; ') +
    '] cable_pnp=[' + $pnpText + '] gstreamer=' + $gstSource)
if ($problems.Count -gt 0) {
    foreach ($p in $problems) { Write-Output ('PREREQ-ERROR ' + $p) }
    Write-Output ('SUMMARY prereq_failed=' + $problems.Count + ' report=' + $ReportDir)
    exit 2
}
Write-Output 'prereqs: OK'

# ---- media and project -----------------------------------------------------
$makeMedia = Join-Path $PSScriptRoot 'make-e2e-media.ps1'
& $makeMedia *> (Join-Path $ReportDir 'make-e2e-media.log')
if (-not $?) { throw "make-e2e-media.ps1 failed" }

function Remove-ScenarioProjectArtifacts {
    if ($KeepProject -and $projectPath) {
        Write-Output ('project_kept=' + $projectPath)
        Write-Output ('hardlinks_kept=' + $linkedMediaDir)
        return
    }
    if ($projectPath) {
        Remove-Item -LiteralPath $projectPath -Force -ErrorAction SilentlyContinue
    }
    Remove-LinkedMediaArtifacts
}

$projectPath = ''
$linkedMediaDir = ''
$spoutFrameCountState = $null
try {
if ($EnableExternalSpoutAudit) {
    # Spout frame numbers are controlled by the SDK's documented per-user option.
    # Snapshot and restore it so the external audit does not leave machine state behind.
    $spoutRegistryPath = 'HKCU:\Software\Leading Edge\Spout'
    $keyExisted = Test-Path -LiteralPath $spoutRegistryPath
    $valueExisted = $false
    $previousValue = $null
    if ($keyExisted) {
        $registryValues = Get-ItemProperty -LiteralPath $spoutRegistryPath -ErrorAction SilentlyContinue
        $frameCountProperty = $registryValues.PSObject.Properties['Framecount']
        if ($frameCountProperty) {
            $valueExisted = $true
            $previousValue = [int]$frameCountProperty.Value
        }
    }
    $spoutFrameCountState = [ordered]@{
        keyExisted = $keyExisted
        valueExisted = $valueExisted
        previousValue = $previousValue
        testValue = 1
        restored = $false
    }
    New-Item -Path $spoutRegistryPath -Force | Out-Null
    New-ItemProperty -LiteralPath $spoutRegistryPath -Name 'Framecount' -PropertyType DWord -Value 1 -Force | Out-Null
    $spoutFrameCountState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'spout-framecount-registry.json') -Encoding UTF8
}
if ($MediaDir) {
    if (-not (Test-Path -LiteralPath $MediaDir)) { throw "MediaDir not found: $MediaDir" }
    $MediaDir = (Resolve-Path -LiteralPath $MediaDir).Path

    # D19b: hard links only (no junction, no copy); the media folder stays untouched.
    $linkedMediaDir = Join-Path $ReportDir 'media'
    New-Item -ItemType Directory -Force -Path $linkedMediaDir | Out-Null
    $mediaExtensions = @('.mp4', '.mov', '.mkv', '.mxf', '.ts')
    foreach ($mediaFile in @(Get-ChildItem -LiteralPath $MediaDir -File |
        Where-Object { $mediaExtensions -contains $_.Extension.ToLowerInvariant() })) {
        $linkPath = Join-Path $linkedMediaDir $mediaFile.Name
        if (Test-Path -LiteralPath $linkPath) {
            if ((Get-HardLinkCount $linkPath) -lt 2) {
                throw ('refusing to replace a non-link file under ReportDir\media: ' + $linkPath)
            }
            [IO.File]::Delete($linkPath)
        }
        New-HardLink -LinkPath $linkPath -TargetPath $mediaFile.FullName
        $createdHardLinks += $linkPath
    }

    $projectPath = Join-Path $ReportDir 'ltc-scenario.tsp'
    $Media = @($Media -join ',')[0]
    $makeArgs = @{ MediaDir = $linkedMediaDir; Out = $projectPath }
    if ($Media) { $makeArgs.Media = $Media }
    if ($MediaInOffsetSeconds -ne 0) { $makeArgs.MediaInOffsetSeconds = $MediaInOffsetSeconds }
    if ($GapSeconds -ne 5) { $makeArgs.GapSeconds = $GapSeconds }
    if ($SegmentSeconds -gt 0) { $makeArgs.SegmentSeconds = $SegmentSeconds }
    Write-Output ('media_select=' + $(if ($Media) { $Media } else { '(first 3 by name)' }) +
        ' media_in_offset=' + $MediaInOffsetSeconds.ToString([Globalization.CultureInfo]::InvariantCulture))
    & $makeProject @makeArgs *> (Join-Path $ReportDir 'make-ltc-scenario-project.log')
    if (-not $?) { throw "make-ltc-scenario-project.ps1 failed" }
    if (-not (Test-Path -LiteralPath $projectPath)) { throw "project not generated: $projectPath" }
    $env:TIMECODE_LTC_SCENARIO_PROJECT = $projectPath
    $env:TIMECODE_REAL_PROJECT_PATH = $projectPath
    $realReport = Join-Path $ReportDir 'real-project'
    New-Item -ItemType Directory -Force -Path $realReport | Out-Null
    $env:TIMECODE_REAL_PROJECT_REPORT_DIR = $realReport
    Write-Output "project=$projectPath"
}

# ---- filter and environment ------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Filter)) {
    # RealProjectGapE2ETests is not in the default filter: it assumes a fixture project whose
    # timeline starts at one hour (it sends 01:00:xx), so against the project generated from
    # -MediaDir its checks do not apply. Pass -Filter explicitly to run it.
    $Filter = 'FullyQualifiedName~LtcHardwareLoopE2ETests|FullyQualifiedName~LtcScenarioE2ETests'
}
Write-Output "filter=$Filter"

$env:TIMECODE_SYNC_PLAYER_E2E_APP_PATH = $AppExe
$env:TCS_LTC_FPS = $LtcFps.ToString([Globalization.CultureInfo]::InvariantCulture)
if ($PreflightOnly) { $env:TCS_PREFLIGHT_ONLY = '1'; Write-Output 'preflight_only=1' } else { Remove-Item Env:TCS_PREFLIGHT_ONLY -ErrorAction SilentlyContinue }
Write-Output ('ltc_fps=' + $env:TCS_LTC_FPS)
# D23-d: the scenario tests write their journals and frame images under
# artifacts\ltc-scenarios unless this is set, which the evidence copy below does
# not look at. Keep them inside the report directory.
$scenarioReport = Join-Path $ReportDir 'scenarios'
New-Item -ItemType Directory -Force -Path $scenarioReport | Out-Null
$env:TIMECODE_LTC_SCENARIO_REPORT_DIR = $scenarioReport
if ($Cycles -gt 0) { $env:TIMECODE_LTC_SCENARIO_CYCLES = [string]$Cycles }
# L-1: continuous-follow audit parameters (empty/0 keeps the test defaults).
if ($FollowSeconds -gt 0) { $env:TCS_L1_FOLLOW_SECONDS = [string]$FollowSeconds }
if (-not [string]::IsNullOrWhiteSpace($FollowTracks)) { $env:TCS_L1_TRACKS = $FollowTracks }
if ($FollowWindowSeconds -gt 0) {
    $env:TCS_L1_WINDOW_SECONDS = $FollowWindowSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($FollowSettlingSeconds -gt 0) {
    $env:TCS_L1_SETTLING_SECONDS = $FollowSettlingSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($FollowStartGateSeconds -gt 0) {
    $env:TCS_L1_START_GATE_SECONDS = $FollowStartGateSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
}
if ($FollowSeconds -gt 0 -or -not [string]::IsNullOrWhiteSpace($FollowTracks) -or $FollowWindowSeconds -gt 0 -or
    $FollowSettlingSeconds -gt 0 -or $FollowStartGateSeconds -gt 0) {
    Write-Output ('l1: follow_seconds=' + $env:TCS_L1_FOLLOW_SECONDS + ' tracks=' + $env:TCS_L1_TRACKS +
        ' window_seconds=' + $env:TCS_L1_WINDOW_SECONDS + ' settling_seconds=' + $env:TCS_L1_SETTLING_SECONDS +
        ' start_gate_seconds=' + $env:TCS_L1_START_GATE_SECONDS)
}

# L-2: machine-observed continuity, load timing, and process health limits.
$env:TCS_L2_ENABLE_SPOUT = if ($EnableSpout) { '1' } else { '0' }
$env:TCS_L2_MAX_FRAME_DEFICIT_SECONDS = $MaxFrameDeficitSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L2_MAX_POSITION_STALL_SECONDS = $MaxPositionStallSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L2_MAX_SPOUT_RECEIVER_GAP_MS = $MaxSpoutReceiverGapMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L2_PROBE_INTERVAL_MS = $L2ProbeIntervalMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L2_MAX_LOAD_MILLISECONDS = $MaxLoadMilliseconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L2_HEALTH_SAMPLE_SECONDS = $HealthSampleSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L3_REHEARSAL_SECONDS = ($ProductionRehearsalHours * 3600).ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L3_BREAK_SECONDS = ($ProductionBreakHours * 3600).ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L3_SHOW_SECONDS = ($ProductionShowHours * 3600).ToString([Globalization.CultureInfo]::InvariantCulture)
$env:TCS_L3_POST_IDLE_SECONDS = ($ProductionPostIdleHours * 3600).ToString([Globalization.CultureInfo]::InvariantCulture)
if ($MaxPrivateGrowthMbPerHour -gt 0) {
    $env:TCS_L2_MAX_PRIVATE_GROWTH_MB_PER_HOUR = $MaxPrivateGrowthMbPerHour.ToString([Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:TCS_L2_MAX_PRIVATE_GROWTH_MB_PER_HOUR -ErrorAction SilentlyContinue
}
if ($MaxHandleGrowthPerHour -gt 0) {
    $env:TCS_L2_MAX_HANDLE_GROWTH_PER_HOUR = $MaxHandleGrowthPerHour.ToString([Globalization.CultureInfo]::InvariantCulture)
} else {
    Remove-Item Env:TCS_L2_MAX_HANDLE_GROWTH_PER_HOUR -ErrorAction SilentlyContinue
}
if ($EnableExternalSpoutAudit) {
    $spoutSenderName = 'TCS_Endurance_' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
    $env:TIMECODE_SYNC_PLAYER_SPOUT_NAME = $spoutSenderName
    $env:TCS_L2_SPOUT_SENDER_NAME = $spoutSenderName
    $env:TCS_L2_SPOUT_RECEIVER_EXE = $SpoutReceiverExe
    $env:TCS_L2_REQUIRE_EXTERNAL_SPOUT = '1'
    $env:TCS_L2_SPOUT_PIXEL_SAMPLE_MS = [string]$SpoutPixelSampleMilliseconds
} else {
    Remove-Item Env:TCS_L2_SPOUT_SENDER_NAME -ErrorAction SilentlyContinue
    Remove-Item Env:TCS_L2_SPOUT_RECEIVER_EXE -ErrorAction SilentlyContinue
    Remove-Item Env:TCS_L2_REQUIRE_EXTERNAL_SPOUT -ErrorAction SilentlyContinue
    Remove-Item Env:TCS_L2_SPOUT_PIXEL_SAMPLE_MS -ErrorAction SilentlyContinue
}
Write-Output ('l2: spout=' + $env:TCS_L2_ENABLE_SPOUT +
    ' external_spout_audit=' + [bool]$EnableExternalSpoutAudit +
    ' max_frame_deficit_seconds=' + $env:TCS_L2_MAX_FRAME_DEFICIT_SECONDS +
    ' max_position_stall_seconds=' + $env:TCS_L2_MAX_POSITION_STALL_SECONDS +
    ' max_spout_receiver_gap_ms=' + $env:TCS_L2_MAX_SPOUT_RECEIVER_GAP_MS +
    ' spout_pixel_sample_ms=' + $env:TCS_L2_SPOUT_PIXEL_SAMPLE_MS +
    ' probe_interval_ms=' + $env:TCS_L2_PROBE_INTERVAL_MS +
    ' max_load_milliseconds=' + $env:TCS_L2_MAX_LOAD_MILLISECONDS +
    ' health_sample_seconds=' + $env:TCS_L2_HEALTH_SAMPLE_SECONDS +
    ' max_private_growth_mb_per_hour=' + $env:TCS_L2_MAX_PRIVATE_GROWTH_MB_PER_HOUR +
    ' max_handle_growth_per_hour=' + $env:TCS_L2_MAX_HANDLE_GROWTH_PER_HOUR)

# ---- build and run ---------------------------------------------------------
# D23-c: Windows PowerShell 5.1 turns every stderr line of a native command into
# an ErrorRecord; with $ErrorActionPreference = 'Stop' the first one (xUnit writes
# "[FAIL]" lines to stderr) aborts the runner after dotnet exits, so the evidence
# copy and the SUMMARY line are skipped. Native output is also decoded with the
# console code page, which garbles the UTF-8 text of dotnet. Run native commands
# with 'Continue', decode as UTF-8, and write ErrorRecords as plain text.
function Invoke-NativeToLog([scriptblock]$Command, [string]$LogPath) {
    $previousPreference = $ErrorActionPreference
    $previousEncoding = $null
    try { $previousEncoding = [Console]::OutputEncoding } catch { }
    $ErrorActionPreference = 'Continue'
    try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }
    try {
        & $Command 2>&1 | ForEach-Object {
            if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.Exception.Message } else { [string]$_ }
        } | Out-File -FilePath $LogPath -Encoding utf8
        return $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previousPreference
        if ($previousEncoding) {
            try { [Console]::OutputEncoding = $previousEncoding } catch { }
        }
    }
}

if (-not $SkipBuild) {
    $buildExit = Invoke-NativeToLog { dotnet build $testProj -c Debug } (Join-Path $ReportDir 'build.log')
    if ($buildExit) { throw "tests build failed ($buildExit)" }
}

$testStart = Get-Date
$trxName = 'results.trx'
$testExit = Invoke-NativeToLog {
    dotnet test $testProj -c Debug --no-build --filter $Filter `
        --results-directory $ReportDir --logger "trx;LogFileName=$trxName"
} (Join-Path $ReportDir 'dotnet-test.log')
Get-Content -LiteralPath (Join-Path $ReportDir 'dotnet-test.log') -Tail 3 | ForEach-Object { Write-Output $_ }

# ---- copy evidence ---------------------------------------------------------
$appLogDest = Join-Path $ReportDir 'app-logs'
New-Item -ItemType Directory -Force -Path $appLogDest | Out-Null
$copiedLogs = @()
# Log folders to collect: the folder next to the app exe (copied to app-logs\).
# Without -AppExe, also the test output folder (copied to app-logs\test-bin\): an E2E
# started without TIMECODE_SYNC_PLAYER_E2E_APP_PATH (for example a plain dotnet test)
# prefers the TimecodeSyncPlayer.exe copied next to the test assembly, and its logs go
# to that folder.
$logSources = @(@{ Dir = (Join-Path $appDir 'logs'); Sub = '' })
if (-not $appExeGiven) {
    $testBinLogs = Join-Path $repoRoot 'tests\TimecodeSyncPlayer.Tests\bin\Debug\net8.0-windows\logs'
    if (-not [string]::Equals([IO.Path]::GetFullPath($testBinLogs), [IO.Path]::GetFullPath($logSources[0].Dir),
            [StringComparison]::OrdinalIgnoreCase)) {
        $logSources += @{ Dir = $testBinLogs; Sub = 'test-bin' }
    }
}
Write-Output ('app_log_dirs=' + (($logSources | ForEach-Object { $_.Dir }) -join ';'))
foreach ($source in $logSources) {
    if (-not (Test-Path -LiteralPath $source.Dir)) { continue }
    $dest = if ($source.Sub) { Join-Path $appLogDest $source.Sub } else { $appLogDest }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $logs = @(Get-ChildItem -LiteralPath $source.Dir -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -like 'timecodesyncplayer-*.log' -or $_.Name -like 'tcs-gst-*.log') -and
            $_.LastWriteTime -ge $testStart.AddMinutes(-2)
        })
    foreach ($log in $logs) {
        Copy-Item -LiteralPath $log.FullName -Destination $dest -Force
        $copiedLogs += $(if ($source.Sub) { $source.Sub + '\' + $log.Name } else { $log.Name })
    }
}

$testArtDest = Join-Path $ReportDir 'test-artifacts'
New-Item -ItemType Directory -Force -Path $testArtDest | Out-Null
$extensions = @('.jsonl', '.json', '.csv', '.txt', '.log', '.png', '.jpg', '.jpeg', '.bmp')
function Copy-RecentFiles([string]$root, [string]$tag) {
    if (-not (Test-Path -LiteralPath $root)) { return }
    $files = @(Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            $extensions -contains $_.Extension.ToLowerInvariant() -and
            $_.LastWriteTime -ge $testStart -and
            -not $_.FullName.StartsWith($ReportDir, [StringComparison]::OrdinalIgnoreCase)
        })
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($root.Length).TrimStart('\')
        $dest = Join-Path $testArtDest (Join-Path $tag $relative)
        New-Item -ItemType Directory -Force -Path (Split-Path $dest -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $dest -Force
    }
}
Copy-RecentFiles (Join-Path $repoRoot 'TestResults') 'testresults'

$normalizedRoot = $repoRoot.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
$sha = [System.Security.Cryptography.SHA256]::Create()
try {
    $hashBytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($normalizedRoot))
} finally {
    $sha.Dispose()
}
$token = ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
$testTempRoot = Join-Path $env:TEMP ('TimecodeSyncPlayer.Tests\' + $token.Substring(0, 12))
Copy-RecentFiles $testTempRoot 'test-temp'

# ---- summary ---------------------------------------------------------------
$passed = 0; $failed = 0; $skipped = 0; $failedNames = @()
$trxPath = Join-Path $ReportDir $trxName
if (Test-Path -LiteralPath $trxPath) {
    [xml]$trx = Get-Content -LiteralPath $trxPath
    $unitResults = @($trx.TestRun.Results.UnitTestResult)
    foreach ($unitResult in $unitResults) {
        switch ($unitResult.outcome) {
            'Passed' { $passed++ }
            'Failed' { $failed++; $failedNames += $unitResult.testName }
            default { $skipped++ }
        }
    }
}

$errFtl = 0
foreach ($name in $copiedLogs) {
    if ((Split-Path $name -Leaf) -notlike 'timecodesyncplayer-*.log') { continue }
    $path = Join-Path $appLogDest $name
    foreach ($line in (Get-Content -LiteralPath $path)) {
        if ($line -notmatch '\[(ERR|FTL)\]') { continue }
        if ($line -match '^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})') {
            $stamp = [datetime]::ParseExact($matches[1], 'yyyy-MM-dd HH:mm:ss', [Globalization.CultureInfo]::InvariantCulture)
            if ($stamp -lt $testStart) { continue }
        }
        $errFtl++
    }
}

$leftover = @(Get-Process -Name TimecodeSyncPlayer -ErrorAction SilentlyContinue)
Write-Output ('SUMMARY passed=' + $passed + ' failed=' + $failed + ' skipped=' + $skipped +
    ' err_ftl=' + $errFtl + ' leftover=' + $leftover.Count + ' report=' + $ReportDir)
foreach ($name in $failedNames) { Write-Output ('FAILED ' + $name) }
Write-Output ('app_logs=' + (($copiedLogs | Sort-Object) -join ','))
# Result JSON (run-result.json) and the retention rules (delete a passed run's output-trace and leftover media links).
# A failure here must not change the test result.
try {
    $reportArgs = @{ ReportDir = $ReportDir; Media = (@($Media) -join ','); Filter = $Filter;
        MediaInOffsetSeconds = $MediaInOffsetSeconds.ToString([Globalization.CultureInfo]::InvariantCulture) }
    if ($appExeGiven) { $reportArgs.AppExe = $AppExe }
    if (-not $KeepTrace) { $reportArgs.Prune = $true }
    & (Join-Path $PSScriptRoot 'ltc-run-report.ps1') @reportArgs
} catch {
    Write-Output ('RESULT-ERROR ' + $_.Exception.Message)
}
}
finally {
    if ($spoutFrameCountState) {
        $spoutRegistryPath = 'HKCU:\Software\Leading Edge\Spout'
        if ($spoutFrameCountState.valueExisted) {
            New-ItemProperty -LiteralPath $spoutRegistryPath -Name 'Framecount' -PropertyType DWord `
                -Value ([int]$spoutFrameCountState.previousValue) -Force | Out-Null
        } else {
            Remove-ItemProperty -LiteralPath $spoutRegistryPath -Name 'Framecount' -ErrorAction SilentlyContinue
        }
        $spoutFrameCountState.restored = $true
        $spoutFrameCountState | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDir 'spout-framecount-registry.json') -Encoding UTF8
    }
    Remove-ScenarioProjectArtifacts
}

if ($failed -gt 0 -or $testExit -ne 0) { exit 1 }
exit 0
