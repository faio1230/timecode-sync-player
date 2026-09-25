<#
.SYNOPSIS
  結果置き場（-Root、省略時は環境変数 TCS_LTC_RESULTS_ROOT）の古い回を、要約だけ残して中身を消す（既定は一覧だけ）。

.DESCRIPTION
  -Days より古い ReportDir（runner.txt のあるフォルダー）について、run-result.json が無ければ
  ltc-run-report.ps1 で作ってから、runner.txt と run-result.json 以外を消す。
  ReportDir\media はハードリンクなので、名前が 2 つ以上あるファイルだけを消す（実素材は消えない）。
  実素材・Downloads には触らない。

.EXAMPLE
  powershell -File scripts\prune-old-ltc-runs.ps1                      # 一覧だけ
  powershell -File scripts\prune-old-ltc-runs.ps1 -Apply               # 消す
  powershell -File scripts\prune-old-ltc-runs.ps1 -Root E:\results -Days 3 -Apply
#>
param(
    [string]$Root = $env:TCS_LTC_RESULTS_ROOT,
    [int]$Days = 7,
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Root)) { throw '-Root か環境変数 TCS_LTC_RESULTS_ROOT で結果置き場を指定してください' }
if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Root not found: $Root" }
# 実行中の回を選ばないよう、1 日より新しいものは対象にしない。
if ($Days -lt 1) { throw '-Days は 1 以上（実行中の回を消さないため）' }
$cutoff = (Get-Date).AddDays(-$Days)
$keepNames = @('runner.txt', 'run-result.json')
$report = Join-Path $PSScriptRoot 'ltc-run-report.ps1'

$runs = @(Get-ChildItem -LiteralPath $Root -Recurse -Directory -Force |
    Where-Object { (Test-Path -LiteralPath (Join-Path $_.FullName 'runner.txt')) -and $_.LastWriteTime -lt $cutoff })

$total = 0L
foreach ($run in $runs) {
    $extra = @(Get-ChildItem -LiteralPath $run.FullName -Force | Where-Object { $keepNames -notcontains $_.Name })
    if ($extra.Count -eq 0) { continue }
    $bytes = 0L
    foreach ($item in $extra) {
        if ($item.PSIsContainer) {
            $sum = (Get-ChildItem -LiteralPath $item.FullName -Recurse -File -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\media\\' } | Measure-Object Length -Sum).Sum
            if ($sum) { $bytes += $sum }
        } else { $bytes += $item.Length }
    }
    $total += $bytes
    '{0,10:N1} MB  {1:yyyy-MM-dd}  {2}' -f ($bytes / 1MB), $run.LastWriteTime, $run.FullName

    if (-not $Apply) { continue }
    if (-not (Test-Path -LiteralPath (Join-Path $run.FullName 'run-result.json'))) {
        # media の掃除は ltc-run-report.ps1 -Prune に任せる（ハードリンクだけ消す）。
        & $report -ReportDir $run.FullName -Prune | Out-Null
    } elseif (Test-Path -LiteralPath (Join-Path $run.FullName 'media')) {
        & $report -ReportDir $run.FullName -Prune | Out-Null
    }
    foreach ($item in @(Get-ChildItem -LiteralPath $run.FullName -Force | Where-Object { $keepNames -notcontains $_.Name })) {
        if ($item.Name -eq 'media') {
            Write-Output ('kept (not only links) ' + $item.FullName)
            continue
        }
        Remove-Item -LiteralPath $item.FullName -Recurse -Force
    }
}
''
'{0} 回、合計 {1:N1} MB（{2} 日より古いもの。media のハードリンクは含めない）' -f $runs.Count, ($total / 1MB), $Days
if (-not $Apply) { '一覧だけです。消すときは -Apply を付けてください。' }
