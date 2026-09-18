# Report the keyframe interval distribution of one or more video files.
#
# Long keyframe intervals make seeking slow: a seek must decode from the
# preceding keyframe up to the target, so the cost grows with the distance
# from that keyframe. Measured on a real 6.6s gap: 244ms right after the
# keyframe, 1258ms mid-gap, 2164ms just before the next one. On material with
# a 0.5s interval the same measurement is 306-411ms regardless of position.
#
# The judgement uses the MAXIMUM gap, not the median. Material whose median is
# inside the recommendation can still hold a 7s gap, and a seek landing there
# is slow. The head gap (0 -> first keyframe) and the tail gap (last keyframe
# -> duration) are included for the same reason.
#
#   powershell -File scripts\inspect-gop.ps1 -Path clip.mp4
#   powershell -File scripts\inspect-gop.ps1 -Path C:\media -Recurse
#   powershell -File scripts\inspect-gop.ps1 -Path a.mp4,b.mov -WarnSeconds 5 -ErrorSeconds 10
#
# Requires ffprobe on PATH, or pass -FfprobePath.
#
# NOTE: keep this file ASCII-only and BOM-less, like the other scripts in this
# repo. Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [switch]$Recurse,
    [double]$WarnSeconds = 5.0,
    [double]$ErrorSeconds = 10.0,
    [string]$FfprobePath = 'ffprobe',
    [switch]$Csv
)

$ErrorActionPreference = 'Stop'

function Resolve-Ffprobe {
    param([string]$Candidate)
    $cmd = Get-Command $Candidate -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = 'C:\Program Files\ffmpeg\bin\ffprobe.exe'
    if (Test-Path $fallback) { return $fallback }
    throw "ffprobe not found. Put it on PATH or pass -FfprobePath."
}

function Get-VideoFiles {
    param([string[]]$Inputs, [bool]$Deep)
    $exts = @('.mp4', '.mov', '.mkv', '.webm', '.m4v', '.ts', '.mxf', '.avi')
    $files = New-Object System.Collections.Generic.List[string]
    foreach ($item in $Inputs) {
        if (-not (Test-Path $item)) { Write-Warning "not found: $item"; continue }
        $entry = Get-Item -LiteralPath $item
        if ($entry.PSIsContainer) {
            $found = Get-ChildItem -LiteralPath $entry.FullName -File -Recurse:$Deep
            foreach ($f in $found) {
                if ($exts -contains $f.Extension.ToLowerInvariant()) { $files.Add($f.FullName) }
            }
        } else {
            $files.Add($entry.FullName)
        }
    }
    return $files
}

function Get-Percentile {
    param([double[]]$Sorted, [double]$Fraction)
    if ($Sorted.Count -eq 0) { return 0.0 }
    if ($Sorted.Count -eq 1) { return $Sorted[0] }
    $pos = $Fraction * ($Sorted.Count - 1)
    $lo = [math]::Floor($pos); $hi = [math]::Ceiling($pos)
    if ($lo -eq $hi) { return $Sorted[[int]$pos] }
    return $Sorted[[int]$lo] + ($pos - $lo) * ($Sorted[[int]$hi] - $Sorted[[int]$lo])
}

function Get-GopReport {
    param([string]$File, [string]$Ffprobe)

    $streamArgs = @('-v', 'error', '-select_streams', 'v:0',
        '-show_entries', 'stream=codec_name,width,height,r_frame_rate',
        '-show_entries', 'format=duration',
        '-of', 'default=noprint_wrappers=1', $File)
    $info = & $Ffprobe @streamArgs
    $meta = @{}
    foreach ($line in $info) {
        if ($line -match '^([^=]+)=(.*)$') { $meta[$Matches[1]] = $Matches[2] }
    }

    $duration = 0.0
    if ($meta.ContainsKey('duration')) { [void][double]::TryParse($meta['duration'], [ref]$duration) }

    # Packet flags carry 'K' on keyframes. This reads the container index and
    # does not decode, so it stays fast even on 4K material.
    $pktArgs = @('-v', 'error', '-select_streams', 'v:0',
        '-show_entries', 'packet=pts_time,flags', '-of', 'csv=p=0', $File)
    $packets = & $Ffprobe @pktArgs

    $keyTimes = New-Object System.Collections.Generic.List[double]
    $lastPts = 0.0
    foreach ($line in $packets) {
        $parts = $line -split ','
        if ($parts.Count -lt 2) { continue }
        $t = 0.0
        if (-not [double]::TryParse($parts[0], [ref]$t)) { continue }
        if ($t -gt $lastPts) { $lastPts = $t }
        if ($parts[1] -like '*K*') { $keyTimes.Add($t) }
    }

    if ($duration -le 0) { $duration = $lastPts }

    $result = [ordered]@{
        File        = $File
        Codec       = $meta['codec_name']
        Width       = $meta['width']
        Height      = $meta['height']
        Fps         = $meta['r_frame_rate']
        Duration    = $duration
        Keyframes   = $keyTimes.Count
        HeadGap     = 0.0
        TailGap     = 0.0
        Median      = 0.0
        P95         = 0.0
        Max         = 0.0
        Verdict     = 'OK'
        Note        = ''
    }

    if ($keyTimes.Count -eq 0) {
        $result.Verdict = 'ERROR'
        $result.Note = 'no keyframe detected'
        return [pscustomobject]$result
    }

    $sortedKeys = @($keyTimes | Sort-Object)
    $gaps = New-Object System.Collections.Generic.List[double]
    $gaps.Add($sortedKeys[0])                       # head: 0 -> first keyframe
    for ($i = 1; $i -lt $sortedKeys.Count; $i++) {
        $gaps.Add($sortedKeys[$i] - $sortedKeys[$i - 1])
    }
    $tail = $duration - $sortedKeys[$sortedKeys.Count - 1]
    if ($tail -lt 0) { $tail = 0.0 }
    $gaps.Add($tail)                                # tail: last keyframe -> end

    $sortedGaps = @($gaps | Sort-Object)
    $result.HeadGap = $sortedKeys[0]
    $result.TailGap = $tail
    $result.Median  = Get-Percentile -Sorted $sortedGaps -Fraction 0.5
    $result.P95     = Get-Percentile -Sorted $sortedGaps -Fraction 0.95
    $result.Max     = $sortedGaps[$sortedGaps.Count - 1]

    if ($result.Max -gt $ErrorSeconds) {
        $result.Verdict = 'ERROR'
        $result.Note = 'seeking into the longest gap will be slow'
    } elseif ($result.Max -gt $WarnSeconds) {
        $result.Verdict = 'WARNING'
        $result.Note = 'has a long keyframe interval somewhere'
    }
    return [pscustomobject]$result
}

$ffprobe = Resolve-Ffprobe -Candidate $FfprobePath
$targets = Get-VideoFiles -Inputs $Path -Deep:$Recurse.IsPresent
if ($targets.Count -eq 0) { throw 'no video files found' }

$reports = New-Object System.Collections.Generic.List[object]
foreach ($t in $targets) {
    try {
        $reports.Add((Get-GopReport -File $t -Ffprobe $ffprobe))
    } catch {
        Write-Warning ("failed: {0} ({1})" -f $t, $_.Exception.Message)
    }
}

if ($Csv) {
    $reports | Select-Object File, Codec, Width, Height, Fps, Duration, Keyframes,
        HeadGap, Median, P95, Max, TailGap, Verdict | ConvertTo-Csv -NoTypeInformation
    return
}

foreach ($r in $reports) {
    $name = Split-Path -Leaf $r.File
    Write-Output ("{0}" -f $name)
    Write-Output ("  codec/size/fps : {0} / {1}x{2} / {3}" -f $r.Codec, $r.Width, $r.Height, $r.Fps)
    Write-Output ("  duration/keyfr : {0:N1}s / {1}" -f $r.Duration, $r.Keyframes)
    Write-Output ("  gaps (s)       : head {0:N3} / median {1:N3} / p95 {2:N3} / max {3:N3} / tail {4:N3}" -f `
        $r.HeadGap, $r.Median, $r.P95, $r.Max, $r.TailGap)
    $verdictLine = "  seek quality   : {0} (max {1:N2}s)" -f $r.Verdict, $r.Max
    if ($r.Note) { $verdictLine += " - " + $r.Note }
    Write-Output $verdictLine
    Write-Output ''
}

$bad = @($reports | Where-Object { $_.Verdict -ne 'OK' })
Write-Output ("{0} file(s): {1} OK, {2} flagged (WARNING > {3:N0}s, ERROR > {4:N0}s on the maximum gap)" -f `
    $reports.Count, ($reports.Count - $bad.Count), $bad.Count, $WarnSeconds, $ErrorSeconds)
