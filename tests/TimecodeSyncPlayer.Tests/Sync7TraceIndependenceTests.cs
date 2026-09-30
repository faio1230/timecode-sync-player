using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 #7: relocate の粗い判定の誤差を、出力トレースの有無と関係なく配信したフレームの PTS
/// （評価位置）で測る。以前は位置のサンプルを出力トレースが有効なときだけ渡していたため、
/// 試験（トレースあり）と本番（トレースなし）で判定の位置が違っていた（release-0.5-plan.md「試験と本番の差」）。
/// 照会位置（パイプライン）と配信 PTS がずれた場面で、トレースを無効にしたまま判定する。
/// </summary>
[Collection("OutputTrace")]
public class Sync7TraceIndependenceTests
{
    private static Func<long> QpcFrom(ManualTimeProvider clock)
    {
        DateTimeOffset origin = clock.GetUtcNow();
        return () => (long)((clock.GetUtcNow() - origin).TotalSeconds * Stopwatch.Frequency);
    }

    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange(SyncMode mode)
    {
        OutputTrace.Current.IsEnabled.Should().BeFalse("前提: 出力トレースは無効（本番と同じ構成）");
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: false, getQpc: QpcFrom(clock));
        h.AddTrack("clip1", 0, duration: 30);
        if (mode == SyncMode.Single)
        {
            h.ChangeMode(SyncMode.Single);
            h.SetDurationSeconds(30);
        }
        h.ManualPlay();
        return (h, clock);
    }

    /// <summary>
    /// v0.6.1 段 A: 走っている LTC（40ms ごとに +1 フレーム、25fps のフレーム境界）を 600ms ぶん送る。再生も同じだけ
    /// 進め、配信した PTS と照会位置は LTC からそれぞれ一定の差に置く（同じ値を繰り返すと保持になり、判定しない）。
    /// </summary>
    private static void SupplyRunningLtc(
        SyncScenarioHarness h, ManualTimeProvider clock, int startFrame,
        double deliveredOffsetSeconds, double queryOffsetSeconds)
    {
        for (int frame = startFrame + 1; frame <= startFrame + 15; frame++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            double ltc = frame / 25.0;
            h.AdvancePlayback(ltc + deliveredOffsetSeconds);
            h.Playback.SetPositionWithoutDelivery(ltc + queryOffsetSeconds);
            h.SupplyLtc(ltc);
        }
    }

    [Theory]
    [InlineData(SyncMode.Continue)]
    [InlineData(SyncMode.Single)]
    public void QueryAheadButDeliveredOnTarget_DoesNotRelocate_WithoutTrace(SyncMode mode)
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange(mode);
        h.SupplyLtc(20.0);
        h.AdvancePlayback(20.0, renderedFrames: 2);          // 配信 20.0（目標と一致）
        h.Playback.SetPositionWithoutDelivery(20.5);          // 照会位置だけ 0.5 秒先行（tol 0.24 の外）
        h.Operations.Clear();

        SupplyRunningLtc(h, clock, startFrame: 500, deliveredOffsetSeconds: 0.0, queryOffsetSeconds: 0.5);

        h.Operations.Should().NotContain(o => o.Name == "seek",
            "誤差は配信 PTS（LTC と一致）で測る。照会位置（0.5 秒先行）のずれでは relocate しない");
    }

    [Theory]
    [InlineData(SyncMode.Continue)]
    [InlineData(SyncMode.Single)]
    public void QueryOnTargetButDeliveredBehind_Relocates_WithoutTrace(SyncMode mode)
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange(mode);
        h.SupplyLtc(20.0);
        h.AdvancePlayback(19.5, renderedFrames: 2);          // 配信 19.5（0.5 秒遅れ）
        h.Playback.SetPositionWithoutDelivery(20.0);          // 照会位置は目標と一致
        h.Operations.Clear();

        SupplyRunningLtc(h, clock, startFrame: 500, deliveredOffsetSeconds: -0.5, queryOffsetSeconds: 0.0);

        h.Operations.Should().Contain(o => o.Name == "seek",
            "誤差は配信 PTS（19.5）で測る。照会位置が目標と一致していても relocate する");
    }
}
