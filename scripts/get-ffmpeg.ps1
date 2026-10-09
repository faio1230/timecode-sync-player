#requires -Version 7.0
# Fetch the pinned ffmpeg build the tests use to make their media (test infrastructure
# 2026-10, item 8). Downloads the release zip into artifacts\cache, checks the SHA-256 of
# the zip and of ffmpeg.exe / ffprobe.exe against the pinned values, and places
# ffmpeg.exe, ffprobe.exe, LICENSE and README.txt in tools\ffmpeg (git-ignored). The zip
# is deleted after a successful placement (it is 230 MB; run again to fetch it again).
# When tools\ffmpeg already holds the pinned files, nothing is downloaded.
#
# License: this build is GPL v3 (gyan.dev "full" build). It is a test tool only: never
# put it in the release package or the installer (package-release.ps1 does not read tools\).
#
# Source: the GitHub release 8.0.1 of GyanD/codexffmpeg (the GitHub mirror of the
# gyan.dev Windows builds). A release asset keeps its name and content, so the same
# URL gives the same bytes again; the gyan.dev site itself only keeps the latest
# release under a moving name. The full build is used because the E2E fixtures need
# the hap encoder (libsnappy) and prores_ks, and libx264 / libx265 / libaom.
#
# The tests and the scripts look for ffmpeg in this order (scripts\TcsFfmpeg.psm1,
# tests Helpers\FfmpegTool.cs): TCS_FFMPEG, tools\ffmpeg, C:\Program Files\ffmpeg\bin,
# PATH. A build older than 6 stops the LTC runner and the tests before they start.
#
# Usage (from the repository root):
#   pwsh -File scripts\get-ffmpeg.ps1
param()
$ErrorActionPreference = "Stop"

# Pinned version, URL and hashes (single place).
$version = "8.0.1"
$zipName = "ffmpeg-$version-full_build.zip"
$zipUrl = "https://github.com/GyanD/codexffmpeg/releases/download/$version/$zipName"
$expectedZipSha256 = "467cde100a47ed4b03a897988aeb4a296890c1e2b2d2864204657d002bc5fb90"
$expectedFfmpegSha256 = "74db6c184a03dba2bdfe23e1a1f41cf5a8385bc1de6a7a1b26db1dc541abef93"
$expectedFfprobeSha256 = "55bb6c6289367ae2383efa86b26bf2596f8adb72ac747360eb13df162354161c"
$expectedVersionPrefix = "ffmpeg version $version-full_build-www.gyan.dev "
$requiredEncoders = @("hap", "prores_ks", "libx264")

$repoRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$cacheDir = Join-Path $repoRoot "artifacts\cache"
$zipPath = Join-Path $cacheDir $zipName
$dest = Join-Path $repoRoot "tools\ffmpeg"
$pinned = [ordered]@{ "ffmpeg.exe" = $expectedFfmpegSha256; "ffprobe.exe" = $expectedFfprobeSha256 }
$otherFiles = @("LICENSE", "README.txt")

function Get-Sha256([string]$path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant()
}

# $null when every pinned file in $dir matches, else the first mismatch.
function Get-PinnedMismatch([string]$dir) {
    foreach ($name in $pinned.Keys) {
        $path = Join-Path $dir $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "$name not found in $dir" }
        $sha = Get-Sha256 $path
        if ($sha -ne $pinned[$name]) { return "$name SHA-256 mismatch: expected $($pinned[$name]), got $sha ($path)" }
    }
    return $null
}

# Runs the placed ffmpeg once: the version line and the encoders the fixtures need.
function Assert-PlacedBuild([string]$dir) {
    $exe = Join-Path $dir "ffmpeg.exe"
    $versionLine = [string](@(& $exe -hide_banner -version 2>&1) | Select-Object -First 1)
    if ($LASTEXITCODE -ne 0 -or -not $versionLine.StartsWith($expectedVersionPrefix)) {
        throw "Unexpected ffmpeg -version: $versionLine ($exe)"
    }
    $encoders = @(& $exe -hide_banner -encoders 2>&1 | ForEach-Object { [string]$_ })
    foreach ($name in $requiredEncoders) {
        if (-not ($encoders | Where-Object { $_ -match ('^\s*V\S*\s+' + [regex]::Escape($name) + '\s') })) {
            throw "Encoder $name not found in $exe"
        }
    }
    $probeLine = [string](@(& (Join-Path $dir "ffprobe.exe") -hide_banner -version 2>&1) | Select-Object -First 1)
    return "$versionLine | ffprobe: $probeLine | encoders: $($requiredEncoders -join ', ')"
}

if ((Test-Path -LiteralPath $dest) -and -not (Get-PinnedMismatch $dest)) {
    Write-Host "Already placed; verifying..."
    $summary = Assert-PlacedBuild $dest
    Write-Host "OK: ffmpeg $version (pinned) at tools\ffmpeg: $summary"
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

# Only the four files are taken out of the zip (the whole build is about 500 MB unpacked).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$staging = Join-Path $cacheDir ("ffmpeg-extract-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    $archive = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $prefix = "ffmpeg-$version-full_build/"
        $wanted = [ordered]@{
            "ffmpeg.exe" = $prefix + "bin/ffmpeg.exe"; "ffprobe.exe" = $prefix + "bin/ffprobe.exe"
            "LICENSE" = $prefix + "LICENSE"; "README.txt" = $prefix + "README.txt"
        }
        foreach ($name in $wanted.Keys) {
            $entry = $archive.GetEntry($wanted[$name])
            if ($null -eq $entry) { throw "$($wanted[$name]) not found in $zipName" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $name), $true)
        }
    } finally {
        $archive.Dispose()
    }
    $mismatch = Get-PinnedMismatch $staging
    if ($mismatch) { throw $mismatch }

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    foreach ($name in @($pinned.Keys) + $otherFiles) {
        Copy-Item -LiteralPath (Join-Path $staging $name) -Destination (Join-Path $dest $name) -Force
    }
} finally {
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
}

# Check the placed copies too, then run them once.
$mismatch = Get-PinnedMismatch $dest
if ($mismatch) { throw $mismatch }
$summary = Assert-PlacedBuild $dest
Remove-Item -LiteralPath $zipPath -Force
Write-Host "OK: ffmpeg $version (pinned, zip $expectedZipSha256) at tools\ffmpeg: $summary"
