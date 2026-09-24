# Generate the "heavy" H.264 set under artifacts/media-heavy (gitignored).
#
# Recommended-format clips (H.264 High, one-second GOP) under the conditions the
# field material has and the default fixtures do not: 4K at 59.94/29.97, audio at
# 48 kHz and 44.1 kHz, a mix of resolution/fps/audio layouts in one playlist, and
# LTC on the first audio channel (LTC leaking from the media audio). Run the same
# three scenario passes on these before sending a candidate to the test machine,
# so "all green on the dev machine, fails on field material" happens less often.
# ProRes, long-GOP H.264 and AV1 are out of scope (v0.5.2 decision) and are not
# generated here.
#
# Each clip has the colour head/base/tail layout of the ltc_a/b/c fixtures so the
# scenario pixel probes can identify it (head 0..1 s when present, tail = last
# second), 20 s long, burned-in second counter outside the centre probe area,
# temporal noise and a constrained bitrate (4K: 40-50 Mbps) for a realistic decode load.
#
#   powershell -File scripts\make-heavy-media.ps1
#   powershell -File scripts\make-heavy-media.ps1 -OutDir D:\heavy -Force
#
# Requires ffmpeg (default C:\Program Files\ffmpeg\bin) and python (for the LTC
# track, scripts\make-ltc-wav.py). Keep this file ASCII-only and BOM-less.
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin',
    [string]$Python = 'python',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot 'artifacts\media-heavy'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$FfmpegExe = Join-Path $FfmpegDir 'ffmpeg.exe'
if (-not (Test-Path -LiteralPath $FfmpegExe)) { throw ('ffmpeg not found: ' + $FfmpegExe) }

function Invoke-Ffmpeg([string[]]$FfArgs, [string]$OutputPath, [string]$Name) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $messages = @(& $FfmpegExe @FfArgs 2>&1 | ForEach-Object { [string]$_ })
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

$sec = 20
$specs = @(
    @{ Name = 'heavy_a_4k5994_aac48k.mp4'; W = 3840; H = 2160; Rate = '60000/1001'; Gop = 60; Mbps = 50;
       Base = '0xFF0000'; Head = '0xFFFFFF'; Tail = '0xFFFF00'; Audio = 'tone48k' },
    @{ Name = 'heavy_b_4k2997_aac44k.mp4'; W = 3840; H = 2160; Rate = '30000/1001'; Gop = 30; Mbps = 40;
       Base = '0x00FF00'; Head = '0xFF00FF'; Tail = '0x00FFFF'; Audio = 'tone44k' },
    @{ Name = 'heavy_c_1080p60_noaudio.mp4'; W = 1920; H = 1080; Rate = '60'; Gop = 60; Mbps = 20;
       Base = '0x0000FF'; Head = ''; Tail = '0xFFA500'; Audio = 'none' },
    @{ Name = 'heavy_d_1080p30_ltc_ch1.mp4'; W = 1920; H = 1080; Rate = '30'; Gop = 30; Mbps = 15;
       Base = '0x800080'; Head = '0xFFFFFF'; Tail = '0x00FFFF'; Audio = 'ltc30' }
)

$ltcWav = Join-Path $OutDir 'ltc30_ch1.wav'

foreach ($s in $specs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 0) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        continue
    }
    $filters = 'color=c=' + $s.Base + ':s=' + $s.W + 'x' + $s.H + ':r=' + $s.Rate + ':d=' + $sec
    if ($s.Head -ne '') {
        $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Head):t=fill:enable='between(t,0,1)'"
    }
    $tailStart = $sec - 1
    $filters += ",drawbox=x=0:y=0:w=iw:h=ih:color=$($s.Tail):t=fill:enable='between(t," + $tailStart + ',' + $sec + ")'"
    # Temporal noise keeps the mean colour (the probes read the centre mean) but makes
    # every frame expensive to code, so the bitrate and decode load resemble field
    # material instead of a flat colour that compresses to almost nothing.
    $filters += ',noise=alls=24:allf=t+u'
    $filters += ",drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{eif\:floor(t)\:d}':x=20:y=20:fontsize=64:fontcolor=black"

    $inputs = @('-f', 'lavfi', '-i', $filters)
    $audioArgs = @('-an')
    switch ($s.Audio) {
        'tone48k' {
            $inputs += @('-f', 'lavfi', '-i', ('sine=frequency=1000:sample_rate=48000:duration=' + $sec))
            $audioArgs = @('-c:a', 'aac', '-b:a', '192k', '-ar', '48000', '-ac', '2')
        }
        'tone44k' {
            $inputs += @('-f', 'lavfi', '-i', ('sine=frequency=1000:sample_rate=44100:duration=' + $sec))
            $audioArgs = @('-c:a', 'aac', '-b:a', '192k', '-ar', '44100', '-ac', '2')
        }
        'ltc30' {
            & $Python (Join-Path $PSScriptRoot 'make-ltc-wav.py') $ltcWav '--fps' '30' '--seconds' "$sec" '--rate' '48000'
            if ($LASTEXITCODE -ne 0) { throw 'make-ltc-wav.py failed' }
            $inputs += @('-i', $ltcWav)
            # PCM keeps the LTC edges intact; AAC would smear them.
            $audioArgs = @('-c:a', 'pcm_s16le', '-ar', '48000', '-ac', '2')
        }
    }
    $mapArgs = @('-map', '0:v')
    if ($s.Audio -ne 'none') { $mapArgs += @('-map', '1:a') }
    if ($s.Audio -eq 'ltc30') { $path = [IO.Path]::ChangeExtension($path, '.mov') }

    $ffargs = @('-y', '-hide_banner', '-v', 'error') + $inputs + $mapArgs +
              @('-c:v', 'libx264', '-preset', 'veryfast', '-b:v', "$($s.Mbps)M", '-maxrate', "$($s.Mbps)M",
                '-bufsize', "$(2 * $s.Mbps)M", '-profile:v', 'high',
                '-g', "$($s.Gop)", '-pix_fmt', 'yuv420p') + $audioArgs + @('-shortest', $path)
    Write-Output ('making: ' + (Split-Path -Leaf $path) + ' (' + $s.W + 'x' + $s.H + '@' + $s.Rate + ', audio ' + $s.Audio + ')')
    Invoke-Ffmpeg $ffargs $path $s.Name
}
if (Test-Path -LiteralPath $ltcWav) { Remove-Item -LiteralPath $ltcWav -Force }

Write-Output '--- result ---'
Get-ChildItem $OutDir -File |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
