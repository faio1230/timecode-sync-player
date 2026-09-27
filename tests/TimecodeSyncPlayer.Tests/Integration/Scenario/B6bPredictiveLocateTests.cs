using FluentAssertions;

namespace TimecodeSyncPlayer.Tests.Integration;

/// <summary>
/// v0.5.4 B6b（追補 3、TSP-Fable の判定）: 規則 3 の予測ロケートと、すべての relocate を着地の状態に通すこと。
/// 偽の再生 API（<see cref="ScenarioPlayback"/>）はシークの所要 c の間は位置を止め、c 後に目標へ着地して
/// そこから倍率どおりに進む。
/// </summary>
[Collection("Serilog global logger")]
public class B6bPredictiveLocateTests
{
    private const long BaseMilliseconds = 50_000;

    private static (SyncScenarioHarness Harness, ScenarioClock Clock) Arrange()
    {
        var clock = new ScenarioClock(new DateTimeOffset(2026, 9, 28, 0, 0, 0, TimeSpan.Zero),
            monotonicMilliseconds: BaseMilliseconds);
        var h = new SyncScenarioHarness(scenarioClock: clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.RunThrough,
        };
        return (h, clock);
    }

    private static IReadOnlyList<double> Seeks(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    // ── 3: ギャップの出口のシークも着地の状態に通す ─────────────────────

    [Fact]
    public void GapExit_SeekWaitsForItsLanding_NoSecondSeekBeforeLanding()
    {
        // 旧: ギャップの出口の SeekTo が ReportSeekSent を通らず、着地の前の同期評価が
        // まだ動いていない位置を見て 2 本目のシーク（2.00 → 2.08）を出していた。
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.GapBehavior = GapBehavior.Black;
        h.AddTrack("A", 0, 5);
        h.AddTrack("B", 8, 10);
        h.ManualPlay();
        h.AdvancePlayback(3.0);
        h.Ltc.Normal(3.0, TimeSpan.FromSeconds(3));        // 3 → 6: A の終わり（5）でギャップへ
        h.Ltc.Normal(2.0, TimeSpan.FromSeconds(3));        // A の中へ戻る（同じトラックへのギャップの出口）
        long start = clock.MonotonicMilliseconds;
        long end = h.Ltc.NextMilliseconds;
        int seeksWhileFirstPending = 0;
        bool firstLanded = false;
        while (clock.MonotonicMilliseconds < end)
        {
            h.AdvanceMilliseconds(40);
            if (clock.MonotonicMilliseconds - start > 1_000)
                h.Playback.SeekLandingDelaySeconds = 0.5;
            if (Seeks(h).Count > 0 && !firstLanded)
            {
                seeksWhileFirstPending = Seeks(h).Count;
                firstLanded = !h.Playback.HasPendingSeek;
            }
        }

        Seeks(h).Should().NotBeEmpty("前提: ギャップの出口でシークする");
        seeksWhileFirstPending.Should().Be(1, "ギャップの出口のシークが着地するまで、次のシークは出さない");
    }

    // ── 1・2: 予測ロケート（目標 = M(now) + c）で relocate が鎖にならない ─────────────

    /// <summary>
    /// 追補 2 の鎖の場面: 同期して 5 秒走った後、LTC が 3 秒前へ飛ぶ。飛んだ後のシークの所要を c にする。
    /// 旧（先行量なし・閾値 max(tol, c)）は c = 0.3 で 84 本の鎖、c = 2.0 は置き換え（門 8）が続いて着地しなかった。
    /// </summary>
    [Theory]
    [InlineData(0.3)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void JumpDuringPlayback_RelocatesAtMostTwice_AndStaysWithinTolerance(double c)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(5));
        long start = clock.MonotonicMilliseconds;
        h.Ltc.Normal(18.0, TimeSpan.FromSeconds(25));      // 3 秒前へ飛ぶ
        long end = h.Ltc.NextMilliseconds;
        bool delaySet = false;
        double maxAbsErrorInLastTenSeconds = 0.0;
        while (clock.MonotonicMilliseconds < end)
        {
            h.AdvanceMilliseconds(40);
            long t = clock.MonotonicMilliseconds - start;
            if (!delaySet && t > 3_000)
            {
                h.Operations.Clear();
                h.Playback.SeekLandingDelaySeconds = c;
                delaySet = true;
            }
            if (t >= 20_000 && !h.Playback.HasPendingSeek)
            {
                double ltc = 10.0 + t / 1000.0 + 3.0;
                maxAbsErrorInLastTenSeconds = Math.Max(
                    maxAbsErrorInLastTenSeconds, Math.Abs(h.Playback.PositionSeconds - ltc));
            }
        }

        Seeks(h).Count.Should().BeInRange(1, 2,
            "予測ロケート: 学習前の 1 本目は c だけ遅れて着地し、2 本目（目標 = M(now) + c）で追い付く");
        maxAbsErrorInLastTenSeconds.Should().BeLessThanOrEqualTo(0.24,
            "以後は tol 以内（varispeed）で、relocate を繰り返さない");
    }

    // ── 2: マスター停止中の合わせは停止した値へ（先行量を付けない。D37-g の守り） ──────

    [Theory]
    [InlineData(LtcSignalLossMode.Stop)]
    [InlineData(LtcSignalLossMode.RunThrough)]
    public void JumpIntoAHold_WithALearnedSeekCost_LandsOnTheHeldValueWithoutLookahead(LtcSignalLossMode mode)
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.SignalLossMode = mode;
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        h.Ltc.Normal(16.0, TimeSpan.FromSeconds(3));       // 3 秒前へ飛ぶ（c = 0.5 を学習させる）
        h.Ltc.Duplicate(25.0, TimeSpan.FromSeconds(1));    // 25.0 へ飛んで保持（マスター停止）
        long start = clock.MonotonicMilliseconds;
        long end = h.Ltc.NextMilliseconds + 400;
        while (clock.MonotonicMilliseconds < end)
        {
            h.AdvanceMilliseconds(40);
            if (clock.MonotonicMilliseconds - start > 2_000)
                h.Playback.SeekLandingDelaySeconds = 0.5;
        }

        h.SeekState.LearnedSeekDurationSeconds.Should().NotBeNull("前提: シークの所要を学習した");
        Seeks(h).Should().Contain(target => Math.Abs(target - 25.0) < 0.05, "停止した値へ合わせる");
        Seeks(h).Should().NotContain(target => target > 25.05,
            "マスター停止中の relocate に先行量を付けない（停止した値の先へ行き過ぎない。D37-g）");
    }

    [Fact]
    public void Lookahead_IsTheLearnedSeekCostWhileTheMasterMoves_AndZeroWhileItIsHeld()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(3));
        h.Ltc.Normal(16.0, TimeSpan.FromSeconds(3));       // 3 秒前へ飛ぶ（c = 0.5 を学習させる）
        long start = clock.MonotonicMilliseconds;
        long end = h.Ltc.NextMilliseconds;
        while (clock.MonotonicMilliseconds < end)
        {
            h.AdvanceMilliseconds(40);
            if (clock.MonotonicMilliseconds - start > 2_000)
                h.Playback.SeekLandingDelaySeconds = 0.5;
        }
        double learned = h.SeekState.LearnedSeekDurationSeconds!.Value;

        h.SyncService.RelocateLookaheadSeconds.Should().BeApproximately(learned, 1e-9,
            "マスターが動いている間は先行量 = c");

        h.SupplyHeldLtc(19.0);                               // 保持（Duplicate）
        h.SyncService.RelocateLookaheadSeconds.Should().Be(0.0, "マスター停止中は先行量を付けない（D37-g）");

        h.SupplyLtc(19.04);                                  // 値が進む
        h.SyncService.RelocateLookaheadSeconds.Should().BeApproximately(learned, 1e-9);
    }

    // ── 追補 4: 着地後の残差と連続 relocate を実機のログから数えられる ─────────────

    [Fact]
    public void Metrics_LogThePostLandingResidualAndChainedRelocates()
    {
        (SyncScenarioHarness h, ScenarioClock clock) = Arrange();
        using var sink = new ScenarioLogSink(clock);
        h.AddTrack("A", 0, 120);
        h.ManualPlay();
        h.AdvancePlayback(10.0);
        h.Ltc.Normal(10.0, TimeSpan.FromSeconds(5));
        long start = clock.MonotonicMilliseconds;
        h.Ltc.Normal(18.0, TimeSpan.FromSeconds(8));       // 3 秒前へ飛ぶ
        long end = h.Ltc.NextMilliseconds;
        while (clock.MonotonicMilliseconds < end)
        {
            h.AdvanceMilliseconds(40);
            if (clock.MonotonicMilliseconds - start > 3_000)
                h.Playback.SeekLandingDelaySeconds = 0.5;
        }

        List<ScenarioGateEvent> relocates = sink.GateEvents.Where(e => e.Name == "relocate").ToList();
        relocates.Should().HaveCount(2, "学習前の 1 本と、先行量つきの 2 本目");
        relocates[0].Message.Should().Contain("reason=sync").And.Contain("chained=False");
        relocates[1].Message.Should().Contain("reason=sync").And.Contain("chained=True",
            "着地の後に同じ発生元で出た 2 本目は連続 relocate として数える");

        List<ScenarioGateEvent> residuals = sink.GateEvents.Where(e => e.Name == "post-landing-residual").ToList();
        residuals.Should().HaveCount(2, "relocate の着地ごとに 1 行");
        residuals[0].Message.Should().Contain("errorMs=-5", "1 本目は c（0.5 秒）ぶん遅れて着地する（符号は再生位置 − M）");
        residuals[1].Message.Should().MatchRegex(@"errorMs=-?\d{1,2}\.\d ", "2 本目（目標 = M + c）の着地の残差は 100ms 未満");
    }
}
