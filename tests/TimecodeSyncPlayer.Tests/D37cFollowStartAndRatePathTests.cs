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
    public void FollowStart_FirstSyncRequest_ForDeficitAboveHalfSeekCost_ForcesSeek()
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
        h.SupplyLtc(1.8);                               // ずれ 0.8 秒（既定 1.0 秒の半分を超える）

        // 追従開始の着地窓が開き、速度補正ではなくシークで詰める。
        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.8, 1e-9);
    }

    [Fact]
    public void FollowStart_SmallDeficitBelowHalfSeekCost_DoesNotSeek()
    {
        // D37-d: L-1 実機の小さい誤差（0.3 秒 < 0.5 × 既定 1.0 秒）では、着地窓中でも
        // シークを強制しない（シークは誤差を増やすだけ）。前進ガードは境界帯用に残る
        // （サービス単体で固定）。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.SetSyncEnabled(false);                        // まだ追従していない
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SetSyncEnabled(true);
        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            h.SupplyLtc(1.30 + (i * 0.01));            // ずれ 0.30〜0.33 秒（既定 1.0 秒の半分以下）
        }

        h.Operations.Should().NotContain(o => o.Name == "seek");
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
        h.SupplyLtc(1.8);                               // 0.8 > 0.5 × 1.0

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(1.8, 1e-9);
    }

    [Fact]
    public void FollowStart_ForcesSeek_EvenWhenDeficitIsBelowLearnedSeekCost()
    {
        // 検証機の M3: 学習済みシーク所要 ≈ 2.0 秒、追従開始時の不足 1.8 秒。
        // 定常なら 1.8 < 2.0 で速度補正だが、着地窓の中はその比較より手前で無効になる。
        // D37-e: さらに追従開始の行き先は LTC + 学習値（2.8 + 2.0 = 4.8）になる。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 10);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        LearnSeekDuration(h, clock, seconds: 2.0);
        h.SetSyncEnabled(false);                        // まだ追従していない
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SetSyncEnabled(true);
        h.SupplyLtc(2.8);                               // 不足 1.8 秒（< 学習済み 2.0 秒）

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(4.8, 1e-9, "LTC 2.8 + 学習値 2.0（D37-e）");
        h.RateAttempts.Should().BeEmpty("着地窓の中では速度補正を選ばない");
    }

    [Fact]
    public void SteadyDeficitBelowLearnedSeekCost_WithoutLandingWindow_UsesRateCatchUp()
    {
        // 上の対照: 同じ 1.8 秒・同じ学習値でも、着地窓がなければ既存ルールどおり速度補正。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 10);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        LearnSeekDuration(h, clock, seconds: 2.0);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SupplyLtc(2.8);                               // 追従開始イベントなし

        h.SeekState.LearnedSeekDurationSeconds.Should().BeApproximately(2.0, 1e-6);
        h.Operations.Should().NotContain(o => o.Name == "seek");
        h.RateAttempts.Should().NotBeEmpty("1.8 < 2.0 のときの既存ルール（速度補正）を変えない");
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

    /// <summary>保留状態を直接動かして、シーク所要の学習値を作る（製品経路は通らない）。</summary>
    private static void LearnSeekDuration(SyncScenarioHarness harness, ManualTimeProvider clock, double seconds)
    {
        harness.SeekState.BeginSeek(1.0, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        // 最初の到達（クールダウン中はまだ settle しない）。
        harness.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        // クールダウン明けの settle で、発行からの実測時間が学習される。
        harness.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        harness.SeekState.LearnedSeekDurationSeconds.Should().BeApproximately(seconds, 1e-6);
        // settle 後の PostSettleSuppress（500ms）を追い越して、次の判定に影響させない。
        clock.Advance(TimeSpan.FromMilliseconds(600));
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
