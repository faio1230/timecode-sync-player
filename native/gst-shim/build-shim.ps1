# Build script for tcs_gstreamer shim (DLL + native smoke test).
# Usage: powershell -File build-shim.ps1 [-Config Debug|Release]
param(
    [string]$Config = "Debug"
)
$ErrorActionPreference = "Stop"
$proj = Split-Path -Parent $MyInvocation.MyCommand.Path

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) { throw "Visual Studio Build Tools with VC tools not found" }
$vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat"
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found at $vcvars" }

$buildDir = Join-Path $proj "build-$($Config.ToLower())"
$cmd = "`"$vcvars`" && cmake -S `"$proj`" -B `"$buildDir`" -G Ninja -DCMAKE_BUILD_TYPE=$Config && cmake --build `"$buildDir`""
cmd /c $cmd
if ($LASTEXITCODE -ne 0) { throw "native build failed (exit $LASTEXITCODE)" }
Write-Host "OK: $buildDir\tcs_gstreamer.dll"
