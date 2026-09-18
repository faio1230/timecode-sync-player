using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D37-c: 追従開始は既存の着地窓（D37-b2）に載せてシークで詰める／速度補正の残差にも
/// 粗い判定と同じ前処理（ありえない変化の除外・中央値）を通す。
/// </summary>
public class D37cFollowStartAndRatePathTests
{
    // ── 穴 1: 追従開始はシークで着地する ───────────────────────────

    [Fact]
    public void FollowStart_FirstSyncRequest_ForcesSeekForSubSeekCostDeficit()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.SetSyncEnabled(false);                        // まだ追従していない
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SetSyncEnabled(true);
        h.SupplyLtc(1.5);                               // ずれ 0.5 秒（シーク所要の既定 1.0 秒以内）

        // 追従開始の着地窓が開き、速度補正ではなくシークで詰める。
        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.5, 1e-9);
    }

    [Fact]
    public void FollowStartWindow_Expires_AndTheSameDeficitUsesRateCatchUp()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.SetSyncEnabled(false);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.SetSyncEnabled(true);
        h.SupplyLtc(1.5);                               // 追従開始 → シーク

        // 着地させて保留を settle させる。
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.AdvancePlayback(1.5 + (i * 0.04), renderedFrames: 1);
            h.SupplyLtc(1.5 + (i * 0.04));
        }
        h.Operations.Clear();
        h.RateAttempts.Clear();

        clock.Advance(TimeSpan.FromSeconds(1.2));       // 着地窓（1 秒）を過ぎる
        h.AdvancePlayback(1.5, renderedFrames: 1);
        h.SupplyLtc(2.0);                               // 同じ 0.5 秒不足

        h.Operations.Should().NotContain(o => o.Name == "seek");
        h.RateAttempts.Should().NotBeEmpty("窓の外では従来どおり速度補正を優先する");
    }

    [Fact]
    public void MonitoringStart_WithSyncAlreadyEnabled_OpensTheLandingWindow()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();

        h.IsMonitoring = false;
        h.IsMonitoring = true;                          // 監視開始（同期は有効のまま）
        h.SupplyLtc(1.5);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.5, 1e-9);
    }

    // ── 穴 2: 速度補正の入力の前処理 ──────────────────────────────

    [Fact]
    public void CorrectionGate_RejectsImpossibleResidualJump_AndCountsIt()
    {
        var (h, clock) = CreateSingleTrackHarness();
        h.AdvancePlayback(1.00, renderedFrames: 2);
        h.SupplyLtc(1.05);                              // 残差 +50ms → rate
        h.RateAttempts.Clear();

        clock.Advance(TimeSpan.FromMilliseconds(40));
        h.AdvancePlayback(1.50, renderedFrames: 1);     // 再生位置クエリの乱れ（ありえない跳び）
        h.SupplyLtc(1.09);                              // 残差 −410ms

        h.Controller.CorrectionRejectedSamples.Should().Be(1);
        h.RateAttempts.Should().BeEmpty("弾いた標本は速度補正へ渡さない");
    }

    [Fact]
    public void CorrectionGate_SpikeSeries_DoesNotPinTheRateToTheClamp()
    {
        var (h, clock) = CreateSingleTrackHarness();
        h.AdvancePlayback(1.00, renderedFrames: 2);
        h.SupplyLtc(1.06);                              // 素の残差 +60ms
        h.RateAttempts.Clear();

        // 素の残差 +60ms を維持したまま、300〜760ms の跳びを交互に混ぜる（M3 の観測に倣う）。
        double[] spikes = [0.0, 0.0, +0.64, 0.0, -0.76, 0.0, 0.0, +0.70, 0.0];
        double ltc = 1.10;
        double playback = 1.04;
        for (int i = 0; i < spikes.Length; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            ltc += 0.04;
            playback += 0.04;
            h.AdvancePlayback(playback + spikes[i], renderedFrames: 1);
            h.SupplyLtc(ltc);
        }

        h.Controller.CorrectionRejectedSamples.Should().Be(3);
        h.RateAttempts.Should().NotBeEmpty();
        h.RateAttempts.Should().OnlyContain(
            rate => Math.Abs(rate - 1.0) < 0.099,
            "跳びを弾いた後の残差は +60ms 前後なので、rate は上下限（±0.10）に張り付かない");
    }

    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) CreateSingleTrackHarness()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        harness.AddTrack("track", 0, duration: 5);
        harness.ChangeMode(SyncMode.Single);
        harness.ManualPlay();
        return (harness, clock);
    }

    /// <summary>ManualTimeProvider と同じ時計を QPC 秒として渡す（ゲートの dt を決定的にする）。</summary>
    private static Func<long> QpcFrom(ManualTimeProvider clock)
    {
        DateTimeOffset origin = clock.GetUtcNow();
        return () => (long)((clock.GetUtcNow() - origin).TotalSeconds * Stopwatch.Frequency);
    }
}
