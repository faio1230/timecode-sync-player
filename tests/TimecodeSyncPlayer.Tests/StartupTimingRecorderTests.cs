using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.6.4 段 6（設計書 3-2、#15）: 起動直後の UI の止まりの区間を切り分ける要約の行 `Startup timing: …`。
/// 観測だけで、起動 1 回に 1 行。各点はプロセスの開始からの ms で、起動の順に並ぶ。
/// </summary>
public class StartupTimingRecorderTests
{
    private static readonly DateTime ProcessStart = new(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc);

    private static DateTime At(double ms) => ProcessStart.AddMilliseconds(ms);

    private static StartupTimingRecorder RecordAll(List<string> lines)
    {
        var r = new StartupTimingRecorder(ProcessStart, fields => lines.Add("Startup timing: " + fields));
        r.Mark(StartupTimingPoint.MainWindowConstructor, At(214));
        r.Mark(StartupTimingPoint.OutputEngineCreated, At(219));
        r.Mark(StartupTimingPoint.WaitForDeviceStart, At(220));
        r.Mark(StartupTimingPoint.WaitForDeviceEnd, At(336));
        r.Mark(StartupTimingPoint.WindowLoaded, At(420));
        r.Mark(StartupTimingPoint.PlayerInitializeStart, At(430));
        r.Mark(StartupTimingPoint.PlayerInitializeEnd, At(1100));
        r.FirstHeartbeat(At(1126), lateMs: 805.1);
        return r;
    }

    [Fact]
    public void Line_ListsEveryPointInStartupOrder_AndEndsWithTheFirstHeartbeatLate()
    {
        var lines = new List<string>();
        RecordAll(lines);

        lines.Should().ContainSingle();
        lines[0].Should().Be(
            "Startup timing: mainWindowCtorMs=214 outputEngineCreatedMs=219 waitForDeviceStartMs=220 " +
            "waitForDeviceEndMs=336 windowLoadedMs=420 playerInitStartMs=430 playerInitEndMs=1100 " +
            "firstHeartbeatMs=1126 firstHeartbeatLateMs=805.1");
    }

    [Fact]
    public void Points_AreMonotonic_InTheOrderOfTheLine()
    {
        var lines = new List<string>();
        RecordAll(lines);

        List<double> points = Regex.Matches(lines[0], @"(\w+)Ms=(-?[\d.]+)")
            .Where(m => m.Groups[1].Value != "firstHeartbeatLate")
            .Select(m => double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture))
            .ToList();
        points.Should().HaveCount(8);
        points.Should().BeInAscendingOrder("区間の値は起動の順に単調に並ぶ");
    }

    [Fact]
    public void Line_IsWrittenOnce_AfterTheWindowIsLoadedAndTheFirstHeartbeat()
    {
        var lines = new List<string>();
        var r = new StartupTimingRecorder(ProcessStart, fields => lines.Add("Startup timing: " + fields));
        r.Mark(StartupTimingPoint.MainWindowConstructor, At(214));
        r.FirstHeartbeat(At(300), lateMs: 0.5);   // Loaded より前の seq=1
        lines.Should().BeEmpty("Window_Loaded（とその中の再生の初期化）の前には出さない");

        r.Mark(StartupTimingPoint.WindowLoaded, At(420));
        lines.Should().ContainSingle("Loaded の時点で seq=1 が済んでいれば、そこで 1 行");
        r.FirstHeartbeat(At(500), lateMs: 1.0);
        r.Mark(StartupTimingPoint.PlayerInitializeEnd, At(600));
        lines.Should().ContainSingle("起動 1 回に 1 行だけ");
        lines[0].Should().Contain("firstHeartbeatMs=300 firstHeartbeatLateMs=0.5");
    }

    [Fact]
    public void MissingPoints_AreWrittenAsMinusOne()
    {
        var lines = new List<string>();
        var r = new StartupTimingRecorder(ProcessStart, fields => lines.Add("Startup timing: " + fields));
        r.Mark(StartupTimingPoint.MainWindowConstructor, At(214));
        r.Mark(StartupTimingPoint.WindowLoaded, At(420));   // 再生が使えない構成: OutputEngine も初期化も無い
        r.FirstHeartbeat(At(450), lateMs: 2.0);

        lines.Should().ContainSingle().Which.Should().Be(
            "Startup timing: mainWindowCtorMs=214 outputEngineCreatedMs=-1 waitForDeviceStartMs=-1 " +
            "waitForDeviceEndMs=-1 windowLoadedMs=420 playerInitStartMs=-1 playerInitEndMs=-1 " +
            "firstHeartbeatMs=450 firstHeartbeatLateMs=2.0");
    }
}
