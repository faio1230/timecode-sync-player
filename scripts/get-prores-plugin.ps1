# Fetch the pinned gst-prores-d3d11 plugin (proresd3d11dec) for development.
# Downloads the release zip into artifacts\cache (reused when present), checks
# the SHA-256 of the zip and of gstproresd3d11.dll against the pinned values,
# and places the DLL and its prores_*.cso shaders in native\gst-prores.
# The DLL name must not change (GStreamer derives the entry point from it).
# Usage (from the repository root):
#   powershell -File scripts\get-prores-plugin.ps1
$ErrorActionPreference = "Stop"

# Pinned version, URL and hashes (single place).
# v0.2.0 is the release bundled with v0.6.0 (fixed 2026-09-30).
$version = "v0.2.0"
$zipName = "gst-prores-d3d11-$version-win64-gst1.28.2.zip"
$zipUrl = "https://github.com/faio1230/gst-prores-d3d11/releases/download/$version/$zipName"
$expectedZipSha256 = "b49fdd041021e1d7a9bd1ed3548f7609ff8d901ad3f56d5bf3c8fa4f8efd8ff8"
$dllName = "gstproresd3d11.dll"
$expectedDllSha256 = "ee8dc3f7631ff077e9acd6c12c9584cdd05e91a954582da911020736abba34f3"
$expectedCsoCount = 6

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cacheDir = Join-Path $repoRoot "artifacts\cache"
$zipPath = Join-Path $cacheDir $zipName
$dest = Join-Path $repoRoot "native\gst-prores"

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

if (Test-Path -LiteralPath $zipPath) {
    Write-Host "Using cached $zipName; verifying..."
} else {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $partial = "$zipPath.partial"
    Write-Host "Downloading $zipUrl"
    $oldProgress = $ProgressPreference
    $ProgressPreference = "SilentlyContinue"
    try {
        Invoke-WebRequest -Uri $zipUrl -OutFile $partial -UseBasicParsing
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
    $dll = $dlls[0]
    $dllSha = Get-Sha256 $dll.FullName
    if ($dllSha -ne $expectedDllSha256) {
        throw "$dllName SHA-256 mismatch: expected $expectedDllSha256, got $dllSha"
    }
    $csos = @(Get-ChildItem -LiteralPath $dll.DirectoryName -File -Filter "prores_*.cso")
    if ($csos.Count -ne $expectedCsoCount) {
        throw "Expected $expectedCsoCount prores_*.cso next to $dllName, found $($csos.Count)"
    }

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    Copy-Item -LiteralPath $dll.FullName -Destination (Join-Path $dest $dllName) -Force
    foreach ($cso in $csos) {
        Copy-Item -LiteralPath $cso.FullName -Destination (Join-Path $dest $cso.Name) -Force
    }
} finally {
    if (Test-Path -LiteralPath $extract) {
        Remove-Item -LiteralPath $extract -Recurse -Force
    }
}

$placedSha = Get-Sha256 (Join-Path $dest $dllName)
if ($placedSha -ne $expectedDllSha256) {
    throw "$dllName SHA-256 mismatch after copy: got $placedSha"
}
Write-Host "OK: gst-prores-d3d11 $version ($dllName $placedSha, $($csos.Count) .cso) at native\gst-prores"
