#requires -Version 7.0
# Shared ffmpeg resolution and version record for the scripts that make test media
# (make-e2e-media.ps1, make-heavy-media.ps1, capture-setup.ps1). The C# fixtures use
# the same order (tests\TimecodeSyncPlayer.Tests\Helpers\FfmpegTool.cs).
#
# Order (v0.6.0 stage 5b, one rule for every script and test):
#   1. TCS_FFMPEG  - full path of ffmpeg.exe
#   2. -FfmpegDir  - folder that holds ffmpeg.exe (the script argument)
#   3. PATH
# ffprobe: TCS_FFPROBE, then ffprobe.exe next to the resolved ffmpeg, then PATH.
# Resolve-TcsFfprobe walks the same levels for a script that only needs ffprobe
# (TCS_FFPROBE, next to TCS_FFMPEG, -FfmpegDir, PATH) and does not need ffmpeg.
#
# Why: the dev machine and the test machine had two ffmpeg builds each, and the
# scripts (-FfmpegDir first) and the tests (PATH) picked opposite ones, so the same
# test ran on 4.2.3 on one machine and 8.0.1 on the other. The build used is now
# printed as the first log line and written next to the media (ffmpeg-version.txt).

Set-StrictMode -Version 3.0

$script:MinimumMajor = 6

function Resolve-TcsFfmpeg {
    [CmdletBinding()]
    param([string]$FfmpegDir = '')

    $exe = ''
    $source = ''
    $fromEnv = [Environment]::GetEnvironmentVariable('TCS_FFMPEG')
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        if (-not (Test-Path -LiteralPath $fromEnv -PathType Leaf)) {
            throw ('TCS_FFMPEG does not point to a file: ' + $fromEnv)
        }
        $exe = (Get-Item -LiteralPath $fromEnv).FullName
        $source = 'env:TCS_FFMPEG'
    } elseif (-not [string]::IsNullOrWhiteSpace($FfmpegDir) -and
        (Test-Path -LiteralPath (Join-Path $FfmpegDir 'ffmpeg.exe') -PathType Leaf)) {
        $exe = (Get-Item -LiteralPath (Join-Path $FfmpegDir 'ffmpeg.exe')).FullName
        $source = 'arg:FfmpegDir'
    } else {
        $onPath = Get-Command ffmpeg -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($onPath) {
            $exe = $onPath.Source
            $source = 'PATH'
        }
    }
    if ([string]::IsNullOrWhiteSpace($exe)) {
        throw 'ffmpeg not found (set TCS_FFMPEG to ffmpeg.exe, pass -FfmpegDir, or put ffmpeg on PATH)'
    }

    $probe = ''
    $probeSource = ''
    $probeFromEnv = [Environment]::GetEnvironmentVariable('TCS_FFPROBE')
    $probeNext = Join-Path (Split-Path -Parent $exe) 'ffprobe.exe'
    if (-not [string]::IsNullOrWhiteSpace($probeFromEnv)) {
        if (-not (Test-Path -LiteralPath $probeFromEnv -PathType Leaf)) {
            throw ('TCS_FFPROBE does not point to a file: ' + $probeFromEnv)
        }
        $probe = (Get-Item -LiteralPath $probeFromEnv).FullName
        $probeSource = 'env:TCS_FFPROBE'
    } elseif (Test-Path -LiteralPath $probeNext -PathType Leaf) {
        $probe = (Get-Item -LiteralPath $probeNext).FullName
        $probeSource = 'next-to-ffmpeg'
    } else {
        $probeOnPath = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($probeOnPath) {
            $probe = $probeOnPath.Source
            $probeSource = 'PATH'
        }
    }

    $all = @(& $exe -version 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw ('ffmpeg -version failed: ' + $exe) }
    $versionLine = ($all | Select-Object -First 1)
    $major = Get-TcsFfmpegMajor -VersionLines $all

    [pscustomobject]@{
        Ffmpeg        = $exe
        Source        = $source
        Ffprobe       = $probe
        FfprobeSource = $probeSource
        VersionLine   = [string]$versionLine
        Major         = $major
    }
}

# ffprobe alone, for a script that reads durations and never runs ffmpeg
# (make-ltc-scenario-project.ps1). Same levels as Resolve-TcsFfmpeg, without
# requiring ffmpeg itself:
#   0. TCS_FFPROBE              - full path of ffprobe.exe (the override of this module)
#   1. next to TCS_FFMPEG       - ffprobe.exe in the folder of the ffmpeg.exe it names
#   2. -FfmpegDir\ffprobe.exe
#   3. PATH
# Returns Ffprobe = '' when none is found; Searched lists where it looked (for the
# error message). Never changes PATH (#43: the runner used to keep the folder that
# the project script appended to PATH, and the app then saw ffprobe).
function Resolve-TcsFfprobe {
    [CmdletBinding()]
    param([string]$FfmpegDir = '')

    $searched = New-Object System.Collections.Generic.List[string]
    $fromEnv = [Environment]::GetEnvironmentVariable('TCS_FFPROBE')
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) {
        if (-not (Test-Path -LiteralPath $fromEnv -PathType Leaf)) {
            throw ('TCS_FFPROBE does not point to a file: ' + $fromEnv)
        }
        return [pscustomobject]@{ Ffprobe = (Get-Item -LiteralPath $fromEnv).FullName; Source = 'env:TCS_FFPROBE'; Searched = @('TCS_FFPROBE') }
    }
    $searched.Add('TCS_FFPROBE (unset)')

    $ffmpegFromEnv = [Environment]::GetEnvironmentVariable('TCS_FFMPEG')
    if (-not [string]::IsNullOrWhiteSpace($ffmpegFromEnv)) {
        $next = Join-Path (Split-Path -Parent $ffmpegFromEnv) 'ffprobe.exe'
        if (Test-Path -LiteralPath $next -PathType Leaf) {
            return [pscustomobject]@{ Ffprobe = (Get-Item -LiteralPath $next).FullName; Source = 'next-to-TCS_FFMPEG'; Searched = @($searched) + $next }
        }
        $searched.Add($next)
    } else {
        $searched.Add('TCS_FFMPEG (unset)')
    }

    if (-not [string]::IsNullOrWhiteSpace($FfmpegDir)) {
        $inDir = Join-Path $FfmpegDir 'ffprobe.exe'
        if (Test-Path -LiteralPath $inDir -PathType Leaf) {
            return [pscustomobject]@{ Ffprobe = (Get-Item -LiteralPath $inDir).FullName; Source = 'arg:FfmpegDir'; Searched = @($searched) + $inDir }
        }
        $searched.Add($inDir)
    } else {
        $searched.Add('-FfmpegDir (empty)')
    }

    $onPath = Get-Command ffprobe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    $searched.Add('PATH')
    if ($onPath) {
        return [pscustomobject]@{ Ffprobe = $onPath.Source; Source = 'PATH'; Searched = @($searched) }
    }
    return [pscustomobject]@{ Ffprobe = ''; Source = ''; Searched = @($searched) }
}

# Major release of an ffmpeg build. Release builds say "ffmpeg version 4.2.3" or
# "n5.0"; git builds say "N-109850-g..." and carry no release number, so libavcodec
# decides for them (58 = 4.x, 59 = 5.x, 60 = 6.x, 61 = 7.x, 62 = 8.x). -1 = unknown.
function Get-TcsFfmpegMajor {
    param([string[]]$VersionLines)
    $first = [string]($VersionLines | Select-Object -First 1)
    if ($first -match '^ffmpeg version n?(\d+)\.') { return [int]$matches[1] }
    foreach ($line in $VersionLines) {
        if ($line -match '^\s*libavcodec\s+(\d+)\.') {
            $lavc = [int]$matches[1]
            if ($lavc -ge 58) { return $lavc - 54 }
        }
    }
    return -1
}

# Log lines to print first: the version line, then where it came from.
function Get-TcsFfmpegLogLines {
    param([Parameter(Mandatory = $true)]$Ffmpeg)
    @(
        ('ffmpeg-version: ' + $Ffmpeg.VersionLine + ' (major ' + $Ffmpeg.Major + ')'),
        ('ffmpeg: ' + $Ffmpeg.Ffmpeg + ' (' + $Ffmpeg.Source + ')'),
        ('ffprobe: ' + $(if ($Ffmpeg.Ffprobe) { $Ffmpeg.Ffprobe + ' (' + $Ffmpeg.FfprobeSource + ')' } else { '(not found)' }))
    )
}

# One warning line when a NEW file is made with an ffmpeg older than 6. Reusing an
# existing file never warns (the file was not made by this ffmpeg).
function Get-TcsFfmpegOldVersionWarning {
    param([Parameter(Mandatory = $true)]$Ffmpeg)
    if ($Ffmpeg.Major -ge $script:MinimumMajor) { return $null }
    $shown = if ($Ffmpeg.Major -lt 0) { 'unknown' } else { [string]$Ffmpeg.Major }
    return ('ffmpeg major ' + $shown + ' is older than ' + $script:MinimumMajor +
        ' and made new media (' + $Ffmpeg.VersionLine + '). Point TCS_FFMPEG at a build 6 or later.')
}

# ffmpeg-version.txt in the media folder: one line per file, "<name> | <version line>".
# New files get the ffmpeg that made them. Files already in the folder without a
# line get "unknown (made before the version record)" once, so an older fixture is
# never attributed to the current ffmpeg. Existing lines are kept as they are.
function Update-TcsFfmpegSidecar {
    param(
        [Parameter(Mandatory = $true)][string]$Directory,
        [Parameter(Mandatory = $true)]$Ffmpeg,
        [string[]]$MadeNames = @(),
        [string[]]$ExistingNames = @()
    )
    $path = Join-Path $Directory 'ffmpeg-version.txt'
    $entries = [ordered]@{}
    if (Test-Path -LiteralPath $path) {
        foreach ($line in [IO.File]::ReadAllLines($path, [Text.Encoding]::UTF8)) {
            if ($line.StartsWith('#') -or -not $line.Contains(' | ')) { continue }
            $index = $line.IndexOf(' | ')
            $entries[$line.Substring(0, $index)] = $line.Substring($index + 3)
        }
    }
    foreach ($name in $ExistingNames) {
        if (-not $entries.Contains($name)) { $entries[$name] = 'unknown (made before the version record)' }
    }
    $stamp = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    foreach ($name in $MadeNames) {
        $entries[$name] = $Ffmpeg.VersionLine + ' | made ' + $stamp
    }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add('# ffmpeg that made each file (scripts\TcsFfmpeg.psm1). <name> | <ffmpeg -version first line> | made <UTC>')
    foreach ($key in @($entries.Keys | Sort-Object)) { $lines.Add($key + ' | ' + $entries[$key]) }
    [IO.File]::WriteAllLines($path, $lines, (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

# Colour tags of the first video stream, read with ffprobe.
function Get-TcsColorTags {
    param(
        [Parameter(Mandatory = $true)]$Ffmpeg,
        [Parameter(Mandatory = $true)][string]$Path
    )
    if (-not $Ffmpeg.Ffprobe) { throw 'ffprobe not found (set TCS_FFPROBE or put ffprobe next to ffmpeg)' }
    $out = @(& $Ffmpeg.Ffprobe -v error -select_streams v:0 `
        -show_entries stream=color_primaries,color_transfer,color_space `
        -of default=noprint_wrappers=1 $Path 2>&1 | ForEach-Object { [string]$_ })
    if ($LASTEXITCODE -ne 0) { throw ('ffprobe failed: ' + $Path + ' ' + ($out -join ' ')) }
    $tags = [ordered]@{ color_primaries = ''; color_transfer = ''; color_space = '' }
    foreach ($line in $out) {
        if ($line -match '^(color_primaries|color_transfer|color_space)=(.*)$') { $tags[$matches[1]] = $matches[2].Trim() }
    }
    return [pscustomobject]$tags
}

# Stops (throws) unless all three tags are bt709 (Expect bt709) or none of them is
# (Expect none: the untagged fallback clip).
function Assert-TcsColorTags {
    param(
        [Parameter(Mandatory = $true)]$Ffmpeg,
        [Parameter(Mandatory = $true)][string]$Path,
        [ValidateSet('bt709', 'none')][string]$Expect = 'bt709'
    )
    $tags = Get-TcsColorTags -Ffmpeg $Ffmpeg -Path $Path
    $text = 'primaries=' + $tags.color_primaries + ' transfer=' + $tags.color_transfer + ' space=' + $tags.color_space
    $values = @($tags.color_primaries, $tags.color_transfer, $tags.color_space)
    if ($Expect -eq 'bt709') {
        $ok = @($values | Where-Object { $_ -ne 'bt709' }).Count -eq 0
    } else {
        $ok = @($values | Where-Object { $_ -eq 'bt709' }).Count -eq 0
    }
    if (-not $ok) {
        throw ('colour tags check failed (expected ' + $Expect + '): ' + (Split-Path -Leaf $Path) + ' ' + $text)
    }
    return ('colour-tags ok (' + $Expect + '): ' + (Split-Path -Leaf $Path) + ' ' + $text)
}

Export-ModuleMember -Function Resolve-TcsFfmpeg, Resolve-TcsFfprobe, Get-TcsFfmpegMajor, Get-TcsFfmpegLogLines,
    Get-TcsFfmpegOldVersionWarning, Update-TcsFfmpegSidecar, Get-TcsColorTags, Assert-TcsColorTags
