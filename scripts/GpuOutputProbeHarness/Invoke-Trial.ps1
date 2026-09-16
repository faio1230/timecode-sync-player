# One explicitly requested trial + analysis. Run serially from the worktree root; the parent evaluates each result before the next.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('off','signal')][string]$CopyRetry,
    [Parameter(Mandatory)][double]$SendPhaseMs,
    [ValidateSet('tick','ready','vsync','vblank')][string]$DisplayPacing = 'tick',
    [double]$PresentMarginMs = 3,
    [ValidateSet('off','vblank')][string]$ComposeAlign = 'off',
    [double]$ComposeLeadMs = 1.5,
    [ValidateSet('keyed','fence')][string]$SourceSync = 'keyed',
    [ValidateSet('pattern','contract-fake')][string]$Source = 'pattern',
    [string]$SourceSize = '',
    [int]$Width = 3840,
    [int]$Height = 2160,
    [int]$Seconds = 32,
    [int]$Warmup = 5,
    [int]$MonitorIndex = 1,
    [switch]$Windowed,
    [Parameter(Mandatory)][string]$LogRoot
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Set-Location $root
$runner = Join-Path $root 'scripts\GpuOutputProbeHarness\Run-GpuOutputProbe.ps1'
$app = Join-Path $root 'scripts\GpuOutputProbe\bin\Debug\net8.0-windows\GpuOutputProbe.exe'
$receiver = Join-Path $env:USERPROFILE 'Downloads\Spout-SDK-examples_2-007-017\Spout-SDK-examples\Examples_2-007-017\SpoutDX\WinSpoutDXreceiver.exe'
$before = @(Get-ChildItem -Directory (Join-Path $root $LogRoot) -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName })
$args = @{
    AppExe=$app; ReceiverExe=$receiver; ReceiverMode='official'; Mode='split'; Output='both'
    Width=$Width; Height=$Height; Fps=60; MutexWaitMs=8; PresentWaitMs=0; PresentWaitPlan='fixed'
    DisplayPacing=$DisplayPacing; PresentMarginMs=$PresentMarginMs; ComposeAlign=$ComposeAlign; ComposeLeadMs=$ComposeLeadMs; CopyRetry=$CopyRetry; SourceSync=$SourceSync; Source=$Source; SourceSize=$SourceSize; SendPhaseMs=$SendPhaseMs
    Seconds=$Seconds; Warmup=$Warmup; MonitorIndex=$MonitorIndex; LogRoot=(Join-Path $root $LogRoot)
}
if ($Windowed) { $args.Windowed = $true }
& $runner @args | Out-Null
$run = Get-ChildItem -Directory (Join-Path $root $LogRoot) | Where-Object { $before -notcontains $_.FullName } | Sort-Object Name | Select-Object -Last 1
Write-Output ("RUN " + $run.FullName)
python (Join-Path $root 'scripts\GpuOutputProbeHarness\analyze_probe.py') (Join-Path $run.FullName 'app')
Write-Output ("analyze exit=" + $LASTEXITCODE)
Get-Content (Join-Path $run.FullName 'runner-result.json') | ConvertFrom-Json | Select-Object completedNormally, appExit, receiverExit, receiverForced, appStillRunning, timedOut, error | Format-List
Get-Process | Where-Object { $_.ProcessName -match 'GpuOutputProbe|WinSpoutDXreceiver' } | Select-Object Id, ProcessName, StartTime
