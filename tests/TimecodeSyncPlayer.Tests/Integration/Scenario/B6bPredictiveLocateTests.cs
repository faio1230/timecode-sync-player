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
}
