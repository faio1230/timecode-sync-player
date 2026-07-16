[CmdletBinding()]
param(
    [string]$AppPath,
    [string]$FfmpegPath = "ffmpeg",
    [ValidateRange(20, 30)]
    [int]$DurationSeconds = 25
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($AppPath)) {
    $AppPath = Join-Path $projectRoot "src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe"
}
$AppPath = [System.IO.Path]::GetFullPath($AppPath)
if (-not (Test-Path -LiteralPath $AppPath -PathType Leaf)) {
    throw "TimecodeSyncPlayer.exe was not found: $AppPath`nBuild the Debug configuration first or pass -AppPath."
}

$ffmpegCommand = Get-Command $FfmpegPath -ErrorAction SilentlyContinue
if (-not $ffmpegCommand) {
    throw "ffmpeg was not found: $FfmpegPath"
}

$captureDirectory = Join-Path $env:TEMP ("timecode-sync-player-capture-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $captureDirectory | Out-Null
$videoPath = Join-Path $captureDirectory "smpte-timecode.mp4"
$settingsPath = Join-Path $captureDirectory "settings.json"

$filter = "drawtext=fontfile='C\:/Windows/Fonts/consola.ttf':timecode='00\:00\:00\:00':rate=25:x=(w-tw)/2:y=h-90:fontsize=48:fontcolor=white:box=1:boxcolor=black@0.65"
Write-Host "Generating the capture video: $videoPath"
& $ffmpegCommand.Source `
    -hide_banner -loglevel error -y `
    -f lavfi -i "smptehdbars=size=1280x720:rate=25" `
    -vf $filter `
    -t $DurationSeconds `
    -c:v libx264 -pix_fmt yuv420p -movflags +faststart `
    $videoPath
if ($LASTEXITCODE -ne 0) {
    throw "ffmpeg failed with exit code $LASTEXITCODE."
}

@"
{
  "isTimelineVisible": true
}
"@ | Set-Content -LiteralPath $settingsPath -Encoding UTF8

$previousSettingsPath = $env:TIMECODE_SYNC_PLAYER_SETTINGS_PATH
try {
    $env:TIMECODE_SYNC_PLAYER_SETTINGS_PATH = $settingsPath
    $arguments = @(
        "--open", ('"' + $videoPath + '"'),
        "--playlist", ('"' + $videoPath + '"')
    )
    $process = Start-Process `
        -FilePath $AppPath `
        -ArgumentList $arguments `
        -WorkingDirectory (Split-Path -Parent $AppPath) `
        -PassThru
}
finally {
    $env:TIMECODE_SYNC_PLAYER_SETTINGS_PATH = $previousSettingsPath
}

Write-Host "TimecodeSyncPlayer is ready for a client-side screenshot."
Write-Host "Use the RDP client or local OS screenshot function; this script does not capture the screen."
Write-Host "Save the final PNG as: $(Join-Path $projectRoot 'assets\screenshot.png')"

[pscustomobject]@{
    ProcessId = $process.Id
    VideoPath = $videoPath
    SettingsPath = $settingsPath
    ScreenshotDestination = Join-Path $projectRoot "assets\screenshot.png"
}
