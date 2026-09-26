using FluentAssertions;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C4: S-1〜S-4・C-2 相当の台本を回し、同期シークの本数が段 0
/// （docs/design/v0.5.4-gate-baseline.md §5.4/§5.6）と矛盾しないことを記録する。
/// sync.gate の Debug 行は ScenarioLogSink が仮想時刻つきで拾う（製品コードは変えない）。
/// </summary>
[Collection("Serilog global logger")]
public class ScenarioGateBaselineTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private const int BaseMilliseconds = 50_000;
    private readonly ITestOutputHelper _output;

    public ScenarioGateBaselineTests(ITestOutputHelper output) => _output = output;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        return (h, clock);
    }

    /// <summary>
    /// 台本のカーソルまで進める（長い無音を挟まない。無音は 250ms で信号断になるため）。
    /// カーソルは「次に足す区間の開始」なので、足した区間のフレームまで届いたら止まる。
    /// </summary>
    private static void RunToCursor(SyncScenarioHarness h, ScenarioClock clock)
    {
        long end = h.Ltc.NextMilliseconds + 40;
        int guard = 0;
        while (clock.MonotonicMilliseconds < end && guard++ < 100_000)
            h.AdvanceMilliseconds(40);
    }

    private sealed record ScenarioCounts(int SyncSeeks, int LandingSeeks, int TotalSeeks, int Timeouts, int GateLines);

    private static ScenarioCounts Counts(SyncScenarioHarness h, ScenarioLogSink sink) =>
        new(ScenarioMetrics.SyncSeekCount(h),
            ScenarioMetrics.LandingSeekCount(h),
            ScenarioMetrics.SeekCount(h),
            sink.Count("pending-timeout"),
            sink.GateEvents.Count);

    private void Report(string name, ScenarioCounts counts, ScenarioLogSink sink)
    {
        _output.WriteLine(
            $"{name}: syncSeeks={counts.SyncSeeks} landingSeeks={counts.LandingSeeks} " +
            $"totalSeeks={counts.TotalSeeks} timeouts={counts.Timeouts} gateLines={counts.GateLines}");
        foreach (IGrouping<string, ScenarioGateEvent> group in sink.GateEvents
                     .GroupBy(e => e.Name)
                     .OrderByDescending(g => g.Count())
                     .Take(8))
        {
            _output.WriteLine($"  gate {group.Key}={group.Count()}");
        }
    }

    [Fact]
    public void S1_Follow_KeepsSeekCountWithinTheStage0Range()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        h.Ltc.Normal(3.0, TimeSpan.FromSeconds(2));
        RunToCursor(h, clock);

        ScenarioCounts counts = Counts(h, sink);
        Report("S-1", counts, sink);
        counts.SyncSeeks.Should().BeInRange(0, 2, "段 0 の S-1 は同期シーク 0〜2 本");
        counts.Timeouts.Should().Be(0, "段 0 は全シナリオで時間切れ 0");
    }

    [Fact]
    public void S2_StopModeHolds_LandEveryHoldWithoutTimeouts()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.ChangeMode(SyncMode.Single);
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
        for (int cycle = 0; cycle < 10; cycle++)
        {
            h.Ltc.Jump(3.0).Duplicate(3.0, TimeSpan.FromMilliseconds(2500));
            h.Ltc.Jump(15.0).Duplicate(15.0, TimeSpan.FromMilliseconds(2500));
        }
        RunToCursor(h, clock);

        ScenarioCounts counts = Counts(h, sink);
        Report("S-2", counts, sink);
        h.Operations.Count(o => o.Name == "signal-loss-pause")
            .Should().BeGreaterThan(0, "停止モードの保持で一時停止する");
        counts.SyncSeeks.Should().BeInRange(6, 20, "段 0 の S-2 は同期シーク 13〜16 本（v0.5.3 は 12）");
        (counts.SyncSeeks + counts.LandingSeeks).Should().BeGreaterThanOrEqualTo(19,
            "保持ごとに目標へ寄る（層では確認フレームの同期シークが先に着地する）");
        counts.Timeouts.Should().Be(0, "段 0 は全シナリオで時間切れ 0");
        sink.Count("signal-loss-confirm").Should().BeGreaterThanOrEqualTo(20,
            "20 回の保持それぞれで損失を確定している（sync.gate を仮想時刻つきで拾えている）");
    }

    [Fact]
    public void S3_OutOfRangeHold_StopsAtTheTrackEndAndRecovers()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.ChangeMode(SyncMode.Single);
        h.AddTrack("A", 0, 5);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
        h.Ltc.Jump(20.0).Duplicate(20.0, TimeSpan.FromSeconds(6));
        h.Ltc.Jump(2.0).Duplicate(2.0, TimeSpan.FromSeconds(3));
        RunToCursor(h, clock);

        ScenarioCounts counts = Counts(h, sink);
        Report("S-3", counts, sink);
        counts.SyncSeeks.Should().BeInRange(0, 3, "段 0 の S-3 は同期シーク 2 本");
        counts.Timeouts.Should().Be(0, "段 0 は全シナリオで時間切れ 0");
    }

    [Fact]
    public void S4_PlaylistSwitch_KeepsSeekCountWithinTheStage0Range()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.ChangeMode(SyncMode.Single);
        h.AddTrack("A", 0, 30);
        h.AddTrack("B", 30, 60);
        h.AddTrack("C", 60, 60);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(20.0, TimeSpan.FromMilliseconds(400));
        RunToCursor(h, clock);
        h.SelectPlaylistRow(1);
        h.LoadCurrentFile();
        h.Ltc.Normal(35.0, TimeSpan.FromMilliseconds(400));
        RunToCursor(h, clock);
        h.SelectPlaylistRow(2);
        h.LoadCurrentFile();
        h.Ltc.Normal(65.0, TimeSpan.FromMilliseconds(400));
        RunToCursor(h, clock);

        ScenarioCounts counts = Counts(h, sink);
        Report("S-4", counts, sink);
        counts.SyncSeeks.Should().BeInRange(0, 6, "段 0 の S-4 は同期シーク 2〜3 本");
        counts.Timeouts.Should().Be(0, "段 0 は全シナリオで時間切れ 0");
    }

    [Fact]
    public void C2_CrossTrackJumps_DoNotProduceTimeouts()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 30);
        h.AddTrack("B", 30, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(7.0, TimeSpan.FromMilliseconds(200));
        for (int round = 0; round < 3; round++)
        {
            h.Ltc.Jump(15.0).Duplicate(15.0, TimeSpan.FromMilliseconds(1500));
            h.Ltc.Jump(35.0).Duplicate(35.0, TimeSpan.FromMilliseconds(1500));
        }
        RunToCursor(h, clock);

        ScenarioCounts counts = Counts(h, sink);
        Report("C-2", counts, sink);
        counts.Timeouts.Should().Be(0, "段 0 は全シナリオで時間切れ 0");
        counts.SyncSeeks.Should().BeLessThanOrEqualTo(6, "トラック跨ぎ 6 回に対して過剰な再シークをしない");
        h.Operations.Count(o => o.Name == "loadfile")
            .Should().BeGreaterThanOrEqualTo(3, "別トラックへはロードで移る（段 0 は同期シーク 1 本）");
    }
}
