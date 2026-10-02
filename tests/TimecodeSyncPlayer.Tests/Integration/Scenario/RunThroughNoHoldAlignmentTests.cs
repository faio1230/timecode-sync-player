using FluentAssertions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit.Abstractions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.6.3 (ii)（設計書 14-4、利用者の決定、TSP-Fable の承認）: RunThrough では、LTC が止まったと判定しても映像を止まった位置へ
/// 戻さずに走り続け、LTC が戻ったとき（確定した Jump・等速）に規則 3 で 1 回だけ合わせる。止まった値へ合わせる経路
/// （規則 4 の入口の合わせ、D31-b の RunThrough 側、D20-b）は保持中は合わせない。(C)（損失中に Duplicate で確定した Jump）の
/// 1 回は位置の変更なので残す。停止モードは変えない。Single の範囲外の LTC はクリップの端で止める（S-4）を、入口の合わせから
/// 分離した境界の経路で保つ。テストは観測の行（relocate の reason・masterStopped・Sync hold summary）に頼らない。
/// </summary>
[Collection("Serilog global logger")]
public class RunThroughNoHoldAlignmentTests
{
    private const long BaseMilliseconds = 50_000;
    private const double ToleranceSeconds = 0.24;

    private readonly ITestOutputHelper _output;

    public RunThroughNoHoldAlignmentTests(ITestOutputHelper output) => _output = output;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange(LtcSignalLossMode lossMode)
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = lossMode,
        };
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        return (h, clock);
    }

    private sealed record Seek(long AtMs, double Target, double Position)
    {
        public double Delta => Target - Position;
    }

    /// <summary>台本を歩め、各歩みで新しく出たシークを（出た時点の再生位置つきで）記録する。</summary>
    private static List<Seek> RunUntil(SyncScenarioHarness h, ScenarioClock clock, long endMilliseconds, long origin,
        Action<long>? perStep = null)
    {
        var seeks = new List<Seek>();
        int seen = h.Operations.Count(o => o.Name == "seek");
        while (clock.MonotonicMilliseconds < endMilliseconds)
        {
            double before = h.PlaybackSeconds;
            perStep?.Invoke(clock.MonotonicMilliseconds - origin);
            h.AdvanceMilliseconds(40);
            List<ScenarioPlaybackOperation> all = h.Operations.Where(o => o.Name == "seek").ToList();
            for (; seen < all.Count; seen++)
                seeks.Add(new Seek(clock.MonotonicMilliseconds - origin, all[seen].Value ?? double.NaN, before));
        }
        return seeks;
    }

    private void Report(string label, IEnumerable<Seek> seeks, SyncScenarioHarness h) =>
        _output.WriteLine($"{label}: seeks=[{string.Join(", ", seeks.Select(s => $"{s.AtMs}ms {s.Target:F3}({s.Delta:+0.000;-0.000})"))}] " +
            $"position={h.PlaybackSeconds:F3} paused={h.IsPaused}");

    /// <summary>追従（10 → 13 秒）→ 映像を保持値の 0.5 秒先へ → 13.0 で保持 2 秒。保持の間のシークを返す。</summary>
    private List<Seek> FollowThenHold(SyncScenarioHarness h, ScenarioClock clock, out long origin)
    {
        origin = clock.MonotonicMilliseconds;
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        long holdStart = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(13.0, TimeSpan.FromSeconds(2));
        RunUntil(h, clock, holdStart, origin);
        h.AdvancePlayback(h.PlaybackSeconds + 0.5);          // 映像は保持値の 0.5 秒先（入口の合わせの許容 0.24 を超える）
        return RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
    }

    /// <summary>(a) RunThrough: 保持の間は止まった値へ合わせない（後ろ向きのシーク 0、シーク 0）。今は入口の合わせで 13.0 へ 1 本戻す。</summary>
    [Fact]
    public void A_RunThrough_Hold_NoBackwardSeekWhileStopped()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        List<Seek> duringHold = FollowThenHold(h, clock, out _);
        Report("hold", duringHold, h);

        duringHold.Count(s => s.Delta < -1.0 / 30.0).Should().Be(0, "保持の間は止まった値へ戻さない");
        duringHold.Should().BeEmpty("RunThrough の保持の間はシークしない");
        h.IsPaused.Should().BeFalse("RunThrough は保持の間も走る");
    }

    /// <summary>(b) 復帰が確定した Jump（20.0 → 20.04）: relocate は 1 本以内、着地の後の |誤差| ≤ tol。</summary>
    [Fact]
    public void B1_RunThrough_RecoveryByConfirmedJump_RelocatesAtMostOnce()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        List<Seek> duringHold = FollowThenHold(h, clock, out long origin);
        long recoveryStart = h.Ltc.NextMilliseconds;
        h.Ltc.Jump(20.0);
        h.Ltc.Normal(20.04, TimeSpan.FromSeconds(3));
        List<Seek> recovery = RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
        Report("hold", duringHold, h);
        Report("recovery", recovery, h);
        double ltcEnd = 20.04 + (h.Ltc.NextMilliseconds - recoveryStart - 40) / 1000.0 - 0.04;

        (duringHold.Count + recovery.Count).Should().BeLessThanOrEqualTo(1, "保持の間 0 本、復帰で 1 本以内（合計 ≤ 1）");
        Math.Abs(h.PlaybackSeconds - ltcEnd).Should().BeLessThanOrEqualTo(ToleranceSeconds, "復帰の後は tol 以内");
    }

    /// <summary>(b) 復帰が等速（13.04 から 30fps 相当で進む）: relocate は 1 本以内（走り続けた映像を 1 回だけ戻す）。</summary>
    [Fact]
    public void B2_RunThrough_RecoveryAtNormalSpeed_RelocatesAtMostOnce()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        List<Seek> duringHold = FollowThenHold(h, clock, out long origin);
        long recoveryStart = h.Ltc.NextMilliseconds;
        h.Ltc.Normal(13.04, TimeSpan.FromSeconds(3));
        List<Seek> recovery = RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
        Report("hold", duringHold, h);
        Report("recovery", recovery, h);
        double ltcEnd = 13.04 + (h.Ltc.NextMilliseconds - recoveryStart) / 1000.0 - 0.04;

        (duringHold.Count + recovery.Count).Should().BeLessThanOrEqualTo(1, "保持の間 0 本、復帰で 1 本以内（合計 ≤ 1）");
        Math.Abs(h.PlaybackSeconds - ltcEnd).Should().BeLessThanOrEqualTo(ToleranceSeconds, "復帰の後は tol 以内");
    }

    /// <summary>(d) 停止モードは今のまま: 保持値へ 1 本着地（held-landing）して一時停止する。</summary>
    [Fact]
    public void D_StopMode_Hold_LandsOnceAndPauses_AsBefore()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.Stop);
        List<Seek> duringHold = FollowThenHold(h, clock, out _);
        Report("hold", duringHold, h);

        duringHold.Should().ContainSingle("停止モードは保持値への着地 1 本（今のまま）")
            .Which.Target.Should().BeApproximately(13.0, 1e-6);
        h.IsPaused.Should().BeTrue("停止モードは保持で一時停止する");
    }

    /// <summary>
    /// (e) D31-b・D20-b の RunThrough 側: 損失中に止まった値が這う（4 歩、500ms ごとに +1 フレーム）。這う前進は保持値の変更ではなく
    /// （v0.6.1 案 1）、保持中は合わせない。合わせ 0（今も 0 の見込み。経路が外れても 0 のまま固定する）。
    /// </summary>
    [Fact]
    public void E_RunThrough_ValueCreepsDuringLoss_NoAlignment()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        long origin = clock.MonotonicMilliseconds;
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        long holdStart = h.Ltc.NextMilliseconds;
        double held = 13.0;
        for (int step = 0; step < 4; step++)
        {
            h.Ltc.Duplicate(held, TimeSpan.FromMilliseconds(500));
            held += 1.0 / 25.0;
        }
        RunUntil(h, clock, holdStart, origin);
        h.AdvancePlayback(h.PlaybackSeconds + 0.5);
        List<Seek> duringHold = RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
        Report("crawl", duringHold, h);

        duringHold.Should().BeEmpty("RunThrough の保持の間は、値が這っても止まった値へ合わせない");
    }

    /// <summary>
    /// (f) (C) は残す: RunThrough で損失中に、止まった値が別の値（8.0）へ飛んで Duplicate で確定した（マスターの位置の変更）。
    /// 新しい保持値へ 1 回だけ合わせる（今のまま）。
    /// </summary>
    [Fact]
    public void F_RunThrough_ConfirmedJumpIntoHoldDuringLoss_StillAlignsOnce()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(LtcSignalLossMode.RunThrough);
        long origin = clock.MonotonicMilliseconds;
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        h.Ltc.Duplicate(13.0, TimeSpan.FromSeconds(1));     // 保持（損失）
        long jumpAt = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(8.0, TimeSpan.FromSeconds(2));      // 損失中に 8.0 へ飛んで、Duplicate で確定（(C)）
        RunUntil(h, clock, jumpAt, origin);
        List<Seek> afterJump = RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
        Report("jump-into-hold", afterJump, h);

        afterJump.Should().ContainSingle("(C) の確定した Jump の 1 回の合わせは残す")
            .Which.Target.Should().BeApproximately(8.0, 1e-6);
    }

    /// <summary>(g) Continue で LTC がトラックの範囲外（ギャップ）で止まったとき: ギャップの経路のまま（シークしない、走らせない）。</summary>
    [Fact]
    public void G_Continue_HoldInsideAGap_KeepsTheGapPath()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 10);
        h.AddTrack("B", 14, 10);
        h.ManualPlay();
        h.AdvancePlayback(8.0);
        long origin = clock.MonotonicMilliseconds;
        h.Ltc.Normal(8.0, TimeSpan.FromSeconds(3));          // 8 → 11: A の終わり（10）でギャップへ
        long holdStart = h.Ltc.NextMilliseconds;
        h.Ltc.Duplicate(11.0, TimeSpan.FromSeconds(2));      // ギャップの中で保持
        RunUntil(h, clock, holdStart, origin);
        List<Seek> duringHold = RunUntil(h, clock, h.Ltc.NextMilliseconds, origin);
        Report("gap-hold", duringHold, h);

        h.IsGapActive.Should().BeTrue("前提: ギャップの中");
        duringHold.Should().BeEmpty("ギャップの中の保持では端への合わせも無い（今のまま）");
    }

    /// <summary>
    /// (c) S-4（Single・RunThrough・60p、クリップ [5,25]、LTC 35.0 を保持したまま次のトラックを読み込む。ロードの遅れ 0 と 0.2 秒）:
    /// 入口の合わせを外しても、範囲外の LTC はクリップの端で止める（境界の経路）。出口への 1 本（reason boundary）で境界の保持に入り、
    /// 位置は出口の近くに留まる。今は入口の合わせの 1 本（reason hold-entry）なので reason の行で赤。
    /// </summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    public void C_S4_SingleRunThrough_HeldOutsideTheClip_SeeksOnceToTheExitWithReasonBoundary(double loadSeconds)
    {
        const double clipIn = 5.0;
        const double clipOut = 25.0;
        const double videoFps = 60.0;
        var clock = new ScenarioClock(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        using var sink = new ScenarioLogSink(clock);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
            MediaInSeconds = clipIn,
            MediaOutSeconds = clipOut,
        };
        h.AddTrack("A", 0, 30);
        h.AddTrack("B", 0, 30);
        h.ChangeMode(SyncMode.Single);
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(1.0);
        h.Ltc.Normal(34.8, TimeSpan.FromMilliseconds(200));
        h.Ltc.Duplicate(35.0, TimeSpan.FromSeconds(15));
        long end = clock.MonotonicMilliseconds + 1_500;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);

        h.Playback.SeekLandingDelaySeconds = 0;
        h.Playback.LoadDurationSeconds = loadSeconds;
        h.ManualNextTrack();
        h.Playback.SetFps(videoFps);
        h.Operations.Clear();
        int gateBefore = sink.GateEvents.Count;
        end = clock.MonotonicMilliseconds + 10_000;
        while (clock.MonotonicMilliseconds < end)
            h.AdvanceMilliseconds(40);

        List<double> seeks = h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();
        List<string> relocates = sink.GateEvents.Skip(gateBefore).Where(e => e.Name == "relocate").Select(e => e.Message).ToList();
        _output.WriteLine($"load={loadSeconds:F1}s seeks=[{string.Join(", ", seeks.Select(v => v.ToString("F3")))}] position={h.PlaybackSeconds:F3} " +
            $"paused={h.IsPaused} relocates=[{string.Join(" | ", relocates)}]");

        h.Operations.Select(o => o.Name).Should().Contain("clip-end-hold", "出口へ着いた後は境界の保持（出口で一時停止）に入る");
        h.PlaybackSeconds.Should().BeInRange(clipOut, clipOut + 1.0 / 25.0 + 2.0 / videoFps, "出口の近くに留まる（出口の先へ走らない）");
        seeks.Should().ContainSingle("出口へのシークは 1 本").Which.Should().BeApproximately(clipOut, 1e-6);
        relocates.Should().ContainSingle().Which.Should().Contain("reason=boundary",
            "端へのシークは入口の合わせではなく、範囲外の LTC をクリップの端で止める境界の経路が出す");
        h.SyncService.BoundarySeeks.Should().Be(1, "観測: 境界の経路の端へのシークは 1 本（Sync hold summary の boundarySeeks）");
        h.SyncService.BackwardSeeksWhileStopped.Should().Be(0, "観測: RunThrough でマスター停止中の後ろ向きの relocate は 0");
    }

    private sealed class SummarySink : ILogEventSink, IDisposable
    {
        private readonly ILogger _previous = Log.Logger;
        private readonly int _owner = Environment.CurrentManagedThreadId;
        public List<LogEvent> Events { get; } = new();
        public SummarySink() => Log.Logger = new LoggerConfiguration().MinimumLevel.Information().WriteTo.Sink(this).CreateLogger();
        public void Emit(LogEvent e)
        {
            if (Environment.CurrentManagedThreadId == _owner && e.MessageTemplate.Text.StartsWith("Sync hold summary", StringComparison.Ordinal))
                lock (Events) Events.Add(e);
        }
        public void Dispose() => Log.Logger = _previous;
    }

    /// <summary>
    /// 観測（14-4 の案 A）: 監視の停止で "Sync hold summary" を Information で 1 行。RunThrough の保持は入口 1 回、マスター停止中の
    /// 後ろ向きの relocate 0。停止モードは保持値への着地（後ろ向き 1、今のまま）。
    /// </summary>
    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough, 0)]
    [InlineData(LtcSignalLossMode.Stop, 1)]
    public void SyncHoldSummary_IsLoggedWhenMonitoringStops(LtcSignalLossMode lossMode, int expectedBackward)
    {
        using var sink = new SummarySink();
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange(lossMode);
        FollowThenHold(h, clock, out _);

        h.IsMonitoring = false;

        List<LogEvent> summaries;
        lock (sink.Events) summaries = sink.Events.ToList();
        summaries.Should().ContainSingle();
        _output.WriteLine(summaries[0].RenderMessage());
        summaries[0].Properties["HoldEntries"].ToString().Should().Be("1");
        summaries[0].Properties["BackwardSeeksWhileStopped"].ToString().Should().Be(expectedBackward.ToString());
        summaries[0].Properties["BoundarySeeks"].ToString().Should().Be("0");
    }
}
