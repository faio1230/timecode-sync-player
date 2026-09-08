param(
    [Parameter(Mandatory=$true)][string]$VendorPath,
    [string]$OutputDirectory,
    [string]$VisualStudioPath
)
$ErrorActionPreference = 'Stop'
$vendor = (Resolve-Path -LiteralPath $VendorPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot 'bin/Debug/x64' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$files = @('SpoutDX.cpp','SpoutDirectX.cpp','SpoutFrameCount.cpp','SpoutSenderNames.cpp','SpoutSharedMemory.cpp','SpoutCopy.cpp','SpoutUtils.cpp')
foreach ($file in $files + @('SpoutDX.h')) {
    if (-not (Test-Path -LiteralPath (Join-Path $vendor $file) -PathType Leaf)) { throw "Missing vendor source: $file" }
}
if (-not $VisualStudioPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $VisualStudioPath = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($LASTEXITCODE -ne 0 -or -not $VisualStudioPath) { throw 'MSVC x64 build tools not found' }
}
$vcvars = Join-Path $VisualStudioPath 'VC/Auxiliary/Build/vcvars64.bat'
if (-not (Test-Path -LiteralPath $vcvars -PathType Leaf) -or $vcvars.Contains('"')) { throw 'Invalid vcvars64.bat path' }
# Read the compiler environment from a child cmd; compilation receives arguments
# directly. Vendor paths are never interpolated into shell command text.
$envCommand = 'call "{0}" >nul && set' -f $vcvars
$compilerEnvironment = & $env:ComSpec /d /c $envCommand
if ($LASTEXITCODE -ne 0) { throw 'vcvars64.bat failed' }
$savedEnvironment = @{}
try {
    foreach ($line in $compilerEnvironment) {
        if ($line -match '^([^=]+)=(.*)$') {
            $key = $matches[1]
            if ($key -in 'HOME','CODEX_HOME') { continue }
            $savedEnvironment[$key] = [Environment]::GetEnvironmentVariable($key,'Process')
            [Environment]::SetEnvironmentVariable($key,$matches[2],'Process')
        }
    }
    [IO.Directory]::CreateDirectory($output) | Out-Null
    # Preserve each upstream file's copyright/license blocks alongside the binary.
    $notices = foreach ($source in Get-ChildItem -LiteralPath $vendor -File | Where-Object { $_.Extension -in '.cpp','.h' }) {
        $contents = Get-Content -LiteralPath $source.FullName -Raw
        $headers = @([regex]::Matches($contents,'(?s)/\*.*?\*/') | Where-Object { $_.Value -match 'Copyright|Redistribution' })
        if (-not $headers.Count) { throw "Vendor file lacks expected retained notice: $($source.Name)" }
        "$($source.Name)`r`n$(($headers | ForEach-Object { $_.Value }) -join "`r`n")`r`n"
    }
    $notices | Set-Content -LiteralPath (Join-Path $output 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8
    # Instrument only the no-argument ReceiveTexture copy branch in a generated
    # file. Never edit the supplied vendor tree or assume a nearby SDK version
    # has the same function layout. The hook only increments a local counter.
    $originalSpoutSource = Join-Path $vendor 'SpoutDX.cpp'
    $spoutText = [IO.File]::ReadAllText($originalSpoutSource)
    $functionPattern = 'bool\s+spoutDX::ReceiveTexture\(\)\s*\{'
    $functionMatches = [regex]::Matches($spoutText, $functionPattern)
    if ($functionMatches.Count -ne 1) { throw 'Expected exactly one ReceiveTexture() definition' }
    $functionStart = $functionMatches[0].Index
    $nextFunctionPattern = 'bool\s+spoutDX::ReceiveTexture\(ID3D11Texture2D\*\*\s+ppTexture\)'
    $nextMatches = [regex]::Matches($spoutText, $nextFunctionPattern)
    if ($nextMatches.Count -ne 1 -or $nextMatches[0].Index -le $functionStart) {
        throw 'Cannot establish the exact ReceiveTexture() function boundary'
    }
    $bodyStart = $functionStart + $functionMatches[0].Length - 1
    $depth = 0
    $functionEnd = -1
    # Ignore comments/string/character literals when locating the matching brace.
    $tokens = [regex]::Matches($spoutText.Substring($bodyStart), '(?s)//[^\r\n]*|/\*.*?\*/|"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|[{}]')
    foreach ($token in $tokens) {
        if ($token.Value -eq '{') { $depth++ }
        elseif ($token.Value -eq '}') {
            $depth--
            if ($depth -eq 0) { $functionEnd = $bodyStart + $token.Index + 1; break }
        }
    }
    if ($functionEnd -le $bodyStart -or $functionEnd -ge $nextMatches[0].Index) {
        throw 'Cannot isolate the ReceiveTexture() function body'
    }
    $functionLength = $functionEnd - $functionStart
    $functionText = $spoutText.Substring($functionStart, $functionLength)
    $copyCall = 'm_pImmediateContext->CopyResource(m_pTexture, m_pSharedTexture);'
    if ([regex]::Matches($functionText, [regex]::Escape($copyCall)).Count -ne 1) {
        throw 'Expected exactly one shared-to-private copy inside ReceiveTexture()'
    }
    if ($spoutText.Contains('SpoutProbeSharedCopySubmitted')) { throw 'Vendor source already contains diagnostic hook' }
    $instrumentedFunction = $functionText.Replace($copyCall, $copyCall + "`r`n`t`t`t`tSpoutProbeSharedCopySubmitted();")
    $instrumentedText = 'extern "C" void SpoutProbeSharedCopySubmitted();' + "`r`n" +
        $spoutText.Substring(0, $functionStart) + $instrumentedFunction + $spoutText.Substring($functionStart + $functionLength)
    $generatedSpoutSource = Join-Path $output 'SpoutDX.probe.cpp'
    [IO.File]::WriteAllText($generatedSpoutSource, $instrumentedText, [Text.UTF8Encoding]::new($false))
    $originalSources = @($files | ForEach-Object { Join-Path $vendor $_ })
    $sources = @((Join-Path $PSScriptRoot 'Program.cpp')) + @($files | ForEach-Object {
        if ($_ -eq 'SpoutDX.cpp') { $generatedSpoutSource } else { Join-Path $vendor $_ }
    })
    $arguments = @('/nologo','/std:c++17','/EHsc','/Od','/Zi','/MDd','/DWIN32','/D_DEBUG','/D_CRT_SECURE_NO_WARNINGS',"/I$vendor","/Fo$output\","/Fd$output\SpoutReceiverProbe.pdb","/Fe$output\SpoutReceiverProbe.exe") + $sources + @('/link','/DEBUG','/MACHINE:X64','user32.lib','gdi32.lib','opengl32.lib')
    & cl.exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "Receiver build failed: $LASTEXITCODE" }
    $manifest = @($sources + $originalSources + @((Join-Path $PSScriptRoot 'build.ps1')) + @((Get-ChildItem -LiteralPath $vendor -File -Filter '*.h').FullName)) | Select-Object -Unique | ForEach-Object {
        [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    }
    $manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'build-inputs.json') -Encoding UTF8
    Get-FileHash -LiteralPath (Join-Path $output 'SpoutReceiverProbe.exe') -Algorithm SHA256
} finally {
    foreach ($key in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key,$savedEnvironment[$key],'Process') }
}
