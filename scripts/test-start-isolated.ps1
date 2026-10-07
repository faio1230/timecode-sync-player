#requires -Version 7.0
# scripts\start-isolated.ps1 の自己試験（v0.6.6、棚卸しの #37）。アプリは起動しない。
# exe の代わりに pwsh で小さな probe スクリプトを起動し、受け取った環境変数と引数を JSON に書かせて確かめる。
# 利用者の %LOCALAPPDATA%\TimecodeSyncPlayer\settings.json が前後で変わらないことも見る。
# 終わりに一時フォルダを消す。
#
#   pwsh -NoProfile -File scripts\test-start-isolated.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$startScript = Join-Path $PSScriptRoot 'start-isolated.ps1'
$pwshPath = (Get-Process -Id $PID).Path
$settingsVariable = 'TIMECODE_SYNC_PLAYER_SETTINGS_PATH'

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output ('ok   ' + $Name) } else { Write-Output ('FAIL ' + $Name); $failures.Add($Name) }
}

function Get-FileStamp([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '(なし)' }
    $item = Get-Item -LiteralPath $Path
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash + ' ' + $item.LastWriteTimeUtc.Ticks
}

# start-isolated.ps1 を別の pwsh で -File 起動する（利用者が打つのと同じ渡り方）。出力と PID を返す。
function Invoke-StartIsolated([string[]]$Arguments) {
    $output = (& $pwshPath -NoProfile -File $startScript @Arguments 2>&1 | Out-String)
    $exitCode = $LASTEXITCODE
    $childPid = $null
    if ($output -match 'PID: (\d+)') { $childPid = [int]$Matches[1] }
    [pscustomobject]@{ ExitCode = $exitCode; Output = $output; ChildPid = $childPid }
}

function Wait-Child($Result) {
    if ($null -ne $Result.ChildPid) {
        try { Wait-Process -Id $Result.ChildPid -Timeout 60 -ErrorAction Stop } catch { }
    }
}

$userSettings = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TimecodeSyncPlayer\settings.json'
$userStampBefore = Get-FileStamp $userSettings
$sessionValueBefore = [Environment]::GetEnvironmentVariable($settingsVariable)

$root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-start-isolated-test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Force -Path $root | Out-Null
    $probe = Join-Path $root 'probe.ps1'
    @'
$out = $args[0]
[ordered]@{
    settingsPath = [Environment]::GetEnvironmentVariable('TIMECODE_SYNC_PLAYER_SETTINGS_PATH')
    arguments = @($args | Select-Object -Skip 1)
    workingDirectory = (Get-Location).Path
} | ConvertTo-Json | Set-Content -LiteralPath $out -Encoding UTF8
'@ | Set-Content -LiteralPath $probe -Encoding UTF8

    # 1. 既定の一時フォルダ。スクリプトの名前の付かない引数（- や -- で始まるもの・空白入り・= 入り）がそのまま exe へ渡る
    $out1 = Join-Path $root 'out1.json'
    $r = Invoke-StartIsolated @('-ExePath', $pwshPath, '-NoProfile', '-File', $probe, $out1, '--playlist', 'a b.json', '--load-project=c d.json', '--mute')
    Wait-Child $r
    Check ($r.ExitCode -eq 0) '既定: 終了コード 0'
    Check ($null -ne $r.ChildPid) '既定: PID を表示する'
    Check ($r.Output -match '設定フォルダ: (.+)') '既定: 一時の場所を表示する'
    $shownDirectory = $Matches[1].Trim()
    Check ($shownDirectory.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) "既定: 一時の場所は %TEMP% の下（$shownDirectory）"
    Check (Test-Path -LiteralPath $out1) '既定: probe が走った'
    if (Test-Path -LiteralPath $out1) {
        $seen = Get-Content -LiteralPath $out1 -Raw | ConvertFrom-Json
        Check ($seen.settingsPath -eq (Join-Path $shownDirectory 'settings.json')) "既定: 環境変数が一時の settings.json を指す（$($seen.settingsPath)）"
        Check ((@($seen.arguments) -join '|') -eq '--playlist|a b.json|--load-project=c d.json|--mute') "既定: 引数がそのまま渡る（$(@($seen.arguments) -join '|')）"
        Check ($seen.workingDirectory -eq (Split-Path -Parent $pwshPath)) '既定: 作業フォルダは exe のフォルダ'
    }
    if ($shownDirectory -and (Test-Path -LiteralPath $shownDirectory)) { Remove-Item -LiteralPath $shownDirectory -Recurse -Force }

    # 2. 引数が --playlist から始まっても exe へ渡る。-SettingsDirectory と -SettingsTemplate（写すだけ）
    $out2 = Join-Path $root 'out2.json'
    $settingsDir = Join-Path $root 'given-settings'
    $template = Join-Path $root 'template.json'
    '{ "syncOffsetMs": 12 }' | Set-Content -LiteralPath $template -Encoding UTF8
    $templateStamp = Get-FileStamp $template
    $r = Invoke-StartIsolated @('-ExePath', $pwshPath, '-SettingsDirectory', $settingsDir, '-SettingsTemplate', $template, $probe, $out2, '--playlist', 'x.json')
    Wait-Child $r
    Check ($r.ExitCode -eq 0) '指定: 終了コード 0'
    if (Test-Path -LiteralPath $out2) {
        $seen = Get-Content -LiteralPath $out2 -Raw | ConvertFrom-Json
        Check ($seen.settingsPath -eq (Join-Path $settingsDir 'settings.json')) "指定: 環境変数が指定のフォルダの settings.json を指す（$($seen.settingsPath)）"
        Check ((@($seen.arguments) -join '|') -eq '--playlist|x.json') "指定: 引数が渡る（$(@($seen.arguments) -join '|')）"
    }
    else {
        Check $false '指定: probe が走った'
    }
    Check ((Get-Content -LiteralPath (Join-Path $settingsDir 'settings.json') -Raw).Contains('"syncOffsetMs": 12')) '指定: 元の設定が写る'
    Check ((Get-FileStamp $template) -eq $templateStamp) '指定: 元の設定ファイルは変わらない'

    # 3. 利用者の既定の場所を指すと起動しない
    $userDir = Split-Path -Parent $userSettings
    $out3 = Join-Path $root 'out3.json'
    $r = Invoke-StartIsolated @('-ExePath', $pwshPath, '-SettingsDirectory', $userDir, '-NoProfile', '-File', $probe, $out3)
    Wait-Child $r
    Check ($r.ExitCode -ne 0) '利用者の場所: 止まる'
    Check ($null -eq $r.ChildPid -and -not (Test-Path -LiteralPath $out3)) '利用者の場所: 起動しない'

    # 4. exe が無いと止まる
    $r = Invoke-StartIsolated @('-ExePath', (Join-Path $root 'missing.exe'))
    Check ($r.ExitCode -ne 0 -and $null -eq $r.ChildPid) 'exe が無い: 止まる'
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

Check ((Get-FileStamp $userSettings) -eq $userStampBefore) '利用者の settings.json は前後で変わらない'
Check ([Environment]::GetEnvironmentVariable($settingsVariable) -eq $sessionValueBefore) 'このシェルの環境変数は変わらない'

if ($failures.Count -gt 0) {
    Write-Output ("FAILED: {0} 件" -f $failures.Count)
    exit 1
}
Write-Output 'ALL OK'
exit 0
