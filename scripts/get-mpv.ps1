[CmdletBinding()]
param(
    [string]$DestinationDirectory,
    [switch]$Force,
    [switch]$AllowUnverified
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

Set-StrictMode -Version 2.0
[Net.ServicePointManager]::SecurityProtocol =
    [Net.ServicePointManager]::SecurityProtocol -bor
    [Net.SecurityProtocolType]::SystemDefault -bor
    [Net.SecurityProtocolType]::Tls12

if ([string]::IsNullOrWhiteSpace($DestinationDirectory)) {
    $DestinationDirectory = Join-Path $PSScriptRoot "..\native"
}

$releaseApi = "https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/latest"
$sevenZipUrl = "https://www.7-zip.org/a/7zr.exe"
$sevenZipSha256 = "56B8CC9F4971CEF253644FAFE54063ED7FDCA551D4DEE0F8C6BAA81B855ACD72"
$assetPattern = "^mpv-dev-x86_64-\d{8}-git-[^.]+\.7z$"
$destination = [IO.Path]::GetFullPath($DestinationDirectory)
$destinationDll = Join-Path $destination "libmpv-2.dll"
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("TimecodeSyncPlayer-mpv-" + [Guid]::NewGuid().ToString("N"))

function Write-Step([string]$message) {
    Write-Host ("[get-mpv] " + $message)
}

try {
    Write-Step "Resolving the latest Windows x64 libmpv release..."
    $headers = @{
        "Accept" = "application/vnd.github+json"
        "User-Agent" = "TimecodeSyncPlayer-get-mpv"
    }
    $release = Invoke-RestMethod -Uri $releaseApi -Headers $headers
    $assets = @($release.assets | Where-Object { $_.name -match $assetPattern })
    if ($assets.Count -ne 1) {
        throw "Could not select exactly one asset. Pattern: $assetPattern / Count: $($assets.Count)"
    }

    $asset = $assets[0]
    if ([string]::IsNullOrWhiteSpace($asset.browser_download_url)) {
        throw "The GitHub release asset has no download URL: $($asset.name)"
    }

    if (Test-Path -LiteralPath $destinationDll) {
        if (-not $Force) {
            $answer = Read-Host "Overwrite the existing libmpv-2.dll? [y/N]"
            if ($answer -notmatch "^[Yy]$") {
                Write-Step "Canceled. The existing DLL was not changed."
                return
            }
        }
        Write-Step "Overwriting the existing libmpv-2.dll."
    }

    New-Item -ItemType Directory -Path $tempRoot | Out-Null
    $archivePath = Join-Path $tempRoot $asset.name
    $sevenZipPath = Join-Path $tempRoot "7zr.exe"
    $extractDirectory = Join-Path $tempRoot "extracted"
    New-Item -ItemType Directory -Path $extractDirectory | Out-Null

    Write-Step "Downloading libmpv: $($asset.name)"
    Invoke-WebRequest -Uri $asset.browser_download_url -Headers $headers -OutFile $archivePath -UseBasicParsing

    $hasVerifiableDigest =
        $asset.PSObject.Properties.Name -contains "digest" -and
        -not [string]::IsNullOrWhiteSpace($asset.digest) -and
        $asset.digest -match "^sha256:(?<hash>[0-9a-fA-F]{64})$"
    if ($hasVerifiableDigest) {
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
        if ($actualHash -ne $Matches.hash) {
            throw "Archive SHA-256 mismatch. expected=$($Matches.hash) actual=$actualHash"
        }
        Write-Step "Verified the archive SHA-256."
    }
    else {
        $warning = "The selected GitHub asset has no valid SHA-256 digest, so its integrity cannot be verified: $($asset.name)"
        Write-Warning ("SECURITY WARNING: " + $warning)
        if (-not $AllowUnverified) {
            throw ($warning + ". Refusing to continue. Use -AllowUnverified only after independently verifying the asset.")
        }
        Write-Warning "Continuing because -AllowUnverified was explicitly specified."
    }

    Write-Step "Downloading the official 7zr.exe..."
    Invoke-WebRequest -Uri $sevenZipUrl -OutFile $sevenZipPath -UseBasicParsing
    $actualSevenZipHash = (Get-FileHash -LiteralPath $sevenZipPath -Algorithm SHA256).Hash
    if ($actualSevenZipHash -ne $sevenZipSha256) {
        throw "7zr.exe SHA-256 mismatch. expected=$sevenZipSha256 actual=$actualSevenZipHash"
    }
    Write-Step "Verified the pinned 7zr.exe SHA-256 (7-Zip 26.02)."

    Write-Step "Extracting the archive..."
    & $sevenZipPath x $archivePath ("-o" + $extractDirectory) -y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "7zr.exe failed with exit code $LASTEXITCODE."
    }

    $dlls = @(Get-ChildItem -LiteralPath $extractDirectory -Filter "libmpv-2.dll" -File -Recurse)
    if ($dlls.Count -ne 1) {
        throw "Could not find exactly one extracted libmpv-2.dll. Count: $($dlls.Count)"
    }

    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Copy-Item -LiteralPath $dlls[0].FullName -Destination $destinationDll -Force
    $installed = Get-Item -LiteralPath $destinationDll
    if ($installed.Length -le 0) {
        throw "The installed libmpv-2.dll is empty: $destinationDll"
    }

    Write-Step ("Installed: {0} ({1:N0} bytes)" -f $destinationDll, $installed.Length)
}
catch {
    Write-Error ("libmpv installation failed: " + $_.Exception.Message)
    exit 1
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
