#requires -Version 7.0
# Self-test of the crash count of the LTC runner (test infrastructure 2026-10, item 2):
# Get-TcsCrashSummary and friends in scripts\LtcRunMetrics.psm1, and the run-result.json fields
# ltc-run-report.ps1 writes from them (crashes, launches, proresGpuFirstLaunches, verdict,
# failReasons). Builds small fake report directories under %TEMP% (fake event-log records in
# crash-events.json, fake app logs, a fake trx and dump), runs ltc-run-report.ps1 on them
# (no -Prune) and checks the JSON. Reads no event log, starts no app; removes the folders at the end.
#
#   pwsh -NoProfile -File scripts\test-ltc-crash-count.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'LtcRunMetrics.psm1') -Force

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$Condition, [string]$Name) {
    if ($Condition) { Write-Output ('ok   ' + $Name) } else { Write-Output ('FAIL ' + $Name); $failures.Add($Name) }
}

$roots = New-Object System.Collections.Generic.List[string]
try {
    # The run: 22:00:00 to 22:10:00 local. S1 runs 22:00-22:03, S2 22:03-22:06, S3 22:06-22:10.
    $day = [DateTime]::new(2026, 10, 7, 22, 0, 0, [DateTimeKind]::Local)
    $offset = [TimeZoneInfo]::Local.GetUtcOffset($day)
    $zone = ($(if ($offset -lt [TimeSpan]::Zero) { '-' } else { '+' })) + $offset.ToString('hh\:mm')
    function AppLine([DateTime]$At, [string]$Text) { $At.ToString('yyyy-MM-dd HH:mm:ss.fff') + ' ' + $zone + ' [INF] ' + $Text }
    function Iso([DateTime]$At) { ([DateTimeOffset]$At).ToString('o') }
    function Utc([DateTime]$At) { $At.ToUniversalTime().ToString('o') }
    function Ev1026([DateTime]$At, [string]$App, [string]$Info) {
        [ordered]@{ timeCreated = (Iso $At); provider = '.NET Runtime'; id = 1026
            values = @("Application: $App`nCoreCLR Version: 8.0.2125.47513`n.NET Version: 8.0.21`nDescription: The process was terminated due to an unhandled exception.`nException Info: $Info`n") }
    }
    function Ev1000([DateTime]$At, [string]$App, [string]$Module, [string]$Code, [string]$Offset, [int]$ProcessId) {
        [ordered]@{ timeCreated = (Iso $At); provider = 'Application Error'; id = 1000
            values = @($App, '0.6.6.0', '69bc0000', $Module, '1.0.0.0', '28606b21', $Code, $Offset, [string]$ProcessId,
                '134358294707143201', "C:\app\$App", "C:\app\$Module", '9ac6e988-5ada-486e-b0b7-c44be12339cc', '', '') }
    }
    $avInfo = 'exception code c0000005, exception address 00007FFB77D9088F'

    # App log of the day: one launch before the run (not counted), then three in the run.
    #   L1 22:00:10 reads H.264 first           -> not ProRes GPU first
    #   L2 22:03:10 FetchMetadata proresd3d11dec 1.2 s after the launch -> ProRes GPU first (the crash)
    #   L3 22:06:10 proresd3d11dec 3 s after the launch                  -> not first (over 2 s)
    $appLog = @(
        (AppLine $day.AddHours(-1) '=== TimecodeSyncPlayer v0.6.6 起動 === ログ: C:\app\logs\timecodesyncplayer-.log'),
        (AppLine $day.AddHours(-1).AddSeconds(1) 'FetchMetadata: 3840x2160 59.940fps V:proresd3d11dec A:'),
        (AppLine $day.AddSeconds(10) '=== TimecodeSyncPlayer v0.6.6 起動 === ログ: C:\app\logs\timecodesyncplayer-.log'),
        (AppLine $day.AddSeconds(11) 'FetchMetadata: 1920x1080 25.000fps V:d3d11h264dec A:aac'),
        (AppLine $day.AddMinutes(3).AddSeconds(10) '=== TimecodeSyncPlayer v0.6.6 起動 === ログ: C:\app\logs\timecodesyncplayer-.log'),
        (AppLine $day.AddMinutes(3).AddSeconds(11.2) 'FetchMetadata: 3840x2160 59.940fps V:proresd3d11dec A:'),
        (AppLine $day.AddMinutes(6).AddSeconds(10) '=== TimecodeSyncPlayer v0.6.6 起動 === ログ: C:\app\logs\timecodesyncplayer-.log'),
        (AppLine $day.AddMinutes(6).AddSeconds(13) 'FetchMetadata: 3840x2160 59.940fps V:proresd3d11dec A:')
    )
    $trxText = @"
<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="$(Iso $day)" start="$(Iso $day)" finish="$(Iso $day.AddMinutes(10))" />
  <Results>
    <UnitTestResult testName="Ns.LtcScenarioE2ETests.S1_First" outcome="Passed" startTime="$(Iso $day)" endTime="$(Iso $day.AddMinutes(3))" />
    <UnitTestResult testName="Ns.LtcScenarioE2ETests.S2_Second" outcome="Failed" startTime="$(Iso $day.AddMinutes(3))" endTime="$(Iso $day.AddMinutes(6))" />
    <UnitTestResult testName="Ns.LtcScenarioE2ETests.S3_Third" outcome="Passed" startTime="$(Iso $day.AddMinutes(6))" endTime="$(Iso $day.AddMinutes(10))" />
  </Results>
</TestRun>
"@
    # Records that never count: a TimecodeSyncPlayer 1026 / 1000 before the window, and another
    # application's 1026 / 1000 inside the window.
    $outsideAndOthers = @(
        (Ev1026 $day.AddMinutes(-30) 'TimecodeSyncPlayer.exe' $avInfo),
        (Ev1000 $day.AddMinutes(-30).AddMilliseconds(100) 'TimecodeSyncPlayer.exe' 'tcs_gstreamer.dll' 'c0000005' '0x00000000000a1b2c' 1111),
        (Ev1026 $day.AddMinutes(4) 'shot.exe' 'System.TimeoutException: wait timeout'),
        (Ev1000 $day.AddMinutes(4).AddMilliseconds(100) 'shot.exe' 'KERNELBASE.dll' 'e0434352' '0x00000000000c41ca' 2222)
    )

    function New-FakeReport([string]$Tag, [object[]]$Events, [string[]]$DumpNames, [switch]$NoCrashEvents) {
        $root = Join-Path ([IO.Path]::GetTempPath()) ('tcs-ltc-crash-count-' + $Tag + '-' + [Guid]::NewGuid().ToString('N'))
        $roots.Add($root)
        New-Item -ItemType Directory -Force -Path (Join-Path $root 'app-logs') | Out-Null
        [IO.File]::WriteAllLines((Join-Path $root 'app-logs\timecodesyncplayer-20261007.log'), $appLog)
        $trxText | Set-Content -LiteralPath (Join-Path $root 'results.trx') -Encoding UTF8
        if (-not $NoCrashEvents) {
            [ordered]@{ schema = 'ltc-crash-events/1'; fromUtc = (Utc $day); testEndUtc = (Utc $day.AddMinutes(10))
                toUtc = (Utc $day.AddMinutes(10).AddSeconds(3)); error = $null; events = @($Events) } |
                ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $root 'crash-events.json') -Encoding UTF8
        }
        if ($DumpNames) {
            New-Item -ItemType Directory -Force -Path (Join-Path $root 'dumps') | Out-Null
            foreach ($name in $DumpNames) {
                $path = Join-Path $root ('dumps\' + $name)
                [IO.File]::WriteAllBytes($path, [byte[]](1, 2, 3))
                [IO.File]::SetLastWriteTime($path, $day.AddMinutes(3).AddSeconds(14))
            }
        }
        $out = @(& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'ltc-run-report.ps1') -ReportDir $root 2>&1 | ForEach-Object { [string]$_ })
        Check ($LASTEXITCODE -eq 0) ($Tag + ': ltc-run-report.ps1 exit 0 (' + ($out -join ' | ') + ')')
        return Get-Content -LiteralPath (Join-Path $root 'run-result.json') -Raw | ConvertFrom-Json
    }

    # ---- no crash: only records that must not count ----
    $zero = New-FakeReport 'zero' $outsideAndOthers @()
    Check ($zero.crashes.count -eq 0) 'zero: crashes.count 0 (a TimecodeSyncPlayer 1026 outside the window and another app 1026 in it are not counted)'
    Check ($zero.crashes.collected -eq $true) 'zero: crashes.collected true'
    Check ($zero.crashes.dotnetRuntime1026 -eq 0 -and $zero.crashes.applicationError1000 -eq 0) 'zero: 1026 / 1000 counts 0'
    Check (@($zero.crashes.items).Count -eq 0) 'zero: no crash items'
    Check ($zero.crashes.dumps.count -eq 0) 'zero: dumps.count 0 (no dumps folder)'
    Check ($zero.launches -eq 3) 'zero: launches 3 (the launch before the window is not counted)'
    Check ($zero.proresGpuFirstLaunches -eq 1) 'zero: proresGpuFirstLaunches 1 (FetchMetadata proresd3d11dec within 2 s of the launch)'
    Check ($zero.verdict -eq 'fail' -and @($zero.failReasons) -contains 'tests' -and -not (@($zero.failReasons) -contains 'crash')) `
        'zero: verdict fail from the failed test only (failReasons tests, no crash)'

    # ---- one crash: a 1026 and a 1000 of the same crash, 3 s after the ProRes GPU first launch ----
    $crashAt = $day.AddMinutes(3).AddSeconds(13)
    $oneEvents = @($outsideAndOthers) + @(
        (Ev1026 $crashAt 'TimecodeSyncPlayer.exe' $avInfo),
        (Ev1000 $crashAt.AddMilliseconds(120) 'TimecodeSyncPlayer.exe' 'tcs_gstreamer.dll' 'c0000005' '0x000000000009088f' 4242)
    )
    $one = New-FakeReport 'one' $oneEvents @('tsp-4242.dmp', 'tsp-9999.dmp')
    Check ($one.crashes.count -eq 1) 'one: crashes.count 1 (the 1026 and the 1000 of the same crash are one)'
    Check ($one.crashes.dotnetRuntime1026 -eq 1 -and $one.crashes.applicationError1000 -eq 1) 'one: 1026 = 1, 1000 = 1 (TimecodeSyncPlayer in the window only)'
    $item = @($one.crashes.items)[0]
    Check ($item.code -eq 'c0000005' -and $item.address -eq '00007FFB77D9088F') 'one: exception code and address from the 1026'
    Check ($item.module -eq 'tcs_gstreamer.dll' -and $item.offset -eq '000000000009088f' -and $item.pid -eq 4242) 'one: module, offset and pid from the 1000'
    Check ((@($item.sources) -join ',') -eq '1026,1000') 'one: sources 1026,1000'
    Check ($item.scenario -eq 'S2_Second') 'one: scenario from the trx start..end'
    Check ($item.proresGpuFirstLaunch -eq $true) 'one: assigned to the ProRes GPU first launch'
    Check ($item.secondsAfterLaunch -eq 3.0) 'one: 3.0 s after the launch'
    Check ($item.launchVersion -eq '0.6.6') 'one: launch version'
    Check ($item.dump -eq 'tsp-4242.dmp') 'one: dump by pid'
    Check ($one.crashes.dumps.count -eq 2 -and (@($one.crashes.dumps.names) -contains 'tsp-9999.dmp')) 'one: dumps count and names'
    Check ($one.launches -eq 3 -and $one.proresGpuFirstLaunches -eq 1) 'one: launches 3, proresGpuFirstLaunches 1'
    Check ($one.verdict -eq 'fail' -and @($one.failReasons) -contains 'crash') 'one: verdict fail, failReasons has crash'

    # ---- no crash-events.json (a run of an older runner) ----
    $old = New-FakeReport 'old' @() @() -NoCrashEvents
    Check ($null -eq $old.crashes.count -and $old.crashes.collected -eq $false) 'old: crashes.count null, collected false'
    Check ($old.launches -eq 3) 'old: launches counted in the trx start..finish'
    Check (-not (@($old.failReasons) -contains 'crash')) 'old: no crash reason'

    # ---- the functions directly ----
    # A 1000 with no 1026 (a crash the runtime did not see) is a crash of its own; its dump is
    # found by time when the pid has none.
    $only1000 = Get-TcsCrashSummary -Records @(
        (Ev1000 $day.AddMinutes(7) 'TimecodeSyncPlayer.exe' 'ntdll.dll' 'c0000374' '0x0000000000100000' 5555)
    ) -FromUtc $day.ToUniversalTime() -ToUtc $day.AddMinutes(10).ToUniversalTime() -AppLines $appLog `
        -DumpFiles @([pscustomobject]@{ Name = 'tsp-other.dmp'; LastWriteTimeUtc = $day.AddMinutes(7).AddSeconds(5).ToUniversalTime() })
    $c = @($only1000.crashes.items)[0]
    Check ($only1000.crashes.count -eq 1 -and $c.code -eq 'c0000374' -and $c.pid -eq 5555) 'function: a 1000 alone is one crash'
    Check ($c.dump -eq 'tsp-other.dmp') 'function: dump by time when no tsp-<pid>.dmp'
    Check ($c.proresGpuFirstLaunch -eq $false) 'function: assigned to the launch at 22:06:10 (not ProRes GPU first)'
    # Two crashes a moment apart each with its 1026: both counted, one 1000 pairs with the nearer one.
    $two = Get-TcsCrashSummary -Records @(
        (Ev1026 $day.AddMinutes(1) 'TimecodeSyncPlayer.exe' $avInfo),
        (Ev1026 $day.AddMinutes(1).AddSeconds(4) 'TimecodeSyncPlayer.exe' $avInfo),
        (Ev1000 $day.AddMinutes(1).AddSeconds(4.1) 'TimecodeSyncPlayer.exe' 'x.dll' 'c0000005' '0x1' 6666)
    ) -FromUtc $day.ToUniversalTime() -ToUtc $day.AddMinutes(10).ToUniversalTime()
    Check ($two.crashes.count -eq 2) 'function: two 1026 are two crashes'
    Check (@($two.crashes.items)[1].pid -eq 6666 -and $null -eq @($two.crashes.items)[0].pid) 'function: the 1000 pairs with the nearer 1026'
    $launches = @(Get-TcsAppLaunches -AppLines $appLog)
    Check ($launches.Count -eq 4 -and $launches[0].proresGpuFirst) 'function: launches without a window (4, the earlier one is ProRes GPU first)'

    Write-Output '--- run-result of the one-crash fake run (crashes, launches, proresGpuFirstLaunches, verdict):'
    [ordered]@{ crashes = $one.crashes; launches = $one.launches; proresGpuFirstLaunches = $one.proresGpuFirstLaunches
        verdict = $one.verdict; failReasons = $one.failReasons } | ConvertTo-Json -Depth 6
} finally {
    foreach ($root in $roots) { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Output ('FAILED ' + $failures.Count)
    exit 1
}
Write-Output 'ALL OK'
