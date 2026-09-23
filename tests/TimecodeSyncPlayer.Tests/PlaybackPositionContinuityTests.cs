using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 0.4.8 hotfix: 復号が一時的に追いつかず再生位置の照会が 2 系列を行き来するとき、
/// 位置を後退させず、後退した直後は速度補正を止める（テスト 5・6）。
/// </summary>
public class PlaybackPositionContinuityTests
{
    private static long Qpc(double seconds) => (long)(seconds * Stopwatch.Frequency);

    [Fact]
    public void Observe_ForwardPositions_PassThroughUnchanged()
    {
        var c = new PlaybackPositionContinuity(() => 0);
        c.Observe(10.000, 1, 5).Should().Be(10.000);
        c.Observe(10.021, 1, 5).Should().Be(10.021);
        c.Observe(10.043, 1, 0).Should().Be(10.043, "世代の分からない照会も同じ系列");
        c.IsUnstable().Should().BeFalse();
        c.BackwardSamples.Should().Be(0);
    }

    [Fact]
    public void Observe_AlternatingBases_NeverGoesBackward_AndMarksUnstable()
    {
        // 実測の形: 補間位置（先）とオーディオ位置（後）が 1 照会ごとに入れ替わる。
        double now = 0;
        var c = new PlaybackPositionContinuity(() => Qpc(now));
        double[] observed = [425.557, 425.650, 425.698, 425.600, 425.621, 425.756, 425.642];
        var reported = new List<double>();
        foreach (double v in observed)
        {
            now += 0.02;
            reported.Add(c.Observe(v, 7, 15));
        }

        reported.Should().BeInAscendingOrder("同じ系列の中で位置を戻さない");
        reported.Max().Should().Be(425.756);
        c.BackwardSamples.Should().Be(3);
        c.IsUnstable().Should().BeTrue();
    }

    [Fact]
    public void IsUnstable_EndsOneSecondAfterTheLastBackwardSample_AndReportsTheEpisode()
    {
        double now = 0;
        var c = new PlaybackPositionContinuity(() => Qpc(now));
        c.Observe(1.00, 1, 1);
        c.Observe(0.90, 1, 1);                  // 100ms 後退

        now = 0.99;
        c.IsUnstable().Should().BeTrue();
        c.TakeEndedEpisode().Should().BeNull("まだ続いている");

        now = 1.01;
        c.IsUnstable().Should().BeFalse();
        var episode = c.TakeEndedEpisode();
        episode.Should().NotBeNull();
        episode!.Value.Count.Should().Be(1);
        episode.Value.MaxBackSeconds.Should().BeApproximately(0.10, 1e-9);
        c.TakeEndedEpisode().Should().BeNull("1 回だけ返す");
    }

    [Fact]
    public void Observe_SmallJitter_IsHeldButNotCountedAsUnstable()
    {
        var c = new PlaybackPositionContinuity(() => 0);
        c.Observe(5.000, 1, 1);
        c.Observe(4.998, 1, 1).Should().Be(5.000);
        c.IsUnstable().Should().BeFalse("オーディオ刻みより小さい揺れは数えない");
    }

    [Fact]
    public void Observe_NewSeries_AcceptsBackwardPosition()
    {
        // シーク・ロード・一時停止などの乱れ（回数が進む）や世代の変化では戻ってよい。
        var c = new PlaybackPositionContinuity(() => 0);
        c.Observe(100.0, 1, 1);
        c.Observe(20.0, 2, 1).Should().Be(20.0, "乱れの回数が進んだ");
        c.Observe(19.0, 2, 2).Should().Be(19.0, "世代が進んだ");
        c.IsUnstable().Should().BeFalse();
    }

    [Fact]
    public void Observe_LargeBackwardWithinTheSeries_TrustsTheNewValue()
    {
        // 一度の外れ値（先へ飛んだ値）で位置を長く止めない。
        var c = new PlaybackPositionContinuity(() => 0);
        c.Observe(10.0, 1, 1);
        c.Observe(12.0, 1, 1);                  // 外れ値
        c.Observe(10.05, 1, 1).Should().Be(10.05);
        c.Observe(10.07, 1, 1).Should().Be(10.07);
        c.BackwardSamples.Should().Be(1);
    }

    // ── 速度補正の停止（テスト 5・6） ─────────────────────────────

    [Fact]
    public void Correction_WhilePositionUnstable_HoldsRateAtOne_AndResumesAfterwards()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 10);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.AdvancePlayback(1.00, renderedFrames: 2);
        h.SupplyLtc(1.08);                              // 残差 +80ms → 速度補正（1.0 以外）
        h.AppliedRates.Should().Contain(r => r > 1.0);
        h.AppliedRates.Clear();

        h.PlaybackPositionUnstable = true;
        for (int i = 1; i <= 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            h.AdvancePlayback(1.00 + 0.04 * i, renderedFrames: 1);
            h.SupplyLtc(1.08 + 0.04 * i + (i % 2 == 0 ? 0.15 : 0.0));   // 1 標本おきに +150ms 跳ねる
        }
        h.AppliedRates.Should().Equal([1.0], "不安定な間は 1.0 へ 1 回戻して、それ以上動かさない");

        h.PlaybackPositionUnstable = false;
        h.AppliedRates.Clear();
        for (int i = 1; i <= 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            h.AdvancePlayback(1.20 + 0.04 * i, renderedFrames: 1);
            h.SupplyLtc(1.28 + 0.04 * i);               // 安定した +80ms
        }
        h.AppliedRates.Should().Contain(r => r > 1.0, "安定したら補正を再開する");
    }

    private static Func<long> QpcFrom(ManualTimeProvider clock)
    {
        DateTimeOffset origin = clock.GetUtcNow();
        return () => (long)((clock.GetUtcNow() - origin).TotalSeconds * Stopwatch.Frequency);
    }
}
