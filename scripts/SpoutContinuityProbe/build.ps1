param(
    [string]$VendorPath,
    [string]$OutputDirectory,
    [string]$VisualStudioPath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $VendorPath) { $VendorPath = Join-Path $repoRoot 'vendor\Spout2\SPOUTSDK' }
$vendor = (Resolve-Path -LiteralPath $VendorPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'bin\Debug\x64' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$spoutGl = Join-Path $vendor 'SpoutGL'
$spoutDx = Join-Path $vendor 'SpoutDirectX\SpoutDX'
$sources = @(
    (Join-Path $PSScriptRoot 'Program.cpp'),
    (Join-Path $spoutGl 'Spout.cpp'),
    (Join-Path $spoutGl 'SpoutCopy.cpp'),
    (Join-Path $spoutGl 'SpoutDirectX.cpp'),
    (Join-Path $spoutGl 'SpoutFrameCount.cpp'),
    (Join-Path $spoutGl 'SpoutGL.cpp'),
    (Join-Path $spoutGl 'SpoutGLextensions.cpp'),
    (Join-Path $spoutGl 'SpoutReceiver.cpp'),
    (Join-Path $spoutGl 'SpoutSender.cpp'),
    (Join-Path $spoutGl 'SpoutSenderNames.cpp'),
    (Join-Path $spoutGl 'SpoutSharedMemory.cpp'),
    (Join-Path $spoutGl 'SpoutUtils.cpp'),
    (Join-Path $spoutDx 'SpoutDX.cpp')
)
foreach ($source in $sources + @((Join-Path $spoutDx 'SpoutDX.h'))) {
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing source: $source" }
}
if (-not $VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioPath) { throw 'MSVC x64 build tools not found' }
}
$vcvars = Join-Path $VisualStudioPath 'VC\Auxiliary\Build\vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf) -or $vcvars.Contains('"')) { throw 'Invalid vcvars64.bat path' }
$compilerEnvironment = & $env:ComSpec /d /c ('call "{0}" >nul && set' -f $vcvars)
if ($LASTEXITCODE -ne 0) { throw 'vcvars64.bat failed' }
$savedEnvironment = @{}
try {
    foreach ($line in $compilerEnvironment) {
        if ($line -match '^([^=]+)=(.*)$') {
            $key = $matches[1]
            if ($key -in 'HOME','CODEX_HOME') { continue }
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key, 'Process')
            [Environment]::SetEnvironmentVariable($key, $matches[2], 'Process')
        }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    $arguments = @(
        '/nologo','/std:c++17','/EHsc','/Od','/Zi','/MDd','/DWIN32','/D_DEBUG',
        '/DUNICODE','/D_UNICODE','/DSPOUT_BUILD_STATIC','/D_CRT_SECURE_NO_WARNINGS',
        "/I$spoutGl", "/I$spoutDx", "/Fo$output\", "/Fd$output\SpoutContinuityProbe.pdb",
        "/Fe$output\SpoutContinuityProbe.exe"
    ) + $sources + @(
        '/link','/DEBUG','/MACHINE:X64','user32.lib','gdi32.lib','opengl32.lib','d3d9.lib',
        'd3d11.lib','dxgi.lib','shell32.lib','ole32.lib','uuid.lib','winmm.lib','version.lib','advapi32.lib'
    )
    & cl.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Receiver build failed: $LASTEXITCODE" }
    $manifestInputs = @($sources + @((Join-Path $spoutDx 'SpoutDX.h'), $PSCommandPath)) | Select-Object -Unique
    $manifest = $manifestInputs | ForEach-Object {
        [pscustomobject]@{ path = $_; sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash }
    }
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'build-inputs.json') -Encoding UTF8
    $license = Join-Path (Split-Path $vendor -Parent) 'LICENSE'
    if (Test-Path -LiteralPath $license -PathType Leaf) {
        Copy-Item -LiteralPath $license -Destination (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Force
    }
    Get-FileHash -LiteralPath (Join-Path $output 'SpoutContinuityProbe.exe') -Algorithm SHA256
}
finally {
    foreach ($key in $savedEnvironment.Keys) {
        [Environment]::SetEnvironmentVariable($key, $savedEnvironment[$key], 'Process')
    }
}
