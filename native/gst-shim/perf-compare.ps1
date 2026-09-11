# Reference performance comparison: mpv backend vs GStreamer backend.
# Conditions are recorded in the output. Run from the worktree root.
#   powershell -File native/gst-shim/perf-compare.ps1 [-Media <path>] [-Seconds 15]
param(
    [string]$Media = "artifacts/media/test_720p60_long.mp4",
    [int]$Seconds = 15
)
$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$exe = Join-Path $repo "src/TimecodeSyncPlayer/bin/Debug/net8.0-windows/TimecodeSyncPlayer.exe"
if (-not (Test-Path $exe)) { throw "アプリを先にビルドしてください: $exe" }
if (-not (Test-Path (Join-Path $repo $Media))) { throw "メディアが無い: $Media" }

function Measure-Backend {
    param([int]$Backend, [string]$Label)
    $dir = Join-Path $env:TEMP ("tcs-perf-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $dir | Out-Null
    $settingsPath = Join-Path $dir "settings.json"
    [System.IO.File]::WriteAllText($settingsPath, "{`"backend`":$Backend}")

    $env:TIMECODE_SYNC_PLAYER_SETTINGS_PATH = $settingsPath
    $env:TIMECODE_SYNC_PLAYER_SPOUT_NAME = "TCSPerf-$Label"
    $exeDir = Split-Path -Parent $exe
    $p = $null
    try {
        $p = Start-Process -FilePath $exe -ArgumentList "--open", (Join-Path $repo $Media) `
            -WorkingDirectory $exeDir -PassThru
        Start-Sleep -Seconds 4   # load + 再生安定待ち

        $cpu0 = $p.TotalProcessorTime.TotalMilliseconds
        $wsSamples = @()
        $gpuSamples = @()
        $t0 = Get-Date
        for ($i = 0; $i -lt $Seconds; $i++) {
            Start-Sleep -Seconds 1
            try { $p.Refresh() } catch { break }
            $wsSamples += [math]::Round($p.WorkingSet64 / 1MB, 1)
            $g = & nvidia-smi --query-gpu=utilization.gpu,memory.used --format=csv,noheader,nounits 2>$null
            if ($g) { $gpuSamples += $g.Trim() }
        }
        $elapsed = ((Get-Date) - $t0).TotalMilliseconds
        $cpu1 = $p.TotalProcessorTime.TotalMilliseconds
        $cpuPct = [math]::Round(($cpu1 - $cpu0) / $elapsed * 100, 1)
        $cores = [Environment]::ProcessorCount
        [PSCustomObject]@{
            backend        = $Label
            media          = $Media
            seconds        = $Seconds
            cpu_percent_1core = $cpuPct
            cpu_note       = "per single core; machine has $cores logical cores"
            ws_avg_MB      = [math]::Round(($wsSamples | Measure-Object -Average).Average, 1)
            ws_max_MB      = ($wsSamples | Measure-Object -Maximum).Maximum
            gpu_samples    = ($gpuSamples -join " | ")
        }
    }
    finally {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
        Remove-Item Env:TIMECODE_SYNC_PLAYER_SETTINGS_PATH -ErrorAction SilentlyContinue
        Remove-Item Env:TIMECODE_SYNC_PLAYER_SPOUT_NAME -ErrorAction SilentlyContinue
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }
}

"# 条件: RTX 3070 / GStreamer 1.28.2 / Debug ビルド / Spout OFF / LTC 入力なし"
"# 競合: 他エージェントの GPU 試験が同時に走っている場合は参考値"
$mpv = Measure-Backend -Backend 0 -Label "mpv"
$gst = Measure-Backend -Backend 1 -Label "gstreamer"
$mpv, $gst | Format-List
