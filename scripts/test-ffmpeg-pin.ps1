#requires -Version 7.0
# Self-test of item 8 of the test infrastructure bundle (pin the ffmpeg of the tests):
#   - the order of Resolve-TcsFfmpeg / Resolve-TcsFfprobe (scripts\TcsFfmpeg.psm1):
#     TCS_FFMPEG, tools\ffmpeg (scripts\get-ffmpeg.ps1), -FfmpegDir (Program Files by
#     default in the scripts), PATH;
#   - the major version read from "ffmpeg -version" (4.2.3, n5.0, 6.0, 8.0.1, a git build)
#     and the stop before a run below 6 (Get-TcsFfmpegVersionStop);
#   - the runner adds that stop to its prerequisites and writes the ffmpeg to
#     runner-preflight.json, and ltc-run-report.ps1 copies it to run-result.json.
#
# Uses empty stand-in files named ffmpeg.exe / ffprobe.exe under %TEMP% (only their
# location is checked; Resolve-TcsFfmpeg -NoVersion never runs them). PATH, TCS_FFMPEG,
# TCS_FFPROBE and TCS_FFMPEG_TOOLS_DIR are set for this process only and restored at
# the end. Starts no app and no test run.
#
#   pwsh -NoProfile -File scripts\test-ffmpeg-pin.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'TcsFfmpeg.psm1') -Force

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
$root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-ffmpeg-pin-' + [Guid]::NewGuid().ToString('N'))
try {
    # PATH without any folder that holds ffmpeg or ffprobe (the dev machine has them on PATH).
    $cleanPath = (@($savedPath -split ';' | Where-Object {
        $_ -and -not (Test-Path -LiteralPath (Join-Path $_ 'ffprobe.exe')) -and
            -not (Test-Path -LiteralPath (Join-Path $_ 'ffmpeg.exe'))
    }) -join ';')

    $envDir = Join-Path $root 'env'          # TCS_FFMPEG
    $toolsDir = Join-Path $root 'tools'      # tools\ffmpeg
    $programDir = Join-Path $root 'program'  # -FfmpegDir (Program Files in the scripts)
    $pathDir = Join-Path $root 'path'        # PATH
    $missingDir = Join-Path $root 'missing'
    foreach ($dir in @($envDir, $toolsDir, $programDir, $pathDir)) {
        Touch (Join-Path $dir 'ffmpeg.exe'); Touch (Join-Path $dir 'ffprobe.exe')
    }
    $env:TCS_FFPROBE = $null

    # ---- ffmpeg: resolution order -----------------------------------------------------------
    $env:PATH = $pathDir + ';' + $cleanPath
    $env:TCS_FFMPEG_TOOLS_DIR = $toolsDir
    $env:TCS_FFMPEG = Join-Path $envDir 'ffmpeg.exe'
    $r = Resolve-TcsFfmpeg -FfmpegDir $programDir -NoVersion
    Check ($r.Ffmpeg -eq (Join-Path $envDir 'ffmpeg.exe') -and $r.Source -eq 'env:TCS_FFMPEG' -and
        $r.Ffprobe -eq (Join-Path $envDir 'ffprobe.exe') -and $r.FfprobeSource -eq 'next-to-ffmpeg') 'TCS_FFMPEG set: it wins over tools\ffmpeg, -FfmpegDir and PATH'

    $env:TCS_FFMPEG = $null
    $r = Resolve-TcsFfmpeg -FfmpegDir $programDir -NoVersion
    Check ($r.Ffmpeg -eq (Join-Path $toolsDir 'ffmpeg.exe') -and $r.Source -eq 'repo:tools\ffmpeg' -and
        $r.Ffprobe -eq (Join-Path $toolsDir 'ffprobe.exe')) 'TCS_FFMPEG unset: tools\ffmpeg wins over -FfmpegDir (Program Files) and PATH'

    $env:TCS_FFMPEG_TOOLS_DIR = $missingDir
    $r = Resolve-TcsFfmpeg -FfmpegDir $programDir -NoVersion
    Check ($r.Ffmpeg -eq (Join-Path $programDir 'ffmpeg.exe') -and $r.Source -eq 'arg:FfmpegDir') 'no tools\ffmpeg: -FfmpegDir (Program Files) wins over PATH'

    $r = Resolve-TcsFfmpeg -FfmpegDir $missingDir -NoVersion
    Check ($r.Ffmpeg -eq (Join-Path $pathDir 'ffmpeg.exe') -and $r.Source -eq 'PATH') 'only PATH: PATH is the last resort'

    $env:PATH = $cleanPath
    $threw = ''
    try { Resolve-TcsFfmpeg -FfmpegDir $missingDir -NoVersion | Out-Null } catch { $threw = $_.Exception.Message }
    Check ($threw -like 'ffmpeg not found*get-ffmpeg.ps1*') ('nowhere: stops and names get-ffmpeg.ps1 (' + $threw + ')')

    $env:TCS_FFMPEG = Join-Path $missingDir 'ffmpeg.exe'
    $threw = ''
    try { Resolve-TcsFfmpeg -FfmpegDir $programDir -NoVersion | Out-Null } catch { $threw = $_.Exception.Message }
    Check ($threw -like 'TCS_FFMPEG does not point to a file*') 'TCS_FFMPEG pointing nowhere stops (no fallback)'
    $env:TCS_FFMPEG = $null

    # ---- ffprobe alone: tools\ffmpeg comes after TCS_FFMPEG and before -FfmpegDir --------------
    $env:PATH = $pathDir + ';' + $cleanPath
    $env:TCS_FFMPEG_TOOLS_DIR = $toolsDir
    $r = Resolve-TcsFfprobe -FfmpegDir $programDir
    Check ($r.Ffprobe -eq (Join-Path $toolsDir 'ffprobe.exe') -and $r.Source -eq 'repo:tools\ffmpeg') 'ffprobe: tools\ffmpeg wins over -FfmpegDir and PATH'
    $env:TCS_FFMPEG = Join-Path $envDir 'ffmpeg.exe'
    $r = Resolve-TcsFfprobe -FfmpegDir $programDir
    Check ($r.Source -eq 'next-to-TCS_FFMPEG') 'ffprobe: next to TCS_FFMPEG wins over tools\ffmpeg'
    $env:TCS_FFMPEG = $null

    # ---- the default tools\ffmpeg is the one of this repository -----------------------------
    $env:TCS_FFMPEG_TOOLS_DIR = $null
    $expectedTools = Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\ffmpeg'
    Check ((Get-TcsFfmpegToolsDir) -eq $expectedTools) ('tools dir defaults to <repo>\tools\ffmpeg (' + (Get-TcsFfmpegToolsDir) + ')')

    # ---- version: major and the stop below 6 -------------------------------------------------
    $cases = @(
        @{ Lines = @('ffmpeg version 4.2.3 Copyright (c) 2000-2020 the FFmpeg developers', 'libavcodec     58. 54.100 / 58. 54.100'); Major = 4; Stops = $true },
        @{ Lines = @('ffmpeg version n5.0 Copyright (c) 2000-2022 the FFmpeg developers', 'libavcodec     59. 18.100 / 59. 18.100'); Major = 5; Stops = $true },
        @{ Lines = @('ffmpeg version 5.0 Copyright (c) 2000-2022 the FFmpeg developers'); Major = 5; Stops = $true },
        @{ Lines = @('ffmpeg version 6.0-full_build-www.gyan.dev Copyright (c) 2000-2023 the FFmpeg developers', 'libavcodec     60.  3.100 / 60.  3.100'); Major = 6; Stops = $false },
        @{ Lines = @('ffmpeg version 8.0.1-full_build-www.gyan.dev Copyright (c) 2000-2025 the FFmpeg developers', 'libavcodec     62. 11.100 / 62. 11.100'); Major = 8; Stops = $false },
        @{ Lines = @('ffmpeg version N-109850-g78f46065d8-20230212 Copyright (c) 2000-2023 the FFmpeg developers', 'libavcodec     60.  1.100 / 60.  1.100'); Major = 6; Stops = $false },
        @{ Lines = @('something else'); Major = -1; Stops = $true }
    )
    foreach ($case in $cases) {
        $major = Get-TcsFfmpegMajor -VersionLines $case.Lines
        $resolved = [pscustomobject]@{
            Ffmpeg = 'C:\x\ffmpeg.exe'; Source = 'arg:FfmpegDir'; Ffprobe = ''; FfprobeSource = ''
            VersionLine = $case.Lines[0]; Major = $major
        }
        $stop = Get-TcsFfmpegVersionStop $resolved
        $label = $case.Lines[0].Substring(0, [Math]::Min(40, $case.Lines[0].Length))
        Check ($major -eq $case.Major) ('major ' + $case.Major + ': ' + $label + ' (got ' + $major + ')')
        Check ([bool]$stop -eq $case.Stops) ('stops=' + $case.Stops + ': ' + $label)
        if ($stop) {
            Check ($stop.Contains('C:\x\ffmpeg.exe') -and $stop.Contains('arg:FfmpegDir') -and $stop.Contains($case.Lines[0]) -and
                $stop.Contains('get-ffmpeg.ps1')) ('the stop names the place, the version and get-ffmpeg.ps1: ' + $label)
        }
    }

    # ---- records: runner-preflight.json and run-result.json ---------------------------------
    $record = Get-TcsFfmpegRecord ([pscustomobject]@{
        Ffmpeg = 'C:\t\ffmpeg.exe'; Source = 'repo:tools\ffmpeg'; Ffprobe = 'C:\t\ffprobe.exe'; FfprobeSource = 'next-to-ffmpeg'
        VersionLine = 'ffmpeg version 8.0.1-full_build-www.gyan.dev'; Major = 8
    })
    Check ($record.path -eq 'C:\t\ffmpeg.exe' -and $record.source -eq 'repo:tools\ffmpeg' -and $record.major -eq 8 -and
        $record.versionLine.StartsWith('ffmpeg version 8.0.1') -and $record.ffprobe -eq 'C:\t\ffprobe.exe') 'record carries path, source, version line, major and ffprobe'

    $runnerText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'run-ltc-scenarios.ps1'))
    Check ($runnerText.Contains('Get-TcsFfmpegVersionStop $ffmpegResolved') -and
        $runnerText.Contains("`$problems += ('ffmpeg: ' + `$ffmpegStop)")) 'the runner adds the version stop to its prerequisites'
    Check ($runnerText.Contains('ffmpeg = $ffmpegRecord')) 'the runner writes the ffmpeg to runner-preflight.json'
    Check ($runnerText.Contains("Destination (Join-Path `$ReportDir 'ffmpeg-version.txt')")) 'the runner copies ffmpeg-version.txt of the media into the report'
    $reportText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'ltc-run-report.ps1'))
    Check ($reportText.Contains("Get-TcsRunnerPreflightValue `$ReportDir 'ffmpeg'") -and $reportText.Contains('ffmpeg = $ffmpegAtStart')) 'ltc-run-report copies the ffmpeg into run-result.json'

    # ---- get-ffmpeg.ps1 pins the build ------------------------------------------------------
    $getText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'get-ffmpeg.ps1'))
    Check ($getText -match '\$expectedZipSha256 = "[0-9a-f]{64}"' -and $getText -match '\$expectedFfmpegSha256 = "[0-9a-f]{64}"' -and
        $getText -match '\$expectedFfprobeSha256 = "[0-9a-f]{64}"') 'get-ffmpeg.ps1 pins the SHA-256 of the zip, ffmpeg.exe and ffprobe.exe'
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
