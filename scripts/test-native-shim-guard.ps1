#requires -Version 7.0
# src\TimecodeSyncPlayer\NativeShimGuard.targets の自己試験（v0.6.6、棚卸しの #37）。
# 一時フォルダに偽の DLL 2 つ（native 側と build 側）を置いて更新時刻を変え、target だけを
# dotnet msbuild で呼ぶ。リポジトリの native\ には触らない。アプリは起動しない。終わりに一時フォルダを消す。
#
#   pwsh -NoProfile -File scripts\test-native-shim-guard.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$repoRoot = Split-Path -Parent $PSScriptRoot
$targets = Join-Path $repoRoot 'src\TimecodeSyncPlayer\NativeShimGuard.targets'
$targetName = 'TcsCheckStaleNativeShim'

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output ('ok   ' + $Name) } else { Write-Output ('FAIL ' + $Name); $failures.Add($Name) }
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-native-shim-guard-' + [Guid]::NewGuid().ToString('N'))
try {
    $nativeDir = Join-Path $root 'native'
    $buildDir = Join-Path $root 'native\gst-shim\build-debug'
    New-Item -ItemType Directory -Force -Path $nativeDir, $buildDir | Out-Null
    $nativeShim = Join-Path $nativeDir 'tcs_gstreamer.dll'
    $builtShim = Join-Path $buildDir 'tcs_gstreamer.dll'
    [IO.File]::WriteAllBytes($nativeShim, [byte[]](1, 2, 3))
    [IO.File]::WriteAllBytes($builtShim, [byte[]](4, 5, 6))

    $project = Join-Path $root 'guard-probe.proj'
    @"
<Project>
  <Import Project="$targets" />
</Project>
"@ | Set-Content -LiteralPath $project -Encoding UTF8

    function Invoke-Guard([string[]]$Extra) {
        $arguments = @('msbuild', $project, "-t:$targetName", '-nologo', '-v:minimal',
            "-p:TcsNativeShimPath=$nativeShim", "-p:TcsBuiltShimPath=$builtShim") + $Extra
        $output = (& dotnet @arguments 2>&1 | Out-String)
        [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $output }
    }

    $base = [DateTime]::new(2026, 10, 7, 12, 0, 0, [DateTimeKind]::Local)

    # 1. native\ の方が新しい: 通る（エラーも警告も出ない）
    [IO.File]::SetLastWriteTime($nativeShim, $base.AddMinutes(5))
    [IO.File]::SetLastWriteTime($builtShim, $base)
    $r = Invoke-Guard @()
    Check ($r.ExitCode -eq 0) 'native が新しい: 終了コード 0'
    Check (-not ($r.Output -match 'TCS0001|TCS0002')) 'native が新しい: TCS0001/TCS0002 が出ない'

    # 2. 同時刻（build から native へ Copy-Item で写した状態）: 通る
    [IO.File]::SetLastWriteTime($nativeShim, $base)
    $r = Invoke-Guard @()
    Check ($r.ExitCode -eq 0) '同時刻: 終了コード 0'
    Check (-not ($r.Output -match 'TCS0001|TCS0002')) '同時刻: TCS0001/TCS0002 が出ない'

    # 3. build-debug の方が新しい: 止まる。文に両方のパス・両方の更新時刻・解き方
    [IO.File]::SetLastWriteTime($builtShim, $base.AddMinutes(5))
    $r = Invoke-Guard @()
    Check ($r.ExitCode -ne 0) 'build-debug が新しい: 終了コードが 0 でない'
    Check ($r.Output -match 'TCS0001') 'build-debug が新しい: TCS0001'
    Check ($r.Output.Contains($nativeShim)) 'build-debug が新しい: native のパス'
    Check ($r.Output.Contains($builtShim)) 'build-debug が新しい: build-debug のパス'
    Check ($r.Output.Contains('2026-10-07 12:00:00')) 'build-debug が新しい: native の更新時刻'
    Check ($r.Output.Contains('2026-10-07 12:05:00')) 'build-debug が新しい: build-debug の更新時刻'
    Check ($r.Output.Contains('native\ の DLL を消す') -and $r.Output.Contains('native\ へ写す')) 'build-debug が新しい: 解き方 2 つ'
    Check ($r.Output.Contains('AllowStaleNativeShim=true')) 'build-debug が新しい: 通す口の案内'

    # 4. build-debug の方が新しい＋AllowStaleNativeShim=true: 通り、警告 1 行で native を使ったと残す
    $r = Invoke-Guard @('-p:AllowStaleNativeShim=true')
    Check ($r.ExitCode -eq 0) 'AllowStaleNativeShim=true: 終了コード 0'
    Check (-not ($r.Output -match 'TCS0001')) 'AllowStaleNativeShim=true: TCS0001 が出ない'
    Check ($r.Output -match 'warning TCS0002') 'AllowStaleNativeShim=true: 警告 TCS0002'
    Check ($r.Output -match ('使う: ' + [regex]::Escape($nativeShim))) 'AllowStaleNativeShim=true: 使った方（native）を書く'

    # 5. 片方しか無い: 動かない（build-debug だけのときは csproj がそれを写す。native だけのときは比べる相手が無い）
    Remove-Item -LiteralPath $builtShim
    $r = Invoke-Guard @()
    Check ($r.ExitCode -eq 0 -and -not ($r.Output -match 'TCS000')) 'build-debug が無い: 通る'

    # 6. 比べる相手は構成で決まる: Debug は build-debug、Release は build-release
    $debugDir = (& dotnet msbuild $project -nologo '-getProperty:TcsShimBuildDirName' | Out-String).Trim()
    $releaseDir = (& dotnet msbuild $project -nologo '-getProperty:TcsShimBuildDirName' '-p:Configuration=Release' | Out-String).Trim()
    Check ($debugDir -eq 'build-debug') "既定の構成: 比べる相手は build-debug（$debugDir）"
    Check ($releaseDir -eq 'build-release') "Release: 比べる相手は build-release（$releaseDir）"
}
finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}

if ($failures.Count -gt 0) {
    Write-Output ("FAILED: {0} 件" -f $failures.Count)
    exit 1
}
Write-Output 'ALL OK'
exit 0
