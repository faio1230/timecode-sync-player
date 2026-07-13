# Validates TimecodeSyncPlayer diagnostics report sections with a synthetic log.

[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("tcsp-diagnostics-test-" + [Guid]::NewGuid().ToString("N"))
$logPath = Join-Path $tempRoot "timecodesyncplayer-test.log"
$reportDir = Join-Path $tempRoot "reports"

try {
    New-Item -ItemType Directory -Force -Path $tempRoot | Out-Null
    @(
        "2026-05-25 10:00:00.000 [INF] LTC audio stats callbacks=1 samples=48000 decodedFrames=0 sampleRate=48000 bits=32 channels=2 peak=0.100 rms=0.010 decoderFps=0.000",
        "2026-05-25 10:00:02.000 [INF] LTC audio stats callbacks=2 samples=48000 decodedFrames=12 sampleRate=48000 bits=32 channels=2 peak=0.400 rms=0.100 decoderFps=30.000",
        "2026-05-25 10:00:02.010 [WRN] LTC frame diagnostic status=Jump tc=00:00:10:00 rawSeconds=10.000 resolvedSeconds=10.000 deltaSeconds=2.000 deltaFrames=60.00 detectedFps=30.000 resolvedFps=30.000 mode=Auto",
        "2026-05-25 10:00:02.020 [INF] Timecode sync skipped due to LTC frame diagnostic status=Jump tc=00:00:10:00 resolvedSeconds=10.000 deltaSeconds=2.000 deltaFrames=60.00",
        "2026-05-25 10:00:03.000 [INF] Continue mode: waiting for file load stability playback=1.000 mediaPos=1.000 renderedFrames=1",
        "2026-05-25 10:00:04.000 [INF] Continue mode: sync seek ltc=12.000 playback=1.000 target=2.000 delta=1.000 tolerance=0.2000 success=True",
        "2026-05-25 10:00:04.100 [INF] Timecode sync pending Settled playback=2.000 tolerance=0.2000",
        "2026-05-25 10:00:05.000 [INF] Playback perf elapsed=2.00s expectedFps=30.000 playbackRate=1.000 displayedFps=29.00 ticks=60 renderCallbacks=70 coalescedRenderCallbacks=3 renderUpdates=60 frameUpdates=58 renderedFrames=58 avgRenderMs=9.20 maxRenderMs=15.00 avgBitmapMs=4.50 maxBitmapMs=9.00 avgSpoutMs=2.50 maxSpoutMs=5.00 size=1920x1080 spoutEnabled=True",
        "2026-05-25 10:00:05.100 [WRN] Playback perf warning: displayed FPS is below source FPS",
        "2026-05-25 10:00:06.000 [INF] SpoutOutput: 初期化完了 sender='TimecodeSyncPlayer'",
        "2026-05-25 10:00:06.100 [INF] SpoutOutput: 送信開始 1920x1080 pitch=7680 sender='TimecodeSyncPlayer'",
        "2026-05-25 10:00:06.200 [WRN] SpoutOutput: SendImage が false を返した count=4 (device lost?)"
    ) | Set-Content -Encoding UTF8 -LiteralPath $logPath

    & (Join-Path $repoRoot "scripts\run-timecodesyncplayer-diagnostics.ps1") `
        -SkipBuild -SkipNonE2E -SkipE2E `
        -LogPath $logPath `
        -ReportDirectory $reportDir | Out-Host

    if ($LASTEXITCODE -ne 0) {
        throw "Diagnostics runner exited with $LASTEXITCODE."
    }

    $report = Get-ChildItem -LiteralPath $reportDir -Filter "timecodesyncplayer-diagnostics-*.md" |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if ($null -eq $report) {
        throw "Diagnostics report was not created."
    }

    $content = Get-Content -Encoding UTF8 -LiteralPath $report.FullName -Raw
    $required = @(
        "Verdict: **OK**",
        "## LTC Input Health",
        "| LTC decodedFrames=0 | 1 |",
        "| LTC diagnostic Jump | 1 |",
        "## Continue Sync Health",
        "| Continue sync seek success | 1 |",
        "| Timecode sync pending Settled | 1 |",
        "## Playback Perf Timing",
        "| Render-heavy avgRenderMs>=8 | 1 |",
        "| Bitmap-heavy avgBitmapMs>=4 | 1 |",
        "| Spout-heavy avgSpoutMs>=2 | 1 |",
        "## Spout Output Health",
        "| Spout SendImage false | 1 |"
    )

    foreach ($needle in $required) {
        if (-not $content.Contains($needle)) {
            throw "Expected report content was not found: $needle"
        }
    }

    Write-Host "Diagnostics self-test passed: $($report.FullName)"
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
