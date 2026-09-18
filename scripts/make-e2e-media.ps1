# Generate the test clips the E2E suite expects under artifacts/media (gitignored).
#
# GStreamerBackendE2ETests and friends look for fixed file names. When they are
# missing the tests are skipped, so shim coverage in E2E drops to zero. The v0.4
# goal ("all E2E pass" before switching the default backend) needs these clips,
# and every worktree needs its own copy because artifacts/ is gitignored.
#
#   powershell -File scripts\make-e2e-media.ps1
#   powershell -File scripts\make-e2e-media.ps1 -OutDir D:\media -Force
#
# Requires ffmpeg (default C:\Program Files\ffmpeg\bin).
#
# NOTE: keep this file ASCII-only and BOM-less, like the other scripts in this
# repo. Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page, so
# non-ASCII comments break parsing.
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 leaves $PSScriptRoot empty inside param() defaults when the
# script is started with powershell -File, so the default is resolved here.
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\media'
}

# Resolve one concrete ffmpeg.exe and call it by full path. -FfmpegDir used to be
# APPENDED to PATH, so an older ffmpeg earlier on PATH won: ImageMagick ships
# ffmpeg 4.2.3 in its install directory, which has no libsvtav1, and the 4K AV1
# fixture died with "Unknown encoder 'libsvtav1'" while a 2023 build with both
# libsvtav1 and prores_ks sat in C:\Program Files\ffmpeg\bin (seen 2026-09-19).
# -FfmpegDir now wins outright, and the build actually used is printed.
$script:FfmpegExe = ''
$ffmpegCandidate = Join-Path $FfmpegDir 'ffmpeg.exe'
if (Test-Path -LiteralPath $ffmpegCandidate) {
    $script:FfmpegExe = (Get-Item -LiteralPath $ffmpegCandidate).FullName
} else {
    $ffmpegOnPath = Get-Command ffmpeg -ErrorAction SilentlyContinue
    if ($ffmpegOnPath) { $script:FfmpegExe = $ffmpegOnPath.Source }
}
if ([string]::IsNullOrWhiteSpace($script:FfmpegExe)) {
    throw 'ffmpeg not found (pass -FfmpegDir)'
}
Write-Output ('ffmpeg: ' + $script:FfmpegExe)

New-Item -ItemType Directory -Force $OutDir | Out-Null

# Windows PowerShell 5.1 turns each stderr line of a native command into an
# ErrorRecord, and with $ErrorActionPreference = 'Stop' the first one aborts the
# script while ffmpeg is still writing. libsvtav1 prints "Svt[info]" banners to
# stderr regardless of -v error, so the AV1 fixture aborted the whole runner and
# left a zero-byte file behind that later runs skipped as "exists". Run ffmpeg
# with 'Continue', judge by the exit code, and remove the output on failure.
$env:SVT_LOG = '1'
function Invoke-Ffmpeg([string[]]$FfArgs, [string]$OutputPath, [string]$Name) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $messages = @(& $script:FfmpegExe @FfArgs 2>&1 | ForEach-Object { [string]$_ })
        $code = $LASTEXITCODE
    } finally {
        $ErrorActionPreference = $previous
    }
    if ($code -ne 0) {
        $messages | Select-Object -Last 20 | ForEach-Object { Write-Output ('ffmpeg: ' + $_) }
        if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
        throw ('ffmpeg failed: ' + $Name + ' (exit ' + $code + ')')
    }
}

# An existing fixture is reused only when it is not empty (a zero-byte file is
# what an interrupted run leaves behind).
function Test-Fixture([string]$Path) {
    return (Test-Path -LiteralPath $Path) -and ((Get-Item -LiteralPath $Path).Length -gt 0)
}

# libx264 only. The ffmpeg build on this machine exposes h264_nvenc with the old
# preset names (default/slow/medium/hq/...) and rejects p1..p7; even 'medium'
# fails with "Cannot get the preset configuration: unsupported param" (verified
# 2026-09-12). These are test fixtures with no quality requirement.
$venc = @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20')
Write-Output 'encoder: libx264'

# GOP is one second (-g = fps). The TS fixture carries SPS/PPS before every IDR
# to match broadcast streams (and to stay clear of the S3 repro conditions).
$specs = @(
    @{ Name = 'test_1080p60.mp4';     W = 1920; H = 1080; Fps = 60; Sec = 30; Extra = @() },
    @{ Name = 'test_720p60_long.mp4'; W = 1280; H = 720;  Fps = 60; Sec = 45; Extra = @() },
    @{ Name = 'test_720p25.mkv';      W = 1280; H = 720;  Fps = 25; Sec = 20; Extra = @() },
    @{ Name = 'test_720p25.avi';      W = 1280; H = 720;  Fps = 25; Sec = 20; Extra = @() },
    @{ Name = 'test_720p50.ts';       W = 1280; H = 720;  Fps = 50; Sec = 20; Extra = @('-bsf:v', 'dump_extra=freq=keyframe') }
)

foreach ($s in $specs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Fixture $path) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $src = 'testsrc2=size=' + $s.W + 'x' + $s.H + ':rate=' + $s.Fps + ':duration=' + $s.Sec
    $ffargs = @('-y', '-hide_banner', '-v', 'error', '-f', 'lavfi', '-i', $src) +
              $venc + @('-g', "$($s.Fps)", '-pix_fmt', 'yuv420p') + $s.Extra + @($path)
    Write-Output ('making: ' + $s.Name + ' (' + $s.W + 'x' + $s.H + '@' + $s.Fps + ', ' + $s.Sec + 's)')
    Invoke-Ffmpeg $ffargs $path $s.Name
}

# v0.4.1 D12/D13: audio-bearing fixtures. (a) AAC 44.1kHz with the audio track
# first (isolates the missing audioresample: wasapi2sink runs at the device mix
# rate), (b) AAC 48kHz with the video track first (ffmpeg default order; the
# demux video branch needs a queue for the audio sink to preroll).
$audioSpecs = @(
    @{ Name = 'test_720p30_aac44k.mp4';             AudioRate = 44100; AudioFirst = $true },
    @{ Name = 'test_720p30_aac48k_video_first.mp4'; AudioRate = 48000; AudioFirst = $false }
)

foreach ($s in $audioSpecs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Fixture $path) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $video = 'testsrc2=size=1280x720:rate=30:duration=20'
    $audio = 'sine=frequency=1000:sample_rate=' + $s.AudioRate + ':duration=20'
    if ($s.AudioFirst) {
        $inputs = @('-f', 'lavfi', '-i', $audio, '-f', 'lavfi', '-i', $video, '-map', '0:a', '-map', '1:v')
    } else {
        $inputs = @('-f', 'lavfi', '-i', $video, '-f', 'lavfi', '-i', $audio)
    }
    $ffargs = @('-y', '-hide_banner', '-v', 'error') + $inputs +
              $venc + @('-g', '30', '-pix_fmt', 'yuv420p') +
              @('-c:a', 'aac', '-b:a', '128k', '-ar', "$($s.AudioRate)", '-shortest', $path)
    Write-Output ('making: ' + $s.Name + ' (1280x720@30 + AAC ' + $s.AudioRate + 'Hz)')
    Invoke-Ffmpeg $ffargs $path $s.Name
}

# LTC scenario fixtures (track symbols A/B/C): three solid colour clips with
# distinct head/tail colours so the E2E pixel probes can identify the frame. The
# burned-in second number is for eyeballing only and sits outside the centre 60%
# region the probes measure. The LTC scenario tests skip when these are missing.
$scenarioSpecs = @(
    @{ Name = 'ltc_a.mp4'; Base = '0xFF0000'; Head = '0xFFFFFF'; Tail = '0xFFFF00' },
    @{ Name = 'ltc_b.mp4'; Base = '0x00FF00'; Head = '0xFF00FF'; Tail = '0x00FFFF' },
    @{ Name = 'ltc_c.mp4'; Base = '0x0000FF'; Head = '';       Tail = '0xFFA500' }
)

foreach ($s in $scenarioSpecs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Fixture $path) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $filters = 'color=c=' + $s.Base + ':s=1280x720:r=30:d=20'
    if ($s.Head -ne '') {
        $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Head):t=fill:enable='between(t,0,1)'"
    }
    $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Tail):t=fill:enable='between(t,19,20)'"
    $filters += ",drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{eif\:floor(t)\:d}':x=20:y=20:fontsize=48:fontcolor=black"
    $ffargs = @('-y', '-hide_banner', '-v', 'error', '-f', 'lavfi', '-i', $filters) +
              $venc + @('-g', '30', '-pix_fmt', 'yuv420p', '-an', $path)
    Write-Output ('making: ' + $s.Name + ' (1280x720@30, colour fixture)')
    Invoke-Ffmpeg $ffargs $path $s.Name
}

# A1 reproduction fixtures: 4K colour clips whose CPU decode can push the first
# frame after a paused seek past the gap-freeze capture window. Same
# head/base/tail structure as the 1280x720 fixtures above (head 0..1s when the
# fixture has one, tail last second), 12s long, new names only: the three
# fixtures above are not touched and the "first three by name" selection of the
# scenario runner is unchanged. The C-role clip has no head colour, like its
# 1280x720 counterpart.
#   ProRes: prores_ks profile 3 (422 HQ), yuv422p10le, 60fps, one-second GOP.
#   AV1:    libsvtav1 preset 10, yuv420p, 24fps, one-second GOP (CPU decode).
$scenario4kSpecs = @(
    @{ Name = 'ltc_d_4k60_prores.mov'; W = 3840; H = 2160; Fps = 60; Sec = 12;
       Base = '0xFF0000'; Head = '0xFFFFFF'; Tail = '0xFFFF00';
       Encoder = @('-c:v', 'prores_ks', '-profile:v', '3', '-pix_fmt', 'yuv422p10le') },
    @{ Name = 'ltc_e_4k24_av1.mp4'; W = 3840; H = 2160; Fps = 24; Sec = 12;
       Base = '0x00FF00'; Head = '0xFF00FF'; Tail = '0x00FFFF';
       Encoder = @('-c:v', 'libsvtav1', '-preset', '10', '-pix_fmt', 'yuv420p') },
    @{ Name = 'ltc_f_4k60_prores.mov'; W = 3840; H = 2160; Fps = 60; Sec = 12;
       Base = '0x0000FF'; Head = '';       Tail = '0xFFA500';
       Encoder = @('-c:v', 'prores_ks', '-profile:v', '3', '-pix_fmt', 'yuv422p10le') }
)

foreach ($s in $scenario4kSpecs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Fixture $path) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $tailStart = $s.Sec - 1
    $filters = 'color=c=' + $s.Base + ':s=' + $s.W + 'x' + $s.H + ':r=' + $s.Fps + ':d=' + $s.Sec
    if ($s.Head -ne '') {
        $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Head):t=fill:enable='between(t,0,1)'"
    }
    $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Tail):t=fill:enable='between(t," + $tailStart + ',' + $s.Sec + ")'"
    $filters += ",drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{eif\:floor(t)\:d}':x=20:y=20:fontsize=96:fontcolor=black"
    $ffargs = @('-y', '-hide_banner', '-v', 'error', '-f', 'lavfi', '-i', $filters) +
              $s.Encoder + @('-g', "$($s.Fps)", '-an', $path)
    Write-Output ('making: ' + $s.Name + ' (' + $s.W + 'x' + $s.H + '@' + $s.Fps + ', colour fixture)')
    Invoke-Ffmpeg $ffargs $path $s.Name
}

Write-Output '--- result ---'
Get-ChildItem $OutDir -File |
    Where-Object { $_.Extension -in '.mp4', '.mkv', '.avi', '.ts' } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
