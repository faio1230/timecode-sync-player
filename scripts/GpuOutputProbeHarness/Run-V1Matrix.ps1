# V1: run the generated codec matrix through the main app (GStreamer x Gpu), one clip at a time.
[CmdletBinding()]
param(
    [string]$MediaDir = 'C:\Users\codea\Documents\timecode-sync-player-wt-integrate-20260912\artifacts\media\v1',
    [string]$AppExe = 'C:\Users\codea\Documents\timecode-sync-player-wt-integrate-20260912\src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe',
    [string]$LogRoot = 'C:\Users\codea\Documents\timecode-sync-player-wt-integrate-20260912\TestResults\v1',
    [int]$Seconds = 50,
    [string[]]$Only = @(),
    # V11: '' = hardware 既定（decodeMode を settings に入れない）、software = decodeMode=software で回す。
    [ValidateSet('', 'hardware', 'software')][string]$DecodeMode = '',
    # V11: run ラベルは '<prefix>-<clip>'。LogRoot を条件ごとに分けて集計する。
    [string]$LabelPrefix = 'v1'
)
$ErrorActionPreference = 'Continue'
$runner = Join-Path $PSScriptRoot 'Invoke-AppGpuTrial.ps1'
$clips = Get-ChildItem $MediaDir -File | Where-Object { $_.Extension -in '.mp4','.mov','.ts','.mxf','.mkv' } | Sort-Object Name
if ($Only.Count -gt 0) { $clips = $clips | Where-Object { $Only -contains $_.Name } }
$results = @()
foreach ($c in $clips) {
    $label = $LabelPrefix + '-' + [IO.Path]::GetFileNameWithoutExtension($c.Name)
    Write-Output ("=== {0} {1}" -f (Get-Date).ToString('HH:mm:ss'), $c.Name)
    $trialArgs = @(
        '-MediaPath', $c.FullName,
        '-Label', $label,
        '-Seconds', $Seconds,
        '-PlayerBackend', 'Gstreamer',
        '-AppExe', $AppExe,
        '-LogRoot', $LogRoot,
        '-ExitDialog', 'Normal'
    )
    # Windows PowerShell 5.1 の子プロセス呼び出しでは空文字引数が落ちるため、
    # hardware 既定（$DecodeMode が空）では -DecodeMode ごと渡さない。
    if ($DecodeMode) { $trialArgs += @('-DecodeMode', $DecodeMode) }
    $out = & powershell -NoProfile -ExecutionPolicy Bypass -File $runner @trialArgs 2>&1
    $runLine = ($out | Select-String -Pattern '^RUN ' | Select-Object -First 1)
    $json = ($out | Where-Object { $_ -is [string] } | Where-Object { $_ -notmatch '^RUN ' -and $_ -notmatch 'tcs-gst' }) -join "`n"
    $rec = [ordered]@{ clip=$c.Name; run=($(if ($runLine) { $runLine.ToString().Substring(4) } else { $null })); appExit=$null; error=$null }
    try { $j = $json | ConvertFrom-Json; $rec.appExit = $j.appExit; $rec.error = $j.error } catch { $rec.error = 'runner output not parseable' }
    $results += [pscustomobject]$rec
    Write-Output ("    exit={0} error={1} run={2}" -f $rec.appExit, $rec.error, $rec.run)
    Start-Sleep -Seconds 3
}
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $LogRoot 'v1-matrix-results.json') -Encoding UTF8
Write-Output 'V1 MATRIX DONE'
