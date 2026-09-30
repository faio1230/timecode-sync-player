#requires -Version 7.0
# Fetch the pinned gst-prores-d3d11 plugin (proresd3d11dec) for development.
# Downloads the release zip into artifacts\cache (reused when present), checks
# the SHA-256 of the zip and of gstproresd3d11.dll against the pinned values,
# checks the six prores_*.cso shaders against the SHA256SUMS.txt in the zip,
# and places the DLL and the shaders in native\gst-prores. LICENSE, README.txt
# and SHA256SUMS.txt from the zip go to native\gst-prores\licenses.
# The DLL name must not change (GStreamer derives the entry point from it).
# Usage (from the repository root):
#   pwsh -File scripts\get-prores-plugin.ps1
#   pwsh -File scripts\get-prores-plugin.ps1 -VerifyDir <plugin dir>
# -VerifyDir only checks an already placed plugin directory (same layout as
# native\gst-prores) and stops on any mismatch. package-release.ps1 uses it so
# the pinned values below stay in this one place.
param(
    [string]$VerifyDir
)
$ErrorActionPreference = "Stop"

# Pinned version, URL and hashes (single place).
# v0.2.1 is the release bundled with v0.6.0 (fixed 2026-09-30).
$version = "v0.2.1"
$zipName = "gst-prores-d3d11-$version-win64-gst1.28.2.zip"
$zipUrl = "https://github.com/faio1230/gst-prores-d3d11/releases/download/$version/$zipName"
$expectedZipSha256 = "57053c0b4c33c52e53eedf6e6e3f2b61adbc1a7c3605e28e1fde5cd32a13e9b1"
$dllName = "gstproresd3d11.dll"
$expectedDllSha256 = "047cde8f434a061e76b1311ea8abfce1643b12a1a871134c3a912d5c3f0077e2"
$expectedCsoCount = 6
$licenseFiles = @("LICENSE", "README.txt", "SHA256SUMS.txt")
$licensesDirName = "licenses"

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cacheDir = Join-Path $repoRoot "artifacts\cache"
$zipPath = Join-Path $cacheDir $zipName
$dest = Join-Path $repoRoot "native\gst-prores"

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

function Read-Sha256Sums([string]$sumsPath) {
    $sums = @{}
    foreach ($line in (Get-Content -LiteralPath $sumsPath)) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -notmatch '^([0-9a-fA-F]{64}) [ *](.+)$') {
            throw "Unexpected line in ${sumsPath}: $line"
        }
        $sums[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant()
    }
    return $sums
}

# Checks the DLL against the pinned SHA-256 and the six .cso shaders next to it
# against SHA256SUMS.txt. The DLL line of SHA256SUMS.txt must also match the
# pinned value, which ties the shader hashes to the pinned release.
function Assert-ProResPluginFiles([string]$pluginDir, [string]$sumsPath) {
    $dllPath = Join-Path $pluginDir $dllName
    if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
        throw "$dllName not found in $pluginDir"
    }
    $dllSha = Get-Sha256 $dllPath
    if ($dllSha -ne $expectedDllSha256) {
        throw "$dllName SHA-256 mismatch: expected $expectedDllSha256, got $dllSha ($dllPath)"
    }
    if (-not (Test-Path -LiteralPath $sumsPath -PathType Leaf)) {
        throw "SHA256SUMS.txt not found: $sumsPath"
    }
    $sums = Read-Sha256Sums $sumsPath
    if ($sums[$dllName] -ne $expectedDllSha256) {
        throw "SHA256SUMS.txt does not list the pinned $dllName hash ($sumsPath)"
    }
    $csoNames = @($sums.Keys | Where-Object { $_ -like "prores_*.cso" } | Sort-Object)
    if ($csoNames.Count -ne $expectedCsoCount) {
        throw "Expected $expectedCsoCount prores_*.cso in SHA256SUMS.txt, found $($csoNames.Count) ($sumsPath)"
    }
    $csos = @(Get-ChildItem -LiteralPath $pluginDir -File -Filter "prores_*.cso")
    if ($csos.Count -ne $expectedCsoCount) {
        throw "Expected $expectedCsoCount prores_*.cso next to $dllName, found $($csos.Count) ($pluginDir)"
    }
    foreach ($name in $csoNames) {
        $csoPath = Join-Path $pluginDir $name
        if (-not (Test-Path -LiteralPath $csoPath -PathType Leaf)) {
            throw "$name not found next to $dllName ($pluginDir)"
        }
        $csoSha = Get-Sha256 $csoPath
        if ($csoSha -ne $sums[$name]) {
            throw "$name SHA-256 mismatch: expected $($sums[$name]), got $csoSha ($csoPath)"
        }
    }
    return $csoNames
}

if (-not [string]::IsNullOrWhiteSpace($VerifyDir)) {
    $verifyRoot = [System.IO.Path]::GetFullPath($VerifyDir)
    $verifyLicenses = Join-Path $verifyRoot $licensesDirName
    foreach ($name in $licenseFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $verifyLicenses $name) -PathType Leaf)) {
            throw "$name not found in $verifyLicenses (run scripts\get-prores-plugin.ps1)"
        }
    }
    $verified = Assert-ProResPluginFiles $verifyRoot (Join-Path $verifyLicenses "SHA256SUMS.txt")
    Write-Host "OK: gst-prores-d3d11 $version verified ($dllName $expectedDllSha256, $($verified.Count) .cso) at $verifyRoot"
    return
}

if (Test-Path -LiteralPath $zipPath) {
    Write-Host "Using cached $zipName; verifying..."
} else {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    $partial = "$zipPath.partial"
    Write-Host "Downloading $zipUrl"
    $oldProgress = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $partial
    } finally {
        $ProgressPreference = $oldProgress
    }
    Move-Item -LiteralPath $partial -Destination $zipPath -Force
}

$zipSha = Get-Sha256 $zipPath
if ($zipSha -ne $expectedZipSha256) {
    throw "Zip SHA-256 mismatch: expected $expectedZipSha256, got $zipSha ($zipPath)"
}

$extract = Join-Path $cacheDir ("gst-prores-extract-" + [Guid]::NewGuid().ToString("N"))
try {
    Expand-Archive -LiteralPath $zipPath -DestinationPath $extract -Force
    $dlls = @(Get-ChildItem -LiteralPath $extract -Recurse -File -Filter $dllName)
    if ($dlls.Count -ne 1) {
        throw "Expected exactly one $dllName in the zip, found $($dlls.Count)"
    }
    $srcDir = $dlls[0].DirectoryName
    $csoNames = Assert-ProResPluginFiles $srcDir (Join-Path $srcDir "SHA256SUMS.txt")
    foreach ($name in $licenseFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $srcDir $name) -PathType Leaf)) {
            throw "$name not found next to $dllName in the zip"
        }
    }

    $destLicenses = Join-Path $dest $licensesDirName
    New-Item -ItemType Directory -Force -Path $destLicenses | Out-Null
    Copy-Item -LiteralPath (Join-Path $srcDir $dllName) -Destination (Join-Path $dest $dllName) -Force
    foreach ($name in $csoNames) {
        Copy-Item -LiteralPath (Join-Path $srcDir $name) -Destination (Join-Path $dest $name) -Force
    }
    foreach ($name in $licenseFiles) {
        Copy-Item -LiteralPath (Join-Path $srcDir $name) -Destination (Join-Path $destLicenses $name) -Force
    }
} finally {
    if (Test-Path -LiteralPath $extract) {
        Remove-Item -LiteralPath $extract -Recurse -Force
    }
}

# Check the placed copies too (stale or edited files in native\gst-prores stop here).
$placed = Assert-ProResPluginFiles $dest (Join-Path (Join-Path $dest $licensesDirName) "SHA256SUMS.txt")
Write-Host "OK: gst-prores-d3d11 $version ($dllName $expectedDllSha256, $($placed.Count) .cso, licenses) at native\gst-prores"
