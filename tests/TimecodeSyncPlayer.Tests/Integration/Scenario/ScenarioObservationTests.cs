using FluentAssertions;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 C4: 観測層の指標（hold-landing・jump-black・hold-pause・follow）を
/// 仮想時計の台本で出す。記録のみで、失敗の条件は持たない（設計 §2-4）。
/// </summary>
[Collection("Serilog global logger")]
public class ScenarioObservationTests
{
    private static readonly DateTimeOffset BaseUtc = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
    private const int BaseMilliseconds = 50_000;
    private readonly ITestOutputHelper _output;

    public ScenarioObservationTests(ITestOutputHelper output) => _output = output;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange()
    {
        var clock = new ScenarioClock(BaseUtc, monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        return (h, clock);
    }

    private static void RunToEnd(SyncScenarioHarness h, ScenarioClock clock, int marginMilliseconds = 400)
    {
        long end = h.Ltc.NextMilliseconds + marginMilliseconds;
        int guard = 0;
        while (clock.MonotonicMilliseconds < end && guard++ < 100_000)
            h.AdvanceMilliseconds(40);
    }

    [Fact]
    public void C1_JumpSeries_MeasuresLandingLatencyInVirtualMilliseconds()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
        for (int i = 0; i < 20; i++)
        {
            double target = i % 2 == 0 ? 15.0 : 3.0;
            h.Ltc.Jump(target).Duplicate(target, TimeSpan.FromMilliseconds(2500));
        }
        RunToEnd(h, clock);

        IReadOnlyList<ScenarioHoldLanding> landings = ScenarioMetrics.HoldLandings(h);
        foreach (ScenarioHoldLanding landing in landings)
        {
            _output.WriteLine(
                $"{landing.Name} jump@{landing.JumpAtMilliseconds - BaseMilliseconds}ms " +
                $"distance={landing.JumpDistanceSeconds:F3}s " +
                $"latency={(landing.LandingLatencySeconds is double latency ? $"{latency * 1000:F0}ms" : "none")} " +
                $"overBudget={landing.LatencyOverBudget}");
        }
        _output.WriteLine(
            $"sync={ScenarioMetrics.SyncSeekCount(h)} " +
            $"landing={ScenarioMetrics.LandingSeekCount(h)} " +
            $"total={ScenarioMetrics.SeekCount(h)}");

        foreach (ScenarioEvent e in h.Events.Where(e =>
                     e.AtMilliseconds <= BaseMilliseconds + 3_500 &&
                     (e.Kind is "seek" or "sync-seek" or "landing-seek" or "pause" or "resume" or "load" ||
                      (e.Kind == "ltc-frame" && e.Detail == "Jump"))))
        {
            _output.WriteLine(
                $"  ev {e.AtMilliseconds - BaseMilliseconds}ms {e.Kind} {e.Detail} " +
                $"{(e.Value.HasValue ? e.Value.Value.ToString("F3") : "")}");
        }

        landings.Should().HaveCount(20, "C-1 は 20 ジャンプ（10 周 × 2 目標）");
        landings.Should().OnlyContain(l => l.LandingLatencySeconds.HasValue,
            "どのジャンプにも次のシークがある（段 0 の C-1 は同期シーク 20 本）");
        landings.Should().OnlyContain(l => l.JumpDistanceSeconds > 4 * ScenarioMetrics.SyncToleranceSeconds(h),
            "すべて 4×tolerance を超える距離（D38 の再現条件）");
        landings.Should().OnlyContain(l => !l.LatencyOverBudget,
            "D38 の修正後、1.0 秒を超える着地の遅れは 0（段 0 の latencyOverBudget 0 / 0）");
        ScenarioMetrics.SyncSeekCount(h).Should().Be(20, "段 0 の C-1 は同期シーク 20 本");
    }

    [Fact]
    public void S1_FollowMetric_UsesTheSameMappingAsE2E()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        long start = clock.MonotonicMilliseconds;
        h.Ltc.Normal(3.0, TimeSpan.FromSeconds(3));
        RunToEnd(h, clock);

        // E2E と同じく「追従の許容内に入ってから」の区間を見る。
        long settleAt = -1;
        double ltc = double.NaN;
        foreach (ScenarioEvent e in h.Events)
        {
            if (e.Kind == "ltc-frame" && e.Value.HasValue)
                ltc = e.Value.Value;
            if (e.Kind == "tick" && e.Value.HasValue && e.AtMilliseconds >= start + 200 &&
                double.IsFinite(ltc) &&
                Math.Abs(e.Value.Value - ltc) <= ScenarioMetrics.FollowToleranceSeconds)
            {
                settleAt = e.AtMilliseconds;
                break;
            }
        }
        settleAt.Should().BeGreaterThan(0, "追従に入る（位置が LTC の写像の ±0.3 に入る）");

        // 台本の最後の LTC フレームまでを見る（その後の無音は追従の対象外）。
        long lastLtcAt = h.Events.Where(e => e.Kind == "ltc-frame").Max(e => e.AtMilliseconds);
        ScenarioFollowResult follow = ScenarioMetrics.Follow(
            h, settleAt, lastLtcAt, timelineStartSeconds: 0, mediaInSeconds: 0);

        _output.WriteLine($"settleAt={settleAt - BaseMilliseconds}ms samples={follow.Samples} " +
            $"maxError={follow.MaxErrorSeconds:F4}s following={follow.IsFollowing} " +
            $"sync={ScenarioMetrics.SyncSeekCount(h)}");

        var series = new List<(long At, double Ltc, double Position, double Error)>();
        double runningLtc = double.NaN;
        foreach (ScenarioEvent e in h.Events)
        {
            if (e.Kind == "ltc-frame" && e.Value.HasValue)
                runningLtc = e.Value.Value;
            if (e.Kind == "tick" && e.Value.HasValue && e.AtMilliseconds >= settleAt &&
                double.IsFinite(runningLtc))
            {
                series.Add((e.AtMilliseconds, runningLtc, e.Value.Value,
                    Math.Abs(e.Value.Value - runningLtc)));
            }
        }
        foreach (var sample in series.OrderByDescending(s => s.Error).Take(5))
        {
            _output.WriteLine(
                $"  worst at={sample.At - BaseMilliseconds}ms ltc={sample.Ltc:F3} " +
                $"pos={sample.Position:F3} error={sample.Error:F3}");
        }

        follow.Samples.Should().BeGreaterThan(10);
        follow.IsFollowing.Should().BeTrue("着地後は LTC の写像へ ±0.3 で追従する");
        ScenarioMetrics.SyncSeekCount(h).Should().BeInRange(0, 2, "段 0 の S-1 は同期シーク 0〜2 本");
    }

    [Fact]
    public void GapJump_IsCountedAsBlackTicksUntilLanding()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.GapBehavior = GapBehavior.Black;
        h.AddTrack("A", 0, 5);
        h.AddTrack("B", 10, 30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(200));
        RunToEnd(h, clock);

        long jumpAt = clock.MonotonicMilliseconds;
        h.Ltc.Jump(7.0).Duplicate(7.0, TimeSpan.FromMilliseconds(1500));
        RunToEnd(h, clock);

        ScenarioJumpBlack black = ScenarioMetrics.JumpBlack(h, jumpAt, clock.MonotonicMilliseconds);
        _output.WriteLine(
            $"samples={black.Samples} blackTicks={black.BlackTicks} blackSeconds={black.BlackSeconds:F2}");

        black.Samples.Should().BeGreaterThan(0);
        black.BlackTicks.Should().BeGreaterThan(0, "A と B の間のギャップは状態として黒を描く");
    }

    [Fact]
    public void StopModeHold_MeasuresPauseLatencyFromTheHoldStart()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(3.0);

        h.Ltc.Normal(3.0, TimeSpan.FromMilliseconds(400));
        long holdAt = h.Ltc.NextMilliseconds;   // Jump はこの時刻に載る
        h.Ltc.Jump(10.0).Duplicate(10.0, TimeSpan.FromMilliseconds(1500));
        RunToEnd(h, clock);

        ScenarioHoldPause pause = ScenarioMetrics.HoldPause(h, holdAt);
        _output.WriteLine(
            $"hold@{holdAt - BaseMilliseconds}ms pauseLatency=" +
            $"{(pause.PauseLatencySeconds is double latency ? $"{latency * 1000:F0}ms" : "none")}");

        pause.PauseLatencySeconds.Should().NotBeNull("保持の確定で一時停止する（停止モード）");
        pause.PauseLatencySeconds!.Value.Should().BeInRange(0.1, 0.8,
            "最後の有効フレームから 250ms の確定に Tick 間隔が乗る（段 0 の R-1/R-2 と同じ向き）");
    }
}
