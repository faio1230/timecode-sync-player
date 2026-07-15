[CmdletBinding()]
param(
    [string]$Version = "0.1.0",
    [string]$OutputDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path (Join-Path $projectRoot "artifacts") "release"
}
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

$projectPath = Join-Path (Join-Path (Join-Path $projectRoot "src") "TimecodeSyncPlayer") "TimecodeSyncPlayer.csproj"
$releaseDirectory = Join-Path (Split-Path -Parent $projectPath) "bin\Release\net8.0-windows"
$zipName = "TimecodeSyncPlayer-v$Version-win-x64.zip"
$zipPath = Join-Path $OutputDirectory $zipName
$stagingDirectory = Join-Path $OutputDirectory (".package-stage-" + [Guid]::NewGuid().ToString("N"))

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

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    $excludedNames = @("mpv-2.dll", "libmpv-2.dll")
    Get-ChildItem -LiteralPath $releaseDirectory -File | ForEach-Object {
        if ($excludedNames -contains $_.Name -or $_.Extension -eq ".pdb") {
            return
        }
        Copy-Item -LiteralPath $_.FullName -Destination $stagingDirectory
    }

    $requiredRuntimeFiles = @("TimecodeSyncPlayer.exe", "TimecodeSyncPlayer.dll", "SpoutDX.dll")
    foreach ($requiredFile in $requiredRuntimeFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagingDirectory $requiredFile) -PathType Leaf)) {
            throw "Required runtime file is missing from Release output: $requiredFile"
        }
    }

    Copy-Item -LiteralPath (Join-Path $projectRoot "LICENSE") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "THIRD-PARTY-NOTICES.md") -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $projectRoot "CHANGELOG.md") -Destination $stagingDirectory

    $packagedScriptsDirectory = Join-Path $stagingDirectory "scripts"
    New-Item -ItemType Directory -Path $packagedScriptsDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "get-mpv.ps1") -Destination $packagedScriptsDirectory

    $readme = @"
TimecodeSyncPlayer v$Version (Windows x64 beta)

Requirements
- Windows 10/11 x64
- .NET 8 Desktop Runtime
- An audio input device carrying LTC

Setup
1. Open PowerShell in this extracted folder.
2. Install libmpv (it is intentionally not included in this zip):
   powershell -ExecutionPolicy Bypass -File scripts\get-mpv.ps1 -DestinationDirectory .
3. Start TimecodeSyncPlayer.exe.
4. Select the LTC capture device and press START.
5. Load media, then press Sync ON.

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
