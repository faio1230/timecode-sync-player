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
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\media'),
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

if (Test-Path $FfmpegDir) { $env:PATH = "$env:PATH;$FfmpegDir" }
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    throw 'ffmpeg not found on PATH (pass -FfmpegDir)'
}

New-Item -ItemType Directory -Force $OutDir | Out-Null

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
    if ((Test-Path $path) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $src = 'testsrc2=size=' + $s.W + 'x' + $s.H + ':rate=' + $s.Fps + ':duration=' + $s.Sec
    $ffargs = @('-y', '-hide_banner', '-v', 'error', '-f', 'lavfi', '-i', $src) +
              $venc + @('-g', "$($s.Fps)", '-pix_fmt', 'yuv420p') + $s.Extra + @($path)
    Write-Output ('making: ' + $s.Name + ' (' + $s.W + 'x' + $s.H + '@' + $s.Fps + ', ' + $s.Sec + 's)')
    & ffmpeg @ffargs
    if ($LASTEXITCODE -ne 0) { throw ('ffmpeg failed: ' + $s.Name) }
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
    if ((Test-Path $path) -and -not $Force) {
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
    & ffmpeg @ffargs
    if ($LASTEXITCODE -ne 0) { throw ('ffmpeg failed: ' + $s.Name) }
}

Write-Output '--- result ---'
Get-ChildItem $OutDir -File |
    Where-Object { $_.Extension -in '.mp4', '.mkv', '.avi', '.ts' } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
