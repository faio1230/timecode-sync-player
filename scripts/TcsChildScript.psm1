#requires -Version 7.0
# Runs a helper script in a child pwsh, so whatever it does to its process
# ($env:PATH, other variables, the current directory, loaded modules) stays there.
#
# #43 (test infrastructure bundle): run-ltc-scenarios.ps1 called
# make-ltc-scenario-project.ps1 with "&" in its own process. That script appended
# -FfmpegDir to PATH, the change stayed on the runner's PATH, and the test host and
# the app inherited it: a run on a machine without ffprobe on PATH saw ffprobe.
#
#   Import-Module (Join-Path $PSScriptRoot 'TcsChildScript.psm1') -Force
#   $exit = Invoke-TcsChildScript -ScriptPath $script -Arguments @{ MediaDir = $dir; Out = $out } -LogPath $log
#
# Arguments: one "-Name value" pair per hashtable entry, in name order. Arrays are
# joined with ","; doubles and other numbers use the invariant culture. A $true
# value is passed as a bare switch, $false and $null are left out. Every output
# stream of the child (stdout and stderr) goes to LogPath, replacing it, as "*>"
# did before. Returns the child's exit code (0 = success, 1 = the script threw).

Set-StrictMode -Version 3.0

function ConvertTo-TcsChildArgumentList {
    param([System.Collections.IDictionary]$Arguments)
    $list = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Arguments) { return @() }
    foreach ($key in @($Arguments.Keys | Sort-Object)) {
        $value = $Arguments[$key]
        if ($null -eq $value) { continue }
        if ($value -is [bool] -or $value -is [System.Management.Automation.SwitchParameter]) {
            if ([bool]$value) { $list.Add('-' + $key) }
            continue
        }
        if ($value -is [System.Array]) {
            $text = (@($value | ForEach-Object { [System.Convert]::ToString($_, [Globalization.CultureInfo]::InvariantCulture) }) -join ',')
        } else {
            $text = [System.Convert]::ToString($value, [Globalization.CultureInfo]::InvariantCulture)
        }
        $list.Add('-' + $key)
        $list.Add($text)
    }
    return , $list.ToArray()
}

function Invoke-TcsChildScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [System.Collections.IDictionary]$Arguments = @{},
        [Parameter(Mandatory = $true)][string]$LogPath
    )
    if (-not (Test-Path -LiteralPath $ScriptPath -PathType Leaf)) {
        throw ('script not found: ' + $ScriptPath)
    }
    $argList = ConvertTo-TcsChildArgumentList -Arguments $Arguments
    & pwsh -NoProfile -NonInteractive -File $ScriptPath @argList *> $LogPath
    return $LASTEXITCODE
}

Export-ModuleMember -Function Invoke-TcsChildScript, ConvertTo-TcsChildArgumentList
