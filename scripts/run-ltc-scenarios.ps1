# Run the LTC E2E scenarios against one app (installed or a local Debug build)
# with a single command, and leave the evidence in one report directory.
#
#   powershell -File scripts\run-ltc-scenarios.ps1 -AppExe <path to TimecodeSyncPlayer.exe>
#   powershell -File scripts\run-ltc-scenarios.ps1 -Filter "FullyQualifiedName~NoSuchTest"   # dry run
#   powershell -File scripts\run-ltc-scenarios.ps1 -MediaDir <real media folder> [-KeepProject]
#       (the project .tsp is generated under the report directory, never in the
#        media folder, and is removed after the run unless -KeepProject is set)
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
    [switch]$KeepProject,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$testProj = Join-Path $repoRoot 'tests\TimecodeSyncPlayer.Tests\TimecodeSyncPlayer.Tests.csproj'

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
if (-not (Test-Path -LiteralPath (Join-Path $appDir 'tcs_gstreamer.dll'))) {
    $problems += "tcs_gstreamer.dll is missing next to the exe (run build-shim): $appDir"
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
try {
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
    & $makeProject -MediaDir $linkedMediaDir -Out $projectPath *> (Join-Path $ReportDir 'make-ltc-scenario-project.log')
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
    $Filter = 'FullyQualifiedName~LtcHardwareLoopE2ETests|FullyQualifiedName~LtcScenarioE2ETests'
    if ($MediaDir) { $Filter += '|FullyQualifiedName~RealProjectGapE2ETests' }
}
Write-Output "filter=$Filter"

$env:TIMECODE_SYNC_PLAYER_E2E_APP_PATH = $AppExe
if ($Cycles -gt 0) { $env:TIMECODE_LTC_SCENARIO_CYCLES = [string]$Cycles }

# ---- build and run ---------------------------------------------------------
if (-not $SkipBuild) {
    & dotnet build $testProj -c Debug *>&1 |
        Out-File -FilePath (Join-Path $ReportDir 'build.log') -Encoding utf8
    if ($LASTEXITCODE) { throw "tests build failed ($LASTEXITCODE)" }
}

$testStart = Get-Date
$trxName = 'results.trx'
& dotnet test $testProj -c Debug --no-build --filter $Filter `
    --results-directory $ReportDir --logger "trx;LogFileName=$trxName" *>&1 |
    Out-File -FilePath (Join-Path $ReportDir 'dotnet-test.log') -Encoding utf8
$testExit = $LASTEXITCODE
Get-Content -LiteralPath (Join-Path $ReportDir 'dotnet-test.log') -Tail 3 | ForEach-Object { Write-Output $_ }

# ---- copy evidence ---------------------------------------------------------
$appLogDest = Join-Path $ReportDir 'app-logs'
New-Item -ItemType Directory -Force -Path $appLogDest | Out-Null
$copiedLogs = @()
$logDir = Join-Path $appDir 'logs'
if (Test-Path -LiteralPath $logDir) {
    $logs = @(Get-ChildItem -LiteralPath $logDir -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.Name -like 'timecodesyncplayer-*.log' -or $_.Name -like 'tcs-gst-*.log') -and
            $_.LastWriteTime -ge $testStart.AddMinutes(-2)
        })
    foreach ($log in $logs) {
        Copy-Item -LiteralPath $log.FullName -Destination $appLogDest -Force
        $copiedLogs += $log.Name
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
    if ($name -notlike 'timecodesyncplayer-*.log') { continue }
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
}
finally {
    Remove-ScenarioProjectArtifacts
}

if ($failed -gt 0 -or $testExit -ne 0) { exit 1 }
exit 0
