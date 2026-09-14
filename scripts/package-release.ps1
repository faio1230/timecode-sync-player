[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDirectory,
    [string]$InnoSetupCompiler,
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

    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination $stagingDirectory

    $readme = @"
TimecodeSyncPlayer v$Version (Windows x64 beta)

Requirements
- Windows 10/11 x64
- .NET 8 Desktop Runtime
- GStreamer 1.28 MSVC x64 runtime
  (https://gstreamer.freedesktop.org/download/)
- An audio input device carrying LTC

Setup
1. Start TimecodeSyncPlayer.exe.
2. Select the LTC capture device and press START.
3. Load media, then press Sync ON.

SpoutDX.dll is included and enables Spout2 output. See THIRD-PARTY-NOTICES.md for
third-party terms. This beta should be validated with your complete show setup before use.
"@
    Set-Content -LiteralPath (Join-Path $stagingDirectory "README.txt") -Value $readme -Encoding UTF8

    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "Creating $zipName..."
    Compress-Archive -Path (Join-Path $stagingDirectory "*") -DestinationPath $zipPath -CompressionLevel Optimal

    Write-Host "Created: $zipPath"
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}

if ($SkipInstaller) {
    Write-Host "Skipping installer generation because -SkipInstaller was specified."
}
else {
    $isccPath = Resolve-InnoSetupCompiler $InnoSetupCompiler
    $installerScript = Join-Path $PSScriptRoot "installer.iss"
    Write-Host "Creating $setupName with $isccPath..."
    & $isccPath "/DMyAppVersion=$Version" "/DReleaseDirectory=$releaseDirectory" "/DProjectRoot=$projectRoot" "/O$OutputDirectory" "/F$([System.IO.Path]::GetFileNameWithoutExtension($setupName))" $installerScript
    if ($LASTEXITCODE -ne 0) {
        throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf)) {
        throw "Inno Setup completed without creating the expected file: $setupPath"
    }
    Write-Host "Created: $setupPath"
}
