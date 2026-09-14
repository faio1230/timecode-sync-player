[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$InnoSetupCompiler,
    [string]$GStreamerRoot,
    [string]$VcRedistPath,
    [string]$VcRedistUrl = "https://aka.ms/vs/17/release/vc_redist.x64.exe",
    [switch]$SkipBuild,
    [switch]$SkipInstaller
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
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
# （TestResults/p1/closure.json、49 DLL / 38.39MB）。プラグインは lib\gstreamer-1.0、それ以外は bin。
$gstCoreDlls = @(
    "avcodec-61.dll", "avfilter-10.dll", "avformat-61.dll", "avutil-59.dll",
    "bz2.dll", "dav1d.dll", "ffi-7.dll", "glib-2.0-0.dll", "gmodule-2.0-0.dll",
    "gobject-2.0-0.dll", "gstapp-1.0-0.dll", "gstaudio-1.0-0.dll", "gstbase-1.0-0.dll",
    "gstcodecparsers-1.0-0.dll", "gstcodecs-1.0-0.dll", "gstd3d11-1.0-0.dll",
    "gstd3dshader-1.0-0.dll", "gstdxva-1.0-0.dll", "gstmpegts-1.0-0.dll",
    "gstpbutils-1.0-0.dll", "gstreamer-1.0-0.dll", "gstriff-1.0-0.dll", "gstrtp-1.0-0.dll",
    "gsttag-1.0-0.dll", "gstvideo-1.0-0.dll", "intl-8.dll", "orc-0.4-0.dll",
    "pcre2-8-0.dll", "swresample-5.dll", "swscale-8.dll", "z-1.dll"
)
$gstPluginDlls = @(
    "gstapp.dll", "gstaudioconvert.dll", "gstaudioparsers.dll", "gstaudiotestsrc.dll",
    "gstautodetect.dll", "gstcoreelements.dll", "gstd3d11.dll", "gstdav1d.dll",
    "gstisomp4.dll", "gstlibav.dll", "gstmpegtsdemux.dll", "gstmxf.dll",
    "gstplayback.dll", "gstvideoconvertscale.dll", "gstvideoparsersbad.dll",
    "gstvolume.dll", "gstwasapi2.dll"
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
        Copy-Item -LiteralPath (Join-Path $licenseSource "*") -Destination $licenseTarget
    }
}

function Resolve-VcRedist([string]$ExplicitPath, [string]$Url, [string]$CacheDirectory) {
    $path = $ExplicitPath
    if (-not [string]::IsNullOrWhiteSpace($path)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "VcRedistPath not found: $path"
        }
    }
    else {
        New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null
        $path = Join-Path $CacheDirectory "vc_redist.x64.exe"
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            Write-Host "Downloading $Url ..."
            [Net.ServicePointManager]::SecurityProtocol =
                [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
        }
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne "Valid" -or
        $signature.SignerCertificate.Subject -notmatch "Microsoft Corporation") {
        throw "vc_redist.x64.exe の署名を検証できませんでした（Status=$($signature.Status)）。-VcRedistPath で検証済みのファイルを指定してください。"
    }
    return [System.IO.Path]::GetFullPath($path)
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

$releaseSubdirectories = @(Get-ChildItem -LiteralPath $releaseDirectory -Directory)
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

    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination $stagingDirectory

    $readme = @"
TimecodeSyncPlayer v$Version (Windows x64 beta)

Requirements
- Windows 10/11 x64
- .NET 8 Desktop Runtime
- Microsoft Visual C++ 2015-2022 Redistributable (x64)
  The setup installs it automatically. When using the zip, install it manually
  if it is missing: https://aka.ms/vs/17/release/vc_redist.x64.exe
- An audio input device carrying LTC

Setup
1. Start TimecodeSyncPlayer.exe.
2. Select the LTC capture device and press START.
3. Load media, then press Sync ON.

The GStreamer 1.28.2 runtime (bin, plugins and license texts) is included in the
gstreamer folder; no separate GStreamer installation is required. SpoutDX.dll
enables Spout2 output. See THIRD-PARTY-NOTICES.md for third-party terms. This
beta should be validated with your complete show setup before use.
"@
    Set-Content -LiteralPath (Join-Path $stagingDirectory "README.txt") -Value $readme -Encoding UTF8

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
        $isccPath = Resolve-InnoSetupCompiler $InnoSetupCompiler
        $vcRedist = Resolve-VcRedist $VcRedistPath $VcRedistUrl (Join-Path (Join-Path $projectRoot "artifacts") "cache")
        $installerScript = Join-Path $PSScriptRoot "installer.iss"
        Write-Host "Creating $setupName with $isccPath..."
        & $isccPath "/DMyAppVersion=$Version" "/DReleaseDirectory=$stagingDirectory" "/DVcRedistFile=$vcRedist" "/DProjectRoot=$projectRoot" "/O$OutputDirectory" "/F$([System.IO.Path]::GetFileNameWithoutExtension($setupName))" $installerScript
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
