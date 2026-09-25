# Build a project file for the LTC scenario E2E from a folder of real media.
#
# The scenario tests never embed media file names: tracks are referred to as
# M1..M7 by position, and this script writes those symbols as the track names.
#
#   powershell -File scripts\make-ltc-scenario-project.ps1 -MediaDir D:\media -Out C:\reports\ltc-scenario.tsp
#
# -Out is written wherever it points (the runner puts it under its report
# directory). The media folder is user-managed and read-only on the test machine,
# so the runner first creates one hard link per media file under
# <ReportDir>\media and passes that folder as -MediaDir. Tracks are then stored as
# paths relative to the .tsp directory (e.g. media\clip.mp4): ProjectSerializer
# only resolves paths inside the project directory. MediaDir must therefore live
# inside the -Out directory. The runner deletes the generated project and the
# links after the run (-KeepProject keeps them).
#
# Symbols: the videos (mp4 / mov / mkv / mxf / ts) in the folder are numbered
# M1, M2, ... in name order. Without -Media the first -Tracks of them are used.
# With -Media (e.g. -Media M1,M3,M5) exactly those symbols are used, in the given
# order, and each track is named by its symbol, so a symbol always means the same
# file of the folder. Every track uses MediaIn = MediaInOffsetSeconds (default 0) and
# MediaOut = MediaIn + min(SegmentSeconds, duration - MediaIn), so material shorter
# than MediaIn + SegmentSeconds is used up to its end. -MediaInOffsetSeconds skips an
# intro (for example a black first frame that the reference-image checks cannot tell
# apart from a black gap); a video not longer than the offset stops with an error. The timeline starts with
# a 5 s offset and keeps a GapSeconds gap after every track (default 5 s; 0 makes
# the tracks adjacent).
#
# NOTE: keep this file ASCII-only and BOM-less, like the other scripts in this
# repo. Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page, so
# non-ASCII comments break parsing.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MediaDir,
    [Parameter(Mandatory = $true)]
    [string]$Out,
    [int]$Tracks = 3,
    [string[]]$Media = @(),
    [double]$SegmentSeconds = 20,
    # Per-track used length, in track order ("700,170,170"). Empty = SegmentSeconds for every
    # track. Needed when one track is long (a 10 minute follow) and the others are short: one
    # shared length would push the short tracks' reference capture to their very end.
    [string]$TrackSegmentSeconds = '',
    [double]$MediaInOffsetSeconds = 0,
    # Gap between tracks on the timeline, in seconds (default 5). Zero makes the
    # tracks adjacent, which is what a show without black between clips looks like:
    # the switch then has to replace the previous picture instead of leaving black.
    [double]$GapSeconds = 5,
    [string]$FfmpegDir = 'C:\Program Files\ffmpeg\bin'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $MediaDir -PathType Container)) {
    throw 'MediaDir does not exist'
}
$MediaDir = (Resolve-Path -LiteralPath $MediaDir).Path
if ($Tracks -lt 1 -or $Tracks -gt 7) {
    throw 'Tracks must be 1..7 (track symbols M1..M7)'
}
if ($SegmentSeconds -le 0) {
    throw 'SegmentSeconds must be positive'
}
if ($MediaInOffsetSeconds -lt 0 -or [double]::IsNaN($MediaInOffsetSeconds) -or [double]::IsInfinity($MediaInOffsetSeconds)) {
    throw 'MediaInOffsetSeconds must be zero or positive'
}

$Out = [System.IO.Path]::GetFullPath($Out)
if (Test-Path -LiteralPath $Out -PathType Container) {
    throw 'Out must be a file path'
}
$outDir = [System.IO.Path]::GetDirectoryName($Out)
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$outDir = [System.IO.Path]::GetFullPath($outDir)
$outDirPrefix = $outDir.TrimEnd([char]'\') + '\'
if (-not $MediaDir.StartsWith($outDirPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw ('MediaDir must be inside the directory of -Out (the runner creates a junction like ' +
        '<ReportDir>\media -> the real media folder and passes the junction path here). ' +
        'MediaDir=' + $MediaDir + ' OutDir=' + $outDir)
}

if (Test-Path $FfmpegDir) { $env:PATH = "$env:PATH;$FfmpegDir" }
$ffprobe = Get-Command ffprobe -ErrorAction SilentlyContinue
if (-not $ffprobe) {
    throw 'ffprobe not found on PATH (pass -FfmpegDir)'
}

$extensions = @('.mp4', '.mov', '.mkv', '.mxf', '.ts')
$allFiles = @(Get-ChildItem -LiteralPath $MediaDir -File |
    Where-Object { $extensions -contains $_.Extension.ToLowerInvariant() } |
    Sort-Object -Property Name)

# Selection: list of @{ Symbol; File }. Symbols never name the files.
$selection = @()
# -Media accepts "M1,M3,M5" (from -File) and M1,M3,M5 (an array inside PowerShell).
$Media = @($Media -join ',')[0]
if ([string]::IsNullOrWhiteSpace($Media)) {
    if ($allFiles.Count -lt $Tracks) {
        throw "MediaDir contains fewer than $Tracks video files (mp4/mov/mkv/mxf/ts)"
    }
    for ($i = 0; $i -lt $Tracks; $i++) {
        $selection += @{ Symbol = ('M' + ($i + 1)); File = $allFiles[$i] }
    }
} else {
    $symbols = @($Media -split '[,\s]+' | Where-Object { $_ })
    if ($symbols.Count -lt 1 -or $symbols.Count -gt 7) {
        throw 'Media must list 1..7 symbols (e.g. M1,M3,M5)'
    }
    $seen = @{}
    foreach ($symbol in $symbols) {
        if ($symbol -notmatch '^[Mm](\d+)$') { throw ('Media symbol must look like M1: ' + $symbol) }
        $number = [int]$matches[1]
        if ($number -lt 1 -or $number -gt $allFiles.Count) {
            throw ('Media symbol ' + $symbol + ' is out of range (the folder has ' + $allFiles.Count + ' videos)')
        }
        $normalized = 'M' + $number
        if ($seen.ContainsKey($normalized)) { throw ('Media symbol listed twice: ' + $normalized) }
        $seen[$normalized] = $true
        $selection += @{ Symbol = $normalized; File = $allFiles[$number - 1] }
    }
}

$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$perTrackSegments = @()
if (-not [string]::IsNullOrWhiteSpace($TrackSegmentSeconds)) {
    foreach ($piece in ($TrackSegmentSeconds -split '[,\s]+' | Where-Object { $_ })) {
        $value = 0.0
        if (-not [double]::TryParse($piece, [Globalization.NumberStyles]::Float, $invariant, [ref]$value) -or $value -le 0) {
            throw ('TrackSegmentSeconds must list positive numbers: ' + $piece)
        }
        $perTrackSegments += $value
    }
}
if ($GapSeconds -lt 0 -or [double]::IsNaN($GapSeconds) -or [double]::IsInfinity($GapSeconds)) {
    throw 'GapSeconds must be zero or positive'
}
$gap = $GapSeconds
$offset = 5.0
$trackList = @()
$index = 0
foreach ($selected in $selection) {
    $index++
    $file = $selected.File
    $symbol = $selected.Symbol
    $durationText = & $ffprobe.Source -v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "ffprobe failed for track $symbol" }
    $duration = [double]::Parse(($durationText | Select-Object -First 1).Trim(), $invariant)
    if ($duration -le 0) { throw "ffprobe returned no duration for track $symbol" }
    if ($duration -le $MediaInOffsetSeconds) {
        throw ('track ' + $symbol + ' is ' + $duration.ToString('F3', $invariant) +
            ' s long, not longer than MediaInOffsetSeconds ' + $MediaInOffsetSeconds.ToString('F3', $invariant) + ' s')
    }
    $mediaIn = $MediaInOffsetSeconds
    $segmentForTrack = $SegmentSeconds
    if ($perTrackSegments.Count -gt 0) {
        $segmentForTrack = $perTrackSegments[[Math]::Min($index - 1, $perTrackSegments.Count - 1)]
    }
    $used = [Math]::Min($segmentForTrack, $duration - $mediaIn)

    $trackList += [ordered]@{
        id             = ('aaaaaaaa-0000-0000-0000-{0:d12}' -f $index)
        filePath       = $file.FullName.Substring($outDirPrefix.Length)
        name           = $symbol
        mediaIn        = ([TimeSpan]::FromSeconds($mediaIn)).ToString('c')
        mediaOut       = ([TimeSpan]::FromSeconds($mediaIn + $used)).ToString('c')
        timelineOffset = ([TimeSpan]::FromSeconds($offset)).ToString('c')
        mediaDuration  = ([TimeSpan]::FromSeconds($duration)).ToString('c')
        syncOffset     = ([TimeSpan]::Zero).ToString('c')
        isEnabled      = $true
    }
    Write-Output ($symbol + ': duration ' + $duration.ToString('F3', $invariant) +
        's, in ' + $mediaIn.ToString('F3', $invariant) + 's, used ' + $used.ToString('F3', $invariant) + 's, offset ' + ([TimeSpan]::FromSeconds($offset)).ToString('c'))
    $offset += $used + $gap
}

$project = [ordered]@{
    version     = 1
    syncMode    = 0   # Single
    gapBehavior = 1   # Black (GapBehavior.Freeze is 0, Black is 1)
    canvas      = [ordered]@{
        width      = 1920
        height     = 1080
        defaultFit = 'fit-height'
    }
    tracks      = $trackList
}

$json = $project | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($Out, $json, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ('project written: ' + [System.IO.Path]::GetFileName($Out) + ' (' + $trackList.Count + ' tracks)')
