<#
.SYNOPSIS
  試験・計測で溜まる再生成可能なファイルを掃除する（既定は一覧だけで、消さない）。

.DESCRIPTION
  対象（すべて .gitignore 済みで、スクリプトやビルドで作り直せるもの）:
    - artifacts\media*         生成素材（scripts\make-e2e-media.ps1 で作り直せる）
    - artifacts\ltc-scenarios  シナリオの成果物
    - artifacts\output-trace   出力トレース（TIMECODE_SYNC_PLAYER_OUTPUT_TRACE の置き場）
    - artifacts\analysis-data  検証機から受け取った解析用の一式
    - TestResults              テストの結果と写し
    - artifacts\release        候補ビルド（公開済みの版＝git のタグにある版の zip / setup.exe は残す）
    - src\*\bin\*\*\logs       アプリのログ
  -Days より新しいものは残す（進行中の候補を消さないため）。-Keep に一致する名前も残す。

.EXAMPLE
  powershell -File scripts\clean-artifacts.ps1              # 一覧と合計だけ
  powershell -File scripts\clean-artifacts.ps1 -Apply       # 消す
  powershell -File scripts\clean-artifacts.ps1 -Days 0 -Apply -Keep 'cand5'
#>
param(
    [int]$Days = 7,
    [switch]$Apply,
    [string[]]$Keep = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cutoff = (Get-Date).AddDays(-$Days)

function Get-SizeBytes([System.IO.FileSystemInfo]$item) {
    if ($item -is [System.IO.DirectoryInfo]) {
        $sum = (Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
            Measure-Object -Property Length -Sum).Sum
        if ($sum) { return [long]$sum } else { return 0L }
    }
    return [long]$item.Length
}

function Test-Kept([System.IO.FileSystemInfo]$item) {
    foreach ($pattern in $Keep) {
        if ($item.Name -like "*$pattern*") { return $true }
    }
    return $false
}

# 公開済みの版（git のタグ）。artifacts\release のこれらの版は残す。
$publishedVersions = @()
try {
    $publishedVersions = @(git -C $root tag --list 'v*' 2>$null | ForEach-Object { $_.Trim() })
} catch { }

$candidates = New-Object System.Collections.Generic.List[object]
function Add-Children([string]$relative, [string]$filter = '*') {
    $path = Join-Path $root $relative
    if (-not (Test-Path -LiteralPath $path)) { return }
    Get-ChildItem -LiteralPath $path -Filter $filter -Force | ForEach-Object { $candidates.Add($_) }
}

Get-ChildItem -LiteralPath (Join-Path $root 'artifacts') -Directory -Filter 'media*' -Force -ErrorAction SilentlyContinue |
    ForEach-Object { $candidates.Add($_) }
Add-Children 'artifacts\ltc-scenarios'
Add-Children 'artifacts\output-trace'
Add-Children 'artifacts\analysis-data'
Add-Children 'TestResults'

$releaseDir = Join-Path $root 'artifacts\release'
if (Test-Path -LiteralPath $releaseDir) {
    Get-ChildItem -LiteralPath $releaseDir -File -Force | Where-Object {
        $name = $_.Name
        -not ($publishedVersions | Where-Object { $name -like "TimecodeSyncPlayer-$_-*" })
    } | ForEach-Object { $candidates.Add($_) }
}

Get-ChildItem -LiteralPath (Join-Path $root 'src') -Directory -Filter 'logs' -Recurse -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like '*\bin\*' } |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -File -Force } |
    ForEach-Object { $candidates.Add($_) }

$targets = @($candidates | Where-Object { $_.LastWriteTime -lt $cutoff -and -not (Test-Kept $_) })

$total = 0L
foreach ($item in $targets) {
    $size = Get-SizeBytes $item
    $total += $size
    $relative = $item.FullName.Substring($root.Length + 1)
    '{0,10:N1} MB  {1:yyyy-MM-dd}  {2}' -f ($size / 1MB), $item.LastWriteTime, $relative
}
''
'{0} 件、合計 {1:N1} MB（{2} 日より古いもの。残す指定: {3}）' -f $targets.Count, ($total / 1MB), $Days, (($Keep -join ', '), '(なし)')[$Keep.Count -eq 0]

if (-not $Apply) {
    '一覧だけです。消すときは -Apply を付けてください。'
    return
}

foreach ($item in $targets) {
    Remove-Item -LiteralPath $item.FullName -Recurse -Force -Confirm:$false
}
'{0:N1} MB を消しました。' -f ($total / 1MB)
