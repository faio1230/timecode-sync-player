# Fetch the pinned Spout2 SDK sources used to build tcs_gstreamer.dll.
# Tag 2.007.017 / commit f49e2f469f8cb25f559a6eaa61a3f5b8173fc100
# Usage (from the repository root):
#   powershell -File native/gst-shim/get-spout.ps1
$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
$dest = Join-Path $repoRoot "vendor/Spout2"
$tag = "2.007.017"
$expectedCommit = "f49e2f469f8cb25f559a6eaa61a3f5b8173fc100"

if (Test-Path (Join-Path $dest ".git")) {
    Write-Host "vendor/Spout2 already exists; verifying..."
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
    git clone --depth 1 --branch $tag https://github.com/leadedge/Spout2 $dest
}

$commit = (git -C $dest rev-parse HEAD).Trim()
if ($commit -ne $expectedCommit) {
    throw "Spout2 commit mismatch: expected $expectedCommit, got $commit"
}
Write-Host "OK: Spout2 $tag ($commit) at vendor/Spout2"
