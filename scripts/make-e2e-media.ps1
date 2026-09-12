# E2E 用のテスト素材を生成する（artifacts/media、gitignore 対象）。
#
# GStreamerBackendE2ETests などが要求する固定名のファイルを、どの worktree でも
# 同じ内容で作り直せるようにする。素材が無いとテストは Skip されるため、
# 既定切替（v0.4）の「全 E2E 成功」にはこの素材が要る。
#
#   powershell -File scripts\make-e2e-media.ps1
#   powershell -File scripts\make-e2e-media.ps1 -OutDir D:\media -Force
#
# 必要: ffmpeg（既定 C:\Program Files\ffmpeg\bin）。エンコーダは h264_nvenc があれば使い、
# 無ければ libx264 に退避する。
[CmdletBinding()]
param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\media'),
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

if (Test-Path $FfmpegDir) { $env:PATH = "$env:PATH;$FfmpegDir" }
$ffmpeg = (Get-Command ffmpeg -ErrorAction SilentlyContinue)
if (-not $ffmpeg) { throw "ffmpeg が PATH に無い（-FfmpegDir で指定する）" }

New-Item -ItemType Directory -Force $OutDir | Out-Null

$encoders = & ffmpeg -hide_banner -encoders 2>&1
$useNvenc = ($encoders | Select-String -SimpleMatch 'h264_nvenc').Count -gt 0
if ($useNvenc) {
    $venc = @('-c:v', 'h264_nvenc', '-preset', 'p4', '-rc', 'vbr', '-b:v', '12M')
} else {
    $venc = @('-c:v', 'libx264', '-preset', 'veryfast', '-crf', '20')
}
Write-Output ("encoder: {0}" -f $(if ($useNvenc) { 'h264_nvenc' } else { 'libx264' }))

# name, width, height, fps, seconds, extra args
# GOP は 1 秒（-g = fps）。TS は IDR ごとに SPS/PPS を入れる（放送 TS に合わせる）。
$specs = @(
    @{ Name = 'test_1080p60.mp4';      W = 1920; H = 1080; Fps = 60; Sec = 30; Extra = @() },
    @{ Name = 'test_720p60_long.mp4';  W = 1280; H = 720;  Fps = 60; Sec = 45; Extra = @() },
    @{ Name = 'test_720p25.mkv';       W = 1280; H = 720;  Fps = 25; Sec = 20; Extra = @() },
    @{ Name = 'test_720p25.avi';       W = 1280; H = 720;  Fps = 25; Sec = 20; Extra = @() },
    @{ Name = 'test_720p50.ts';        W = 1280; H = 720;  Fps = 50; Sec = 20; Extra = @('-bsf:v', 'dump_extra=freq=keyframe') }
)

foreach ($s in $specs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Path $path) -and -not $Force) {
        Write-Output ("skip (exists): {0}" -f $s.Name)
        continue
    }
    $src = "testsrc2=size=$($s.W)x$($s.H):rate=$($s.Fps):duration=$($s.Sec)"
    $args = @('-y', '-hide_banner', '-v', 'error', '-f', 'lavfi', '-i', $src) +
            $venc + @('-g', "$($s.Fps)", '-pix_fmt', 'yuv420p') + $s.Extra + @($path)
    Write-Output ("making: {0} ({1}x{2}@{3}, {4}s)" -f $s.Name, $s.W, $s.H, $s.Fps, $s.Sec)
    & ffmpeg @args
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg が失敗: $($s.Name)" }
}

Write-Output '--- result ---'
Get-ChildItem $OutDir -File |
    Where-Object { $_.Extension -in '.mp4', '.mkv', '.avi', '.ts' } |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
