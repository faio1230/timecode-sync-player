#requires -Version 7.0
# 配布物（zip と setup.exe）を作る。手順は docs\RELEASE-PROCEDURE-0.4.md の 1 節。
#
# VC++ 再頒布パッケージ（v0.6.0 から 14.50.35710 以上。gst-prores-d3d11 が MSVC 14.50 の CRT を要る）
# - 最低版・固定の版・SHA-256・入手元の URL は、この下の「VC++ 再頒布パッケージの固定値」の 1 か所に置く。
#   インストーラーの版の比較（installer.iss の VcRuntimeMissing）にも、ここの最低版を /D で渡す
# - キャッシュ artifacts\cache\vc_redist.x64.exe が最低版より古い、または固定の SHA-256 と違うときは止まる
# - キャッシュの更新の手順:
#   1. https://aka.ms/vc14/vc_redist.x64.exe（Microsoft の最新の v14 再頒布パッケージの固定リンク。
#      https://aka.ms/vs/18/release/vc_redist.x64.exe へ転送される）の転送先 URL を確かめる
#      （curl -sSIL <URL> の Location。download.visualstudio.microsoft.com の版ごとの URL）
#   2. その URL のファイルを落とし、ProductVersion（(Get-Item <exe>).VersionInfo.ProductVersion）、
#      SHA-256（Get-FileHash）、署名（Get-AuthenticodeSignature が Valid、Microsoft Corporation）を確かめる
#   3. 下の $vcRedistPinnedUrl・$vcRedistPinnedVersion・$vcRedistPinnedSha256 を書き換え、
#      artifacts\cache\vc_redist.x64.exe を消して（または退避して）このスクリプトを実行し直す（固定の URL から落として照合する）
#   注意: https://aka.ms/vs/17/release/vc_redist.x64.exe は 14.44（VS 2022 の系列）を返すので使わない（2026-09-30 に確認）
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$InnoSetupCompiler,
    [string]$GStreamerRoot,
    [string]$VcRedistPath,
    [string]$VcRedistUrl,
    [string]$ProResPluginDir,
    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

# VC++ 再頒布パッケージの固定値（1 か所。更新の手順は先頭のコメント）。
# 最低版はプラグイン側の回答（付属の再頒布パッケージが 14.50.35710）。固定の版は 2026-09-30 に
# https://aka.ms/vc14/vc_redist.x64.exe が返したもの。
$vcRedistMinVersion = [Version]"14.50.35710"
$vcRedistPinnedVersion = "14.51.36247.0"
$vcRedistPinnedSha256 = "843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C"
$vcRedistPinnedUrl = "https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe"
if ([string]::IsNullOrWhiteSpace($VcRedistUrl)) {
    $VcRedistUrl = $vcRedistPinnedUrl
}

$projectRoot = Split-Path -Parent $PSScriptRoot

# 最初の検査（ビルドの前）: git 管理下の文書とスクリプトに制御文字が無いこと（docs\RELEASE-PROCEDURE-0.4.md の 0 節）。
& (Join-Path $PSScriptRoot "check-control-chars.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "Control characters were found in tracked files (see the lines above; scripts\check-control-chars.ps1)."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Join-Path $projectRoot "artifacts") "release"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$projectPath = Join-Path (Join-Path (Join-Path $projectRoot "src") "TimecodeSyncPlayer") "TimecodeSyncPlayer.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
    $versionNodes = @($projectXml.SelectNodes("/Project/PropertyGroup/Version"))
    if ($versionNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($versionNodes[0].InnerText)) {
        throw "Exactly one non-empty Version element is required in $projectPath."
    }

    $Version = $versionNodes[0].InnerText.Trim()
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use the numeric major.minor.patch format: $Version"
}

$releaseDirectory = Join-Path (Split-Path -Parent $projectPath) "bin\Release\net8.0-windows"
$zipName = "TimecodeSyncPlayer-v$Version-win-x64.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$setupName = "TimecodeSyncPlayer-v$Version-setup.exe"
$setupPath = Join-Path $OutputDirectory $setupName
$stagingDirectory = Join-Path $OutputDirectory (".package-stage-" + [Guid]::NewGuid().ToString("N"))

# v0.4 は GStreamer 1.28.2 の必要 DLL を同梱する（実行環境に GStreamer を要求しない）。
# 一覧は V1 の 11 素材で実際にロードされたプラグインと、dumpbin で求めた依存閉包の実測
# （TestResults/p1/closure.json）。v0.4.1（D15）で音声付き素材用に gstaudioresample /
# gsttypefindfunctions を追加し、gsttypefindfunctions の依存閉包から gio-2.0-0.dll を追加した
# （プラグイン 19、bin 32）。プラグインは lib\gstreamer-1.0、それ以外は bin。
$gstCoreDlls = @(
    "avcodec-61.dll", "avfilter-10.dll", "avformat-61.dll", "avutil-59.dll",
    "bz2.dll", "dav1d.dll", "ffi-7.dll", "gio-2.0-0.dll", "glib-2.0-0.dll",
    "gmodule-2.0-0.dll", "gobject-2.0-0.dll", "gstapp-1.0-0.dll", "gstaudio-1.0-0.dll",
    "gstbase-1.0-0.dll", "gstcodecparsers-1.0-0.dll", "gstcodecs-1.0-0.dll",
    "gstd3d11-1.0-0.dll", "gstd3dshader-1.0-0.dll", "gstdxva-1.0-0.dll",
    "gstmpegts-1.0-0.dll", "gstpbutils-1.0-0.dll", "gstreamer-1.0-0.dll",
    "gstriff-1.0-0.dll", "gstrtp-1.0-0.dll", "gsttag-1.0-0.dll", "gstvideo-1.0-0.dll",
    "intl-8.dll", "orc-0.4-0.dll", "pcre2-8-0.dll", "swresample-5.dll", "swscale-8.dll",
    "z-1.dll"
)
$gstPluginDlls = @(
    "gstapp.dll", "gstaudioconvert.dll", "gstaudioparsers.dll", "gstaudioresample.dll",
    "gstaudiotestsrc.dll", "gstautodetect.dll", "gstcoreelements.dll", "gstd3d11.dll",
    "gstdav1d.dll", "gstisomp4.dll", "gstlibav.dll", "gstmpegtsdemux.dll", "gstmxf.dll",
    "gstplayback.dll", "gsttypefindfunctions.dll", "gstvideoconvertscale.dll",
    "gstvideoparsersbad.dll", "gstvolume.dll", "gstwasapi2.dll"
)
$gstLicenseComponents = @(
    "gstreamer-1.0", "gst-plugins-base-1.0", "gst-plugins-bad-1.0", "glib", "ffmpeg",
    "dav1d", "orc", "libffi", "pcre2", "zlib", "bzip2", "proxy-libintl"
)

function Resolve-GStreamerRoot([string]$ExplicitPath) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) { $candidates += $ExplicitPath }
    if (-not [string]::IsNullOrWhiteSpace($env:GSTREAMER_1_0_ROOT_MSVC_X86_64)) {
        $candidates += $env:GSTREAMER_1_0_ROOT_MSVC_X86_64
    }
    $candidates += Join-Path $env:ProgramFiles "gstreamer\1.0\msvc_x86_64"

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath (Join-Path $candidate "bin\gstreamer-1.0-0.dll") -PathType Leaf) -and
            (Test-Path -LiteralPath (Join-Path $candidate "lib\gstreamer-1.0") -PathType Container)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "GStreamer 1.28.2 runtime not found. Use -GStreamerRoot or set GSTREAMER_1_0_ROOT_MSVC_X86_64."
}

function Copy-GStreamerBundle([string]$root, [string]$staging) {
    $binSource = Join-Path $root "bin"
    $pluginSource = Join-Path $root "lib\gstreamer-1.0"
    $binTarget = Join-Path $staging "gstreamer\bin"
    $pluginTarget = Join-Path $staging "gstreamer\lib\gstreamer-1.0"
    New-Item -ItemType Directory -Path $binTarget -Force | Out-Null
    New-Item -ItemType Directory -Path $pluginTarget -Force | Out-Null

    foreach ($name in $gstCoreDlls) {
        $source = Join-Path $binSource $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "GStreamer DLL not found: $source"
        }
        Copy-Item -LiteralPath $source -Destination $binTarget
    }
    foreach ($name in $gstPluginDlls) {
        $source = Join-Path $pluginSource $name
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "GStreamer plugin not found: $source"
        }
        Copy-Item -LiteralPath $source -Destination $pluginTarget
    }
    foreach ($component in $gstLicenseComponents) {
        $licenseSource = Join-Path $root "share\licenses\$component"
        if (-not (Test-Path -LiteralPath $licenseSource -PathType Container)) {
            throw "GStreamer license directory not found: $licenseSource"
        }
        $licenseTarget = Join-Path $staging "gstreamer\share\licenses\$component"
        New-Item -ItemType Directory -Path $licenseTarget -Force | Out-Null
        Copy-Item -Path (Join-Path $licenseSource "*") -Destination $licenseTarget
    }
}

function Resolve-VcRedist([string]$ExplicitPath, [string]$Url, [string]$CacheDirectory) {
    $path = $ExplicitPath
    $fromCache = [string]::IsNullOrWhiteSpace($path)
    if (-not $fromCache) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "VcRedistPath not found: $path"
        }
    }
    else {
        New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
        $path = Join-Path $CacheDirectory "vc_redist.x64.exe"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Host "Downloading $Url ..."
            $partial = "$path.partial"
            $oldProgress = $ProgressPreference
            $ProgressPreference = "SilentlyContinue"
            try {
                Invoke-WebRequest -Uri $Url -OutFile $partial
            }
            finally {
                $ProgressPreference = $oldProgress
            }
            Move-Item -LiteralPath $partial -Destination $path -Force
        }
    }
    $path = [System.IO.Path]::GetFullPath($path)

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne "Valid" -or
        $signature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
        throw "vc_redist.x64.exe の署名を検証できませんでした（Status=$($signature.Status)）。-VcRedistPath で検証済みのファイルを指定してください。"
    }

    # v0.6.0: gst-prores-d3d11 は 14.50 以上の CRT が要る。古い再頒布パッケージを同梱しない。
    $productVersionText = (Get-Item -LiteralPath $path).VersionInfo.ProductVersion
    $productVersion = $null
    if (-not [Version]::TryParse(("" + $productVersionText).Trim(), [ref]$productVersion)) {
        throw "vc_redist.x64.exe の ProductVersion を読めませんでした（'$productVersionText'）: $path"
    }
    $updateHint = "キャッシュの更新の手順は scripts\package-release.ps1 の先頭のコメントと docs\RELEASE-PROCEDURE-0.4.md の 1 節を参照。" +
        "固定の版 $vcRedistPinnedVersion（SHA-256 $vcRedistPinnedSha256）を使うなら、$path を消して（または退避して）実行し直すと $vcRedistPinnedUrl から落として照合する。"
    if ($productVersion -lt $vcRedistMinVersion) {
        if ($fromCache) {
            throw "キャッシュの vc_redist.x64.exe が古い（$productVersion、最低版 $vcRedistMinVersion）: $path。$updateHint"
        }
        throw "-VcRedistPath の vc_redist.x64.exe が古い（$productVersion、最低版 $vcRedistMinVersion）: $path"
    }

    $sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    if ($fromCache -and $sha256 -ne $vcRedistPinnedSha256) {
        throw "キャッシュの vc_redist.x64.exe（$productVersion、SHA-256 $sha256）が固定値と違う: $path。$updateHint"
    }
    Write-Host "VC++ redistributable: $productVersion SHA-256 $sha256 ($path)"
    return $path
}

function Resolve-InnoSetupCompiler([string]$ExplicitPath) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        $candidates += $ExplicitPath
    }
    if (-not [string]::IsNullOrWhiteSpace($env:INNO_SETUP_COMPILER_PATH)) {
        $candidates += $env:INNO_SETUP_COMPILER_PATH
    }

    $pathCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($pathCommand) {
        $candidates += $pathCommand.Source
    }

    $candidates += Join-Path (Join-Path (Join-Path $env:LOCALAPPDATA "Programs") "Inno Setup 6") "ISCC.exe"

    foreach ($candidate in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and
            (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return [System.IO.Path]::GetFullPath($candidate)
        }
    }

    throw "Inno Setup 6 compiler (ISCC.exe) was not found. Use -InnoSetupCompiler or set INNO_SETUP_COMPILER_PATH."
}

# v0.6.0: ProRes の GPU 復号プラグイン（gst-prores-d3d11）。DLL の SHA-256（固定値）と .cso 6 個
# （SHA256SUMS.txt）を get-prores-plugin.ps1 -VerifyDir で照合する（固定値はそのスクリプトの先頭の 1 か所）。
# 無い・違うときは止める（ProRes の GPU 復号の無い配布物を黙って作らない）。ビルドの前に見て早く止める。
if ([string]::IsNullOrWhiteSpace($ProResPluginDir)) {
    $ProResPluginDir = Join-Path $projectRoot "native\gst-prores"
}
$ProResPluginDir = [System.IO.Path]::GetFullPath($ProResPluginDir)
$proResDllName = "gstproresd3d11.dll"
try {
    & (Join-Path $PSScriptRoot "get-prores-plugin.ps1") -VerifyDir $ProResPluginDir
}
catch {
    throw "ProRes の GPU 復号プラグインを照合できませんでした（$ProResPluginDir）: $($_.Exception.Message)。scripts\get-prores-plugin.ps1 を実行するか -ProResPluginDir を指定してください。"
}

# インストーラーの材料（VC++ 再頒布パッケージの版と SHA-256 を含む）もビルドの前に確かめる。
# 古い再頒布パッケージのときに zip だけができて止まる、を避ける。
$isccPath = $null
$vcRedist = $null
if (-not $SkipInstaller) {
    $isccPath = Resolve-InnoSetupCompiler $InnoSetupCompiler
    $vcRedist = Resolve-VcRedist $VcRedistPath $VcRedistUrl (Join-Path (Join-Path $projectRoot "artifacts") "cache")
}

if (-not $SkipBuild) {
    Write-Host "Building TimecodeSyncPlayer $Version (Release)..."
    & dotnet build $projectPath -c Release -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Release build failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $releaseDirectory -PathType Container)) {
    throw "Release output was not found: $releaseDirectory"
}

# gst-extra-plugins は開発用（csproj が native\gst-prores の DLL と .cso をコピーする。同梱でないときだけ
# GST_PLUGIN_PATH に足される）。配布物には入れず、プラグインは下で gstreamer\lib\gstreamer-1.0 に置く。
$releaseSubdirectories = @(Get-ChildItem -LiteralPath $releaseDirectory -Directory |
    Where-Object { $_.Name -ne "gst-extra-plugins" })
if ($releaseSubdirectories.Count -gt 0) {
    $names = ($releaseSubdirectories | ForEach-Object { $_.Name }) -join ", "
    throw "Release output contains subdirectories, but zip staging and installer.iss intentionally copy only top-level files. Remove these directories and retry: $names"
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    # v0.4 は mpv を除去する。Release 出力に mpv が残っていたら黙って除外せず失敗させ、
    # 「製品側の除去が入っていないビルド」を配布物にしない。
    $forbiddenMpvFiles = @(Get-ChildItem -LiteralPath $releaseDirectory -File |
        Where-Object { $_.Name -in @("mpv-2.dll", "libmpv-2.dll") })
    if ($forbiddenMpvFiles.Count -gt 0) {
        $names = ($forbiddenMpvFiles | ForEach-Object { $_.Name }) -join ", "
        throw "v0.4 の配布物に mpv は含めません。Release 出力に残っています: $names。製品側の mpv 除去が反映されたビルドを使用してください。"
    }

    Get-ChildItem -LiteralPath $releaseDirectory -File | ForEach-Object {
        if ($_.Extension -eq ".pdb") {
            return
        }
        Copy-Item -LiteralPath $_.FullName -Destination $stagingDirectory
    }

    $requiredRuntimeFiles = @("TimecodeSyncPlayer.exe", "TimecodeSyncPlayer.dll", "SpoutDX.dll", "tcs_gstreamer.dll")
    foreach ($requiredFile in $requiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile) -PathType Leaf)) {
            throw "Required runtime file is missing from Release output: $requiredFile"
        }
    }

    # 配布する shim は Release ビルドでなければならない（Debug ビルドは MSVCP140D / ucrtbased に依存し、
    # 利用者環境で動かない）。native\tcs_gstreamer.dll に置いたものも含め、Release ビルドと一致するかを見る。
    $releaseShim = Join-Path $projectRoot "native\gst-shim\build-release\tcs_gstreamer.dll"
    if (-not (Test-Path -LiteralPath $releaseShim -PathType Leaf)) {
        throw "Release ビルドの tcs_gstreamer.dll がありません。先に native\gst-shim\build-shim.ps1 -Config Release を実行してください: $releaseShim"
    }
    $stagedShim = Join-Path $stagingDirectory "tcs_gstreamer.dll"
    $expectedHash = (Get-FileHash -LiteralPath $releaseShim -Algorithm SHA256).Hash
    $actualHash = (Get-FileHash -LiteralPath $stagedShim -Algorithm SHA256).Hash
    if ($actualHash -ne $expectedHash) {
        throw "配布物の tcs_gstreamer.dll が Release ビルドと一致しません。native\tcs_gstreamer.dll（または native\gst-shim\build-debug の出力）が Release ビルドで上書きされているか確認してください。"
    }

    # GStreamer ランタイム（必要 DLL とライセンス文書）を同梱する。
    $gstRoot = Resolve-GStreamerRoot $GStreamerRoot
    Write-Host "Bundling GStreamer runtime from $gstRoot ..."
    Copy-GStreamerBundle $gstRoot $stagingDirectory
    foreach ($bundledFile in @(
        "gstreamer\bin\gstreamer-1.0-0.dll",
        "gstreamer\lib\gstreamer-1.0\gstcoreelements.dll",
        "gstreamer\share\licenses\gstreamer-1.0\LGPL-2.0-or-later.txt")) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $bundledFile) -PathType Leaf)) {
            throw "Bundled GStreamer file is missing: $bundledFile"
        }
    }

    # v0.6.0: ProRes の GPU 復号プラグイン（照合済み）を同梱のプラグインのフォルダへ。DLL の名前は変えない
    # （GStreamer がファイル名から入口関数を探す）。.cso は DLL と同じフォルダ（プラグインは DLL 自身の
    # フォルダから探す）。ライセンス文書は gstreamer\share\licenses\gst-prores-d3d11。
    $proResTarget = Join-Path $stagingDirectory "gstreamer\lib\gstreamer-1.0"
    $proResLicenseTarget = Join-Path $stagingDirectory "gstreamer\share\licenses\gst-prores-d3d11"
    New-Item -ItemType Directory -Path $proResLicenseTarget -Force | Out-Null
    $proResFiles = @(Get-Item -LiteralPath (Join-Path $ProResPluginDir $proResDllName)) +
        @(Get-ChildItem -LiteralPath $ProResPluginDir -File -Filter "prores_*.cso")
    $proResCopies = @()
    foreach ($file in $proResFiles) {
        $proResCopies += , @($file.FullName, (Join-Path $proResTarget $file.Name))
    }
    foreach ($name in @("LICENSE", "README.txt")) {
        $proResCopies += , @((Join-Path (Join-Path $ProResPluginDir "licenses") $name), (Join-Path $proResLicenseTarget $name))
    }
    foreach ($copy in $proResCopies) {
        Copy-Item -LiteralPath $copy[0] -Destination $copy[1]
        if ((Get-FileHash -LiteralPath $copy[0] -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $copy[1] -Algorithm SHA256).Hash) {
            throw "ProRes plugin file differs after copy: $($copy[1])"
        }
    }
    Write-Host "Bundled gst-prores-d3d11: $proResDllName and $($proResFiles.Count - 1) .cso in gstreamer\lib\gstreamer-1.0, licenses in gstreamer\share\licenses\gst-prores-d3d11"

    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination $stagingDirectory

    $readme = @"
TimecodeSyncPlayer v$Version (Windows x64)

Requirements
- Windows 10/11 x64
- .NET 8 Desktop Runtime
- Microsoft Visual C++ v14 Redistributable (x64) 14.50.35710 or later
  (formerly Microsoft Visual C++ 2015-2022)
  The setup installs or updates it automatically. When using the zip, install it
  manually if it is missing or older: https://aka.ms/vc14/vc_redist.x64.exe
- An audio input device carrying LTC

Setup
1. Start TimecodeSyncPlayer.exe.
2. Select the LTC capture device and press START.
3. Load media, then press Sync ON.

ProRes GPU decoding is enabled by default on all GPUs (verified on NVIDIA; other
vendors are unverified). Set proResGpu to off to decode on the CPU. A change takes
effect after restarting the app.

The GStreamer 1.28.2 runtime (bin, plugins and license texts) is included in the
gstreamer folder; no separate GStreamer installation is required. SpoutDX.dll
enables Spout2 output. See THIRD-PARTY-NOTICES.md for third-party terms. This
release should be validated with your complete show setup before use.
"@
    # BOM 付きの UTF-8（5.1 の -Encoding UTF8 で作っていた配布物と同じバイト。7 の UTF8 は BOM なし）
    Set-Content -LiteralPath (Join-Path $stagingDirectory "README.txt") -Value $readme -Encoding utf8BOM

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "Creating $zipName..."
    Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "Created: $zipPath"

    if ($SkipInstaller) {
        Write-Host "Skipping installer generation because -SkipInstaller was specified."
    }
    else {
        # インストーラーは zip と同じステージング内容（同梱 GStreamer・ライセンス含む）から作る。
        # ISCC と再頒布パッケージはビルドの前に解決済み。VC++ の最低版は installer.iss の比較へ渡す。
        $installerScript = Join-Path $PSScriptRoot "installer.iss"
        Write-Host "Creating $setupName with $isccPath..."
        & $isccPath "/DMyAppVersion=$Version" "/DReleaseDirectory=$stagingDirectory" "/DVcRedistFile=$vcRedist" "/DProjectRoot=$projectRoot" "/DVcMinMajor=$($vcRedistMinVersion.Major)" "/DVcMinMinor=$($vcRedistMinVersion.Minor)" "/DVcMinBld=$($vcRedistMinVersion.Build)" "/O$OutputDirectory" "/F$([System.IO.Path]::GetFileNameWithoutExtension($setupName))" $installerScript
        if ($LASTEXITCODE -ne 0) {
            throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
        }
        if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
            throw "Inno Setup completed without creating the expected file: $setupPath"
        }
        Write-Host "Created: $setupPath"
    }
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
