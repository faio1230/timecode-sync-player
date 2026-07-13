# TimecodeSyncPlayer verification wrapper.
# Provides short profiles for automated pre-checks and log-only diagnostics.

[CmdletBinding()]
param(
    [ValidateSet("Full", "Quick", "Strict", "LogOnly")]
    [string]$Profile = "Full",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [string]$LogPath,

    [string]$ReportDirectory
)

$ErrorActionPreference = "Stop"
[Console]::InputEncoding = [System.Text.Encoding]::UTF8
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$diagnosticsScript = Join-Path $PSScriptRoot "run-timecodesyncplayer-diagnostics.ps1"
$diagnosticsSelfTestScript = Join-Path $PSScriptRoot "test-timecodesyncplayer-diagnostics.ps1"
if (-not (Test-Path -LiteralPath $diagnosticsScript)) {
    throw "Diagnostics runner was not found: $diagnosticsScript"
}

$diagnosticsArgs = @(
    "-Configuration", $Configuration
)

if (-not [string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $diagnosticsArgs += @("-ReportDirectory", $ReportDirectory)
}

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $diagnosticsArgs += @("-LogPath", $LogPath)
}

switch ($Profile) {
    "Full" {
        Write-Host "Running TimecodeSyncPlayer verification: Full"
    }
    "Quick" {
        Write-Host "Running TimecodeSyncPlayer verification: Quick"
        $diagnosticsArgs += "-SkipE2E"
    }
    "Strict" {
        Write-Host "Running TimecodeSyncPlayer verification: Strict"
        if (Test-Path -LiteralPath $diagnosticsSelfTestScript) {
            & powershell -NoProfile -ExecutionPolicy Bypass -File $diagnosticsSelfTestScript
            if ($LASTEXITCODE -ne 0) {
                exit $LASTEXITCODE
            }
        }
        $diagnosticsArgs += @(
            "-TreatWarningsAsFailure",
            "-PlaybackPerfWarningThreshold", "50",
            "-LoadFileBurstThreshold", "2",
            "-SyncSeekBurstThreshold", "3"
        )
    }
    "LogOnly" {
        Write-Host "Running TimecodeSyncPlayer verification: LogOnly"
        $diagnosticsArgs += @(
            "-SkipBuild",
            "-SkipNonE2E",
            "-SkipE2E"
        )
    }
}

& powershell -NoProfile -ExecutionPolicy Bypass -File $diagnosticsScript @diagnosticsArgs
exit $LASTEXITCODE
