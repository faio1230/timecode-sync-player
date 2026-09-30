#requires -Version 7.0
# Generate the "heavy" H.264 set under artifacts/media-heavy (gitignored).
#
# Recommended-format clips (H.264 High, one-second GOP) under the conditions the
# field material has and the default fixtures do not: 4K at 59.94/29.97, audio at
# 48 kHz and 44.1 kHz, a mix of resolution/fps/audio layouts in one playlist, and
# LTC on the first audio channel (LTC leaking from the media audio). Run the same
# three scenario passes on these before sending a candidate to the test machine,
# so "all green on the dev machine, fails on field material" happens less often.
# Long-GOP H.264 and AV1 are out of scope (v0.5.2 decision) and are not generated
# here. ProRes came back in v0.6.0 (GPU decode, prores-gpu): heavy_e/f are ProRes
# 422 HQ at 59.94 with the three bt709 colour tags and a colr atom, heavy_g is the
# same 4K clip without tags (only for the untagged fallback check). The tags of every
# ProRes clip made here are read back with ffprobe and a mismatch stops the script.
#
# Each clip has the colour head/base/tail layout of the ltc_a/b/c fixtures so the
# scenario pixel probes can identify it (head 0..1 s when present, tail = last
# second), 20 s long, burned-in second counter outside the centre probe area,
# temporal noise and a constrained bitrate (4K: 40-50 Mbps) for a realistic decode load.
#
#   pwsh -File scripts\make-heavy-media.ps1
#   pwsh -File scripts\make-heavy-media.ps1 -OutDir D:\heavy -Force
#   pwsh -File scripts\make-heavy-media.ps1 -Only heavy_e*,heavy_f*,heavy_g*   # the ProRes clips only
#
# Requires ffmpeg (TCS_FFMPEG = full path of ffmpeg.exe, else -FfmpegDir, default
# C:\Program Files\ffmpeg\bin, else PATH; scripts\TcsFfmpeg.psm1), ffprobe next to it
# or TCS_FFPROBE, and python (for the LTC track, scripts\make-ltc-wav.py). The first
# output line is the ffmpeg version; ffmpeg-version.txt in OutDir records the build
# that made each file. Existing files are reused and never re-encoded (without -Force).
[CmdletBinding()]
param(
    [string]$OutDir = '',
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin',
    [string]$Python = 'python',
    [switch]$Force,
    # Wildcards on the clip names; empty = every clip.
    [string[]]$Only = @(),
    # Makes the tagged ProRes clips WITHOUT their colour tags, to see the tag check stop
    # the script. Never for real media.
    [switch]$OmitColorTags
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $PSScriptRoot 'TcsFfmpeg.psm1') -Force
$Ffmpeg = Resolve-TcsFfmpeg -FfmpegDir $FfmpegDir
$FfmpegExe = $Ffmpeg.Ffmpeg
Get-TcsFfmpegLogLines $Ffmpeg | ForEach-Object { Write-Output $_ }
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repoRoot 'artifacts\media-heavy'
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$madeNames = New-Object System.Collections.Generic.List[string]
$existingNames = New-Object System.Collections.Generic.List[string]

function Invoke-Ffmpeg([string[]]$FfArgs, [string]$OutputPath, [string]$Name) {
    # The < 6 warning is for new files only, once per run.
    if ($madeNames.Count -eq 0) {
        $warning = Get-TcsFfmpegOldVersionWarning $Ffmpeg
        if ($warning) { Write-Warning $warning }
    }
    $messages = @(& $FfmpegExe @FfArgs 2>&1 | ForEach-Object { [string]$_ })
    $code = $LASTEXITCODE
    if ($code -ne 0) {
        $messages | Select-Object -Last 20 | ForEach-Object { Write-Output ('ffmpeg: ' + $_) }
        if (Test-Path -LiteralPath $OutputPath) { Remove-Item -LiteralPath $OutputPath -Force }
        throw ('ffmpeg failed: ' + $Name + ' (exit ' + $code + ')')
    }
    $madeNames.Add((Split-Path -Leaf $OutputPath))
}

$sec = 20
$specs = @(
    @{ Name = 'heavy_a_4k5994_aac48k.mp4'; W = 3840; H = 2160; Rate = '60000/1001'; Gop = 60; Mbps = 50;
       Base = '0xFF0000'; Head = '0xFFFFFF'; Tail = '0xFFFF00'; Audio = 'tone48k' },
    @{ Name = 'heavy_b_4k2997_aac44k.mp4'; W = 3840; H = 2160; Rate = '30000/1001'; Gop = 30; Mbps = 40;
       Base = '0x00FF00'; Head = '0xFF00FF'; Tail = '0x00FFFF'; Audio = 'tone44k' },
    @{ Name = 'heavy_c_1080p60_noaudio.mp4'; W = 1920; H = 1080; Rate = '60'; Gop = 60; Mbps = 20;
       Base = '0x0000FF'; Head = ''; Tail = '0xFFA500'; Audio = 'none' },
    # Written as .mov (PCM audio). The name said .mp4 before, so the "exists" check
    # never matched and the clip was re-encoded on every run.
    @{ Name = 'heavy_d_1080p30_ltc_ch1.mov'; W = 1920; H = 1080; Rate = '30'; Gop = 30; Mbps = 15;
       Base = '0x800080'; Head = '0xFFFFFF'; Tail = '0x00FFFF'; Audio = 'ltc30' },
    # v0.6.0: ProRes 422 HQ (prores_ks profile 3, yuv422p10le), intra only, no audio.
    # Colour = 'bt709' writes the three tags and the colr atom; 'none' writes neither.
    @{ Name = 'heavy_e_4k5994_prores422hq.mov'; W = 3840; H = 2160; Rate = '60000/1001';
       Base = '0xFF0000'; Head = '0xFFFFFF'; Tail = '0xFFFF00'; Audio = 'none'; Codec = 'prores'; Colour = 'bt709' },
    @{ Name = 'heavy_f_1080p5994_prores422hq.mov'; W = 1920; H = 1080; Rate = '60000/1001';
       Base = '0x00FF00'; Head = '0xFF00FF'; Tail = '0x00FFFF'; Audio = 'none'; Codec = 'prores'; Colour = 'bt709' },
    @{ Name = 'heavy_g_4k5994_prores422hq_untagged.mov'; W = 3840; H = 2160; Rate = '60000/1001';
       Base = '0x0000FF'; Head = ''; Tail = '0xFFA500'; Audio = 'none'; Codec = 'prores'; Colour = 'none' }
)
# pwsh -File passes "-Only a,b" as one string; split it here.
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim("'", '"') } | Where-Object { $_ })
if ($Only.Count -gt 0) {
    $specs = @($specs | Where-Object { $n = $_.Name; @($Only | Where-Object { $n -like $_ }).Count -gt 0 })
    if ($specs.Count -eq 0) { throw ('no clip matches -Only ' + ($Only -join ',')) }
}

$ltcWav = Join-Path $OutDir 'ltc30_ch1.wav'

foreach ($s in $specs) {
    $path = Join-Path $OutDir $s.Name
    if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Length -gt 0) -and -not $Force) {
        Write-Output ('skip (exists): ' + $s.Name)
        $existingNames.Add($s.Name)
        continue
    }
    $isProRes = $s.ContainsKey('Codec') -and $s.Codec -eq 'prores'
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

    if ($isProRes) {
        $videoArgs = @('-c:v', 'prores_ks', '-profile:v', '3', '-pix_fmt', 'yuv422p10le')
        if ($s.Colour -eq 'bt709' -and -not $OmitColorTags) {
            $videoArgs += @('-color_primaries', 'bt709', '-color_trc', 'bt709', '-colorspace', 'bt709',
                '-movflags', '+write_colr')
        }
        $colourText = if ($OmitColorTags -and $s.Colour -eq 'bt709') { 'bt709 OMITTED (-OmitColorTags)' } else { $s.Colour }
        $codecText = 'ProRes 422 HQ, colour ' + $colourText
    } else {
        $videoArgs = @('-c:v', 'libx264', '-preset', 'veryfast', '-b:v', "$($s.Mbps)M", '-maxrate', "$($s.Mbps)M",
            '-bufsize', "$(2 * $s.Mbps)M", '-profile:v', 'high', '-g', "$($s.Gop)", '-pix_fmt', 'yuv420p')
        $codecText = 'H.264'
    }
    $ffargs = @('-y', '-hide_banner', '-v', 'error') + $inputs + $mapArgs + $videoArgs + $audioArgs + @('-shortest', $path)
    Write-Output ('making: ' + $s.Name + ' (' + $s.W + 'x' + $s.H + '@' + $s.Rate + ', ' + $codecText + ', audio ' + $s.Audio + ')')
    Invoke-Ffmpeg $ffargs $path $s.Name
    if ($isProRes) {
        # Read the tags back with ffprobe. A mismatch deletes the clip (a later run would
        # otherwise reuse it as "exists") and stops the script.
        try {
            Write-Output (Assert-TcsColorTags -Ffmpeg $Ffmpeg -Path $path -Expect $s.Colour)
        } catch {
            $madeNames.Remove($s.Name) | Out-Null
            Remove-Item -LiteralPath $path -Force
            throw
        }
    }
}
if (Test-Path -LiteralPath $ltcWav) { Remove-Item -LiteralPath $ltcWav -Force }
$sidecar = Update-TcsFfmpegSidecar -Directory $OutDir -Ffmpeg $Ffmpeg `
    -MadeNames $madeNames.ToArray() -ExistingNames $existingNames.ToArray()
Write-Output ('ffmpeg-version sidecar: ' + $sidecar + ' (made ' + $madeNames.Count + ', reused ' + $existingNames.Count + ')')

Write-Output '--- result ---'
Get-ChildItem $OutDir -File |
    Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } |
    Format-Table -AutoSize
