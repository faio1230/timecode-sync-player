#requires -Version 7.0
# アプリを使い捨ての設定で起動する（v0.6.6、棚卸しの #37）。実機ロードなどで手でアプリを起動するときに使う。
# 利用者の %LOCALAPPDATA%\TimecodeSyncPlayer\settings.json を書き換えないため。
#
# 一時フォルダを作り、その中の settings.json を TIMECODE_SYNC_PLAYER_SETTINGS_PATH
# （AppSettingsManager.SettingsPathEnvironmentVariable）で exe に渡して起動する。
# - 環境変数は起動する子プロセスにだけ渡す（このシェルの環境は変えない）。
# - 設定ファイルのパスが利用者の既定の settings.json と同じになるときは起動しない。
# - 一時フォルダは消さない（終わった後に設定やログの確かめに使える）。場所は起動時に表示する。
# - 起動したら待たずに戻る。exe の PID を表示する。
#
#   pwsh -File scripts\start-isolated.ps1
#   pwsh -File scripts\start-isolated.ps1 --playlist <プレイリスト>
#   pwsh -File scripts\start-isolated.ps1 -ExePath <exe> -SettingsTemplate <元にする settings.json> --playlist <プレイリスト>
#
# -ExePath          : 既定は src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe
# -SettingsDirectory: 一時フォルダの代わりに使うフォルダ（既定は %TEMP% の下に毎回新しく作る）
# -SettingsTemplate : 一時の settings.json の元にするファイル（写すだけで、元のファイルには書かない）
# 残りの引数（--playlist など、アプリの引数は -- で始まる）はそのまま exe へ渡す。
# 区切りの -- は使わない（pwsh -File では -- が引数の名前として読まれて止まる）。
# param() で受けると、アプリの引数が位置で -ExePath などに入ってしまう。そのため param() を置かず、
# $args を自分で読む（上の 3 つの名前と値の組だけを抜き、残りは順のまま exe へ渡す）。

$ErrorActionPreference = 'Stop'
$ExePath = ''
$SettingsDirectory = ''
$SettingsTemplate = ''
$appArguments = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $args.Count; $i++) {
    $argument = [string]$args[$i]
    $name = $argument.ToLowerInvariant()
    if ($name -in @('-exepath', '-settingsdirectory', '-settingstemplate')) {
        if ($i + 1 -ge $args.Count) { throw "$argument の値がありません" }
        $value = [string]$args[++$i]
        switch ($name) {
            '-exepath' { $ExePath = $value }
            '-settingsdirectory' { $SettingsDirectory = $value }
            '-settingstemplate' { $SettingsTemplate = $value }
        }
        continue
    }
    $appArguments.Add($argument)
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$settingsVariable = 'TIMECODE_SYNC_PLAYER_SETTINGS_PATH'

if ([string]::IsNullOrWhiteSpace($ExePath)) {
    $ExePath = Join-Path $repoRoot 'src\TimecodeSyncPlayer\bin\Debug\net8.0-windows\TimecodeSyncPlayer.exe'
}
$ExePath = [IO.Path]::GetFullPath($ExePath)
if (-not (Test-Path -LiteralPath $ExePath -PathType Leaf)) {
    throw "exe がありません: $ExePath（先に dotnet build するか -ExePath で指定してください）"
}

if ([string]::IsNullOrWhiteSpace($SettingsDirectory)) {
    $SettingsDirectory = Join-Path ([IO.Path]::GetTempPath()) (
        'tcs-isolated-' + (Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$SettingsDirectory = [IO.Path]::GetFullPath($SettingsDirectory)
$settingsPath = Join-Path $SettingsDirectory 'settings.json'

# 利用者の既定の settings.json（AppSettingsManager の既定と同じ場所）を指していないことを確かめる。
$userSettingsPath = [IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TimecodeSyncPlayer\settings.json'))
if ([string]::Equals($settingsPath, $userSettingsPath, [StringComparison]::OrdinalIgnoreCase)) {
    throw "設定ファイルが利用者の settings.json と同じ場所です。別のフォルダを指定してください: $settingsPath"
}

New-Item -ItemType Directory -Force -Path $SettingsDirectory | Out-Null
if (-not [string]::IsNullOrWhiteSpace($SettingsTemplate)) {
    $SettingsTemplate = [IO.Path]::GetFullPath($SettingsTemplate)
    if (-not (Test-Path -LiteralPath $SettingsTemplate -PathType Leaf)) {
        throw "元にする設定ファイルがありません: $SettingsTemplate"
    }
    Copy-Item -LiteralPath $SettingsTemplate -Destination $settingsPath -Force
}

# 子プロセスにだけ環境変数を渡す（ProcessStartInfo.Environment は現在の環境の写し）。
$startInfo = [Diagnostics.ProcessStartInfo]::new($ExePath)
$startInfo.UseShellExecute = $false
$startInfo.WorkingDirectory = Split-Path -Parent $ExePath
$startInfo.Environment[$settingsVariable] = $settingsPath
foreach ($argument in $appArguments) {
    $startInfo.ArgumentList.Add($argument)
}

$process = [Diagnostics.Process]::Start($startInfo)
Write-Output "設定フォルダ: $SettingsDirectory"
Write-Output "設定ファイル: $settingsPath"
Write-Output "exe: $ExePath"
Write-Output "PID: $($process.Id)"
