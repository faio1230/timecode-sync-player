#requires -Version 7.0
# Self-test of #43 (test infrastructure bundle): the LTC scenario runner must not leave
# an ffmpeg folder on its PATH, and make-ltc-scenario-project.ps1 must find ffprobe in
# the same order as the runner's ffmpeg (scripts\TcsFfmpeg.psm1):
#   TCS_FFPROBE, ffprobe.exe next to TCS_FFMPEG, -FfmpegDir\ffprobe.exe, PATH.
#
# Uses empty stand-in files named ffmpeg.exe / ffprobe.exe under %TEMP% (only their
# location is checked; they are never run). PATH, TCS_FFMPEG and TCS_FFPROBE are set
# for this process only and restored at the end. Starts no app and no test run.
#
#   pwsh -NoProfile -File scripts\test-runner-ffprobe-path.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'TcsFfmpeg.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'TcsChildScript.psm1') -Force

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output ('ok   ' + $Name) } else { Write-Output ('FAIL ' + $Name); $failures.Add($Name) }
}
function Touch([string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    [IO.File]::WriteAllBytes($Path, [byte[]]@())
}

$savedPath = $env:PATH
$savedFfmpeg = $env:TCS_FFMPEG
$savedFfprobe = $env:TCS_FFPROBE
$savedToolsDir = $env:TCS_FFMPEG_TOOLS_DIR
$makeProject = Join-Path $PSScriptRoot 'make-ltc-scenario-project.ps1'
$runner = Join-Path $PSScriptRoot 'run-ltc-scenarios.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-runner-ffprobe-' + [Guid]::NewGuid().ToString('N'))
try {
    # PATH without any folder that holds ffmpeg or ffprobe (the dev machine has them on PATH).
    $cleanPath = (@($savedPath -split ';' | Where-Object {
        $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'ffprobe.exe')) -and
            -not (Test-Path -LiteralPath (Join-Path $_ 'ffmpeg.exe'))
    }) -join ';')

    $envDir = Join-Path $root 'env'            # ffmpeg.exe + ffprobe.exe (TCS_FFMPEG)
    $envOnlyDir = Join-Path $root 'env-only'   # ffmpeg.exe without ffprobe.exe
    $argDir = Join-Path $root 'arg'            # ffprobe.exe (-FfmpegDir)
    $pathDir = Join-Path $root 'path'          # ffprobe.exe (PATH)
    $overrideDir = Join-Path $root 'override'  # ffprobe.exe (TCS_FFPROBE)
    Touch (Join-Path $envDir 'ffmpeg.exe'); Touch (Join-Path $envDir 'ffprobe.exe')
    Touch (Join-Path $envOnlyDir 'ffmpeg.exe')
    Touch (Join-Path $argDir 'ffprobe.exe')
    Touch (Join-Path $pathDir 'ffprobe.exe')
    Touch (Join-Path $overrideDir 'ffprobe.exe')
    $missingDir = Join-Path $root 'missing'
    # tools\ffmpeg of the repository (item 8) is replaced by an empty folder, so a pinned
    # build placed by get-ffmpeg.ps1 does not take part in these checks.
    $env:TCS_FFMPEG_TOOLS_DIR = Join-Path $root 'no-tools'

    # ---- resolution order ---------------------------------------------------------------
    $env:PATH = $pathDir + ';' + $cleanPath
    $env:TCS_FFPROBE = $null

    $env:TCS_FFMPEG = Join-Path $envDir 'ffmpeg.exe'
    $r = Resolve-TcsFfprobe -FfmpegDir $argDir
    Check ($r.Ffprobe -eq (Join-Path $envDir 'ffprobe.exe') -and $r.Source -eq 'next-to-TCS_FFMPEG') 'TCS_FFMPEG set: ffprobe next to it wins over -FfmpegDir and PATH'

    $env:TCS_FFMPEG = Join-Path $envOnlyDir 'ffmpeg.exe'
    $r = Resolve-TcsFfprobe -FfmpegDir $argDir
    Check ($r.Ffprobe -eq (Join-Path $argDir 'ffprobe.exe') -and $r.Source -eq 'arg:FfmpegDir') 'TCS_FFMPEG set without ffprobe next to it: -FfmpegDir next'

    $env:TCS_FFMPEG = $null
    $r = Resolve-TcsFfprobe -FfmpegDir $argDir
    Check ($r.Ffprobe -eq (Join-Path $argDir 'ffprobe.exe') -and $r.Source -eq 'arg:FfmpegDir') 'TCS_FFMPEG unset: -FfmpegDir wins over PATH'

    $r = Resolve-TcsFfprobe -FfmpegDir $missingDir
    Check ($r.Ffprobe -eq (Join-Path $pathDir 'ffprobe.exe') -and $r.Source -eq 'PATH') 'only PATH: PATH'

    $env:PATH = $cleanPath
    $r = Resolve-TcsFfprobe -FfmpegDir $missingDir
    Check ($r.Ffprobe -eq '' -and $r.Source -eq '') 'nowhere: empty result'
    Check (@($r.Searched).Count -eq 5 -and ($r.Searched -join ';').Contains((Join-Path $missingDir 'ffprobe.exe')) -and
        (@($r.Searched)[-1] -eq 'PATH')) 'nowhere: Searched lists the five levels'

    $env:TCS_FFMPEG = Join-Path $envDir 'ffmpeg.exe'
    $env:TCS_FFPROBE = Join-Path $overrideDir 'ffprobe.exe'
    $r = Resolve-TcsFfprobe -FfmpegDir $argDir
    Check ($r.Ffprobe -eq (Join-Path $overrideDir 'ffprobe.exe') -and $r.Source -eq 'env:TCS_FFPROBE') 'TCS_FFPROBE overrides every level (module rule)'
    $env:TCS_FFPROBE = Join-Path $missingDir 'ffprobe.exe'
    $threw = $false
    try { Resolve-TcsFfprobe -FfmpegDir $argDir | Out-Null } catch { $threw = $true }
    Check $threw 'TCS_FFPROBE pointing nowhere stops'
    $env:TCS_FFPROBE = $null
    $env:TCS_FFMPEG = $null

    # ---- make-ltc-scenario-project does not touch PATH ----------------------------------
    # In this process (as the runner did before #43): -FfmpegDir exists, the media folder
    # is empty, so the script stops after resolving ffprobe. PATH must be unchanged.
    $outDir = Join-Path $root 'report'
    $mediaDir = Join-Path $outDir 'media'
    New-Item -ItemType Directory -Force -Path $mediaDir | Out-Null
    $env:PATH = $cleanPath
    $before = $env:PATH
    $message = ''
    try {
        & $makeProject -MediaDir $mediaDir -Out (Join-Path $outDir 'p.tsp') -FfmpegDir $argDir *> $null
    } catch { $message = $_.Exception.Message }
    Check ($env:PATH -eq $before) 'make-ltc-scenario-project run in this process leaves PATH as it was'
    Check ($message -like '*fewer than 3 video files*') ('make-ltc-scenario-project got past ffprobe with -FfmpegDir only (' + $message + ')')

    # Nowhere: stops with where it looked.
    $message = ''
    try {
        & $makeProject -MediaDir $mediaDir -Out (Join-Path $outDir 'p.tsp') -FfmpegDir $missingDir *> $null
    } catch { $message = $_.Exception.Message }
    Check ($message -like 'ffprobe not found (searched:*' -and $message.Contains((Join-Path $missingDir 'ffprobe.exe'))) 'make-ltc-scenario-project without ffprobe stops and names the places searched'
    Check ($env:PATH -eq $before) 'PATH unchanged after the failed run'

    # ---- child process (what the runner does now) ---------------------------------------
    $child = Join-Path $root 'child.ps1'
    @(
        'param([string]$Name, [double]$Number = 0, [string]$List = "", [switch]$Flag)',
        '$env:PATH = $env:PATH + ";C:\added-by-child"',
        'Write-Output ("name=" + $Name + " number=" + $Number.ToString([Globalization.CultureInfo]::InvariantCulture) + " list=" + $List + " flag=" + $Flag.IsPresent)',
        'if ($Name -eq "fail") { throw "child failed" }'
    ) | Set-Content -LiteralPath $child -Encoding UTF8
    $log = Join-Path $root 'child.log'
    $before = $env:PATH
    $exit = Invoke-TcsChildScript -ScriptPath $child -Arguments ([ordered]@{ Name = 'ok'; Number = 1.5; List = @('M5', 'M6', 'M1'); Flag = $true }) -LogPath $log
    $text = [IO.File]::ReadAllText($log)
    Check ($exit -eq 0) 'child exit code 0 is returned'
    Check ($env:PATH -eq $before) 'PATH the child changed does not come back to the caller'
    Check ($text.Contains('name=ok number=1.5 list=M5,M6,M1 flag=True')) ('arguments reach the child (' + $text.Trim() + ')')
    $exit = Invoke-TcsChildScript -ScriptPath $child -Arguments @{ Name = 'fail' } -LogPath $log
    Check ($exit -ne 0) ('a child that throws returns non-zero (' + $exit + ')')
    Check (([IO.File]::ReadAllText($log)).Contains('child failed')) 'the error of the child is in the log'

    # make-ltc-scenario-project through the child, with ffprobe only next to TCS_FFMPEG.
    $env:TCS_FFMPEG = Join-Path $envDir 'ffmpeg.exe'
    $exit = Invoke-TcsChildScript -ScriptPath $makeProject -Arguments @{ MediaDir = $mediaDir; Out = (Join-Path $outDir 'p.tsp'); FfmpegDir = $missingDir } -LogPath $log
    $text = [IO.File]::ReadAllText($log)
    Check ($exit -ne 0 -and $text.Contains('ffprobe: ' + (Join-Path $envDir 'ffprobe.exe') + ' (next-to-TCS_FFMPEG)')) 'child make-ltc-scenario-project logs the ffprobe next to TCS_FFMPEG'
    Check ($env:PATH -eq $before) 'PATH unchanged after the child make-ltc-scenario-project'
    $env:TCS_FFMPEG = $null

    # ---- the runner calls the project script through the child -------------------------
    $runnerText = [IO.File]::ReadAllText($runner)
    Check (-not ($runnerText -match '&\s*\$makeProject')) 'the runner no longer runs make-ltc-scenario-project in its own process'
    Check ($runnerText.Contains('Invoke-TcsChildScript -ScriptPath $makeProject')) 'the runner uses Invoke-TcsChildScript for make-ltc-scenario-project'
    Check ($runnerText.Contains('$makeArgs.FfmpegDir = $ffmpegDefaultDir') -and
        $runnerText.Contains('Resolve-TcsFfmpeg -FfmpegDir $ffmpegDefaultDir')) 'the runner passes the -FfmpegDir of its ffmpeg check to the project script'
    $makeText = [IO.File]::ReadAllText($makeProject)
    Check (-not ($makeText -match '\$env:PATH\s*=')) 'make-ltc-scenario-project does not assign PATH'
} finally {
    $env:PATH = $savedPath
    $env:TCS_FFMPEG = $savedFfmpeg
    $env:TCS_FFPROBE = $savedFfprobe
    $env:TCS_FFMPEG_TOOLS_DIR = $savedToolsDir
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures.Count -gt 0) {
    Write-Output ('FAILED: ' + $failures.Count)
    exit 1
}
Write-Output 'ALL OK'
exit 0
