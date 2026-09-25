<#
.SYNOPSIS
  run-ltc-scenarios.ps1 の 1 回ぶんの ReportDir から、機械で差分を取れる結果 JSON（run-result.json）を作る。
  あわせて保持の規則を当てる（合格した回の出力トレースを消す、途中で止めた回の media ハードリンクを消す）。

.DESCRIPTION
  run-result.json の中身:
    候補（アプリの ProductVersion と git SHA）、-Media、MediaIn、フィルター、passed/failed/skipped/invalid、
    失敗テスト名と 1 行の症状、無効（事前確認で止めた）テスト名と理由、
    0x0 ロード件数（D34）、高速ロード件数（Gst loadfile が -FastLoadMs 未満）、トレース保存失敗件数、
    ERR/FTL、Preview stalled、R-1〜R-4 の hold-pause（一時停止までの遅れ）と上限超過、
    L-1 / L-2 の要約（l1-summary / l2-summary から主要な値）。
  所見（3 行）は人が書くので、ここでは作らない。

  保持（-Prune のとき）:
    - 失敗 0・無効 0 の回は output-trace を消す（残すのは失敗・無効の回だけ）
    - ReportDir\media（実素材へのハードリンク）が残っていれば消す（ハードリンクなので実素材は消えない）

.EXAMPLE
  powershell -File scripts\ltc-run-report.ps1 -ReportDir <結果置き場>\<候補>\std-pass-a-rtx-<日時> -Prune
#>
param(
    [Parameter(Mandatory = $true)][string]$ReportDir,
    [string]$AppExe = '',
    [string]$Media = '',
    [string]$MediaInOffsetSeconds = '',
    [string]$Filter = '',
    [int]$FastLoadMs = 30,
    [switch]$Prune
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ReportDir -PathType Container)) { throw "ReportDir not found: $ReportDir" }

# ハードリンクの名前の数（run-ltc-scenarios.ps1 と同じ実装）。
if (-not ('Tcs.HardLink' -as [type])) {
    Add-Type -Namespace Tcs -Name HardLink -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
public static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, System.IntPtr lpSecurityAttributes);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct ByHandleFileInformation {
    public uint FileAttributes;
    public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
    public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
    public uint VolumeSerialNumber;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint NumberOfLinks;
    public uint FileIndexHigh;
    public uint FileIndexLow;
}

[System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
public static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out ByHandleFileInformation info);
'@
}

function Get-HardLinkCount([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
    try {
        $info = New-Object Tcs.HardLink+ByHandleFileInformation
        if (-not [Tcs.HardLink]::GetFileInformationByHandle($stream.SafeFileHandle, [ref]$info)) {
            $code = [System.Runtime.InteropServices.Marshal]::GetLastWin32Error()
            throw ('GetFileInformationByHandle failed path=' + $Path + ' win32=' + $code)
        }
        return [int]$info.NumberOfLinks
    } finally {
        $stream.Dispose()
    }
}

function Read-JsonLines([string]$path) {
    foreach ($line in [IO.File]::ReadLines($path, [Text.Encoding]::UTF8)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $line | ConvertFrom-Json } catch { }
    }
}

function First-Line([string]$text, [int]$max = 240) {
    if (-not $text) { return '' }
    $first = ($text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
    if ($first.Length -gt $max) { $first = $first.Substring(0, $max) }
    return $first.Trim()
}

# ---- 候補 -------------------------------------------------------------------
$productVersion = ''
$sha = ''
if ($AppExe -and (Test-Path -LiteralPath $AppExe)) {
    $productVersion = (Get-Item -LiteralPath $AppExe).VersionInfo.ProductVersion
    if ($productVersion -match '\+([0-9a-f]{7,40})') { $sha = $matches[1] }
}

# ---- trx --------------------------------------------------------------------
$passed = 0; $failed = 0; $skipped = 0
$failures = @(); $invalid = @(); $skips = @()
$trx = Get-ChildItem -LiteralPath $ReportDir -Filter '*.trx' -File | Select-Object -First 1
if ($trx) {
    [xml]$doc = Get-Content -LiteralPath $trx.FullName -Encoding UTF8
    foreach ($r in @($doc.TestRun.Results.UnitTestResult)) {
        $name = ($r.testName -split '\.')[-1]
        $message = ''
        if ($r.Output -and $r.Output.ErrorInfo -and $r.Output.ErrorInfo.Message) { $message = [string]$r.Output.ErrorInfo.Message }
        switch ($r.outcome) {
            'Passed' { $passed++ }
            'Failed' { $failed++; $failures += [ordered]@{ test = $name; symptom = (First-Line $message) } }
            default {
                $text = $message
                if (-not $text -and $r.Output) { $text = [string]$r.Output.InnerText }
                # 事前確認で止めた回かどうかは harness の preflight-invalid で決める（trx の日本語は化けることがある）。
                $skips += [ordered]@{ test = $name; reason = (First-Line $text) }
            }
        }
    }
}

# ---- ログ -------------------------------------------------------------------
$appLogDir = Join-Path $ReportDir 'app-logs'
$appLines = @()
$shimLines = @()
if (Test-Path -LiteralPath $appLogDir) {
    foreach ($f in Get-ChildItem -LiteralPath $appLogDir -File -Recurse) {
        if ($f.Name -like 'timecodesyncplayer-*.log') { $appLines += [IO.File]::ReadAllLines($f.FullName, [Text.Encoding]::UTF8) }
        elseif ($f.Name -like 'tcs-gst-*.log') { $shimLines += [IO.File]::ReadAllLines($f.FullName, [Text.Encoding]::UTF8) }
    }
}
foreach ($f in Get-ChildItem -LiteralPath (Join-Path $ReportDir 'scenarios') -Filter 'tcs-gst-raw.log' -File -Recurse -ErrorAction SilentlyContinue) {
    $shimLines += [IO.File]::ReadAllLines($f.FullName, [Text.Encoding]::UTF8)
}
$zeroCaps = @($shimLines | Where-Object { $_ -match 'loaded .* 0x0@' }).Count
$fastLoads = @($appLines | Where-Object { $_ -match 'Gst loadfile .*elapsedMs=([\d.]+)' -and [double]$matches[1] -lt $FastLoadMs }).Count
$traceSaveFailures = @($appLines | Where-Object { $_ -match '出力トレースの保存に失敗' }).Count
$errFtl = @($appLines | Where-Object { $_ -match '\[(ERR|FTL)\]' }).Count
$previewStalled = @($appLines | Where-Object { $_ -match 'Preview stalled:' }).Count

# ---- harness ----------------------------------------------------------------
$holdPause = @(); $l1 = @(); $l2 = @(); $preflightOk = 0; $preflightWarnings = @()
foreach ($h in Get-ChildItem -LiteralPath (Join-Path $ReportDir 'scenarios') -Filter 'harness.jsonl' -File -Recurse -ErrorAction SilentlyContinue) {
    $scenario = Split-Path (Split-Path $h.FullName -Parent) -Leaf
    $testId = ($scenario -split '-')[0..1] -join '-'
    foreach ($e in Read-JsonLines $h.FullName) {
        switch ($e.event) {
            'preflight-ok' { $preflightOk++ }
            'preflight-warning' { $preflightWarnings += [ordered]@{ test = $testId; track = [string]$e.details.track; reason = [string]$e.details.reason } }
            'preflight-invalid' {
                $invalid += [ordered]@{ test = $testId; reason = [string]$e.details.reason }
            }
            'hold-pause' {
                # R-1/R-2 の上限: 検出 0.25 s + 1 フレーム + 0.35 s（harness の判定と同じ）。1 フレームは 25fps で近似。
                $limit = 0.25 + 0.04 + 0.35
                $holdPause += [ordered]@{ test = $testId; pauseLatencySeconds = [math]::Round([double]$e.details.pauseLatencySeconds, 3); over = ([double]$e.details.pauseLatencySeconds -gt $limit) }
            }
            'l1-summary' {
                $d = $e.details
                $l1 += [ordered]@{ track = $d.track; maxAbsError = $d.maxAbsError; settlingMaxAbsError = $d.settlingMaxAbsError;
                    seekCount = $d.seekCount; longestSeekSeconds = $d.longestSeekSeconds; errorUndecidable = $d.errorUndecidable;
                    stallUpdateWindows = $d.stallUpdateWindows; stallAdvanceWindows = $d.stallAdvanceWindows }
            }
            'l2-summary' {
                $d = $e.details
                $l2 += [ordered]@{ transitions = $d.transitions; trackEntries = $d.trackEntries; gapEntries = $d.gapEntries;
                    timedOutTransitions = $d.timedOutTransitions; boundarySamples = $d.boundarySamples;
                    ltcDisplayMismatchSamples = $d.ltcDisplayMismatchSamples; overToleranceSamples = $d.overToleranceSamples;
                    firstPictureMedianMs = $d.firstPictureMedianMs; firstPictureMaxMs = $d.firstPictureMaxMs;
                    gapBlackMaxMs = $d.gapBlackMaxMs; syncSettleMedianMs = $d.syncSettleMedianMs; syncSettleMaxMs = $d.syncSettleMaxMs;
                    firstErrorMedianSeconds = $d.firstErrorMedianSeconds; firstErrorMaxSeconds = $d.firstErrorMaxSeconds;
                    errorP99Seconds = $d.errorP99Seconds; decodeBehind = $d.decodeBehind; maxFrameDeficitSeconds = $d.maxFrameDeficitSeconds;
                    positionStalls500Ms = $d.positionStalls500Ms; peakPrivateMb = $d.peakPrivateMb }
            }
        }
    }
}

# 無効の回は trx では Skipped。L-1 → L1_ のように名前の頭で突き合わせ、ただのスキップから除く。
$invalidPrefixes = @($invalid | ForEach-Object { ($_.test -replace '-', '') + '_' })
$skips = @($skips | Where-Object { $n = $_.test; -not ($invalidPrefixes | Where-Object { $n.StartsWith($_) }) })
$skipped = $skips.Count

$result = [ordered]@{
    schema = 'ltc-run-result/1'
    label = (Split-Path $ReportDir -Leaf)
    reportDir = $ReportDir
    writtenAt = (Get-Date).ToString('o')
    productVersion = $productVersion
    sha = $sha
    media = $Media
    mediaInOffsetSeconds = $MediaInOffsetSeconds
    filter = $Filter
    passed = $passed; failed = $failed; skipped = $skipped; invalid = $invalid.Count
    failures = $failures
    invalidTests = $invalid
    preflightOk = $preflightOk
    preflightWarnings = $preflightWarnings
    skippedTests = $skips
    zeroCapsLoads = $zeroCaps
    fastLoads = $fastLoads
    fastLoadThresholdMs = $FastLoadMs
    traceSaveFailures = $traceSaveFailures
    errFtl = $errFtl
    previewStalled = $previewStalled
    holdPause = $holdPause
    holdPauseOver = @($holdPause | Where-Object { $_.over }).Count
    l1 = $l1
    l2 = $l2
}

$outPath = Join-Path $ReportDir 'run-result.json'
$result | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $outPath -Encoding UTF8
Write-Output ('RESULT ' + $outPath + ' passed=' + $passed + ' failed=' + $failed + ' invalid=' + $invalid.Count +
    ' skipped=' + $skipped + ' zero_caps=' + $zeroCaps + ' fast_loads=' + $fastLoads + ' trace_save_fail=' + $traceSaveFailures)

if ($Prune) {
    # media はハードリンクだけを消す（runner の D23-b と同じ: 名前が 1 つしかないファイルは実体なので残す）。
    $mediaLinks = Join-Path $ReportDir 'media'
    if (Test-Path -LiteralPath $mediaLinks) {
        $removed = 0; $kept = 0
        foreach ($file in @(Get-ChildItem -LiteralPath $mediaLinks -File -Force)) {
            if ((Get-HardLinkCount $file.FullName) -ge 2) { [IO.File]::Delete($file.FullName); $removed++ }
            else { $kept++; Write-Output ('kept non-link ' + $file.FullName) }
        }
        if (@(Get-ChildItem -LiteralPath $mediaLinks -Force).Count -eq 0) { [IO.Directory]::Delete($mediaLinks, $false) }
        Write-Output ('pruned media links removed=' + $removed + ' kept=' + $kept)
    }
    $trace = Join-Path $ReportDir 'output-trace'
    # 消すのは「結果がそろって合格した回」だけ。trx の無い回（途中で止めた回）は残す。
    if ($trx -and $passed -gt 0 -and $failed -eq 0 -and $invalid.Count -eq 0 -and (Test-Path -LiteralPath $trace)) {
        $bytes = (Get-ChildItem -LiteralPath $trace -Recurse -File | Measure-Object Length -Sum).Sum
        Remove-Item -LiteralPath $trace -Recurse -Force
        Write-Output ('pruned output-trace (passed run) ' + [math]::Round($bytes / 1MB, 1) + ' MB')
    }
}
