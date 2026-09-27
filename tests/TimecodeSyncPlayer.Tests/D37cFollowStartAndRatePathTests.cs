using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D37-c: 追従開始は既存の着地窓（D37-b2）に載せてシークで詰める／速度補正の残差にも
/// 粗い判定と同じ前処理（ありえない変化の除外・中央値）を通す。
/// v0.5.4 B6b: 追従開始の特別扱い（着地窓・先行量）は畳んだ。追従開始も定常と同じく
/// relocate の閾値 max(tol, 学習したシークの所要) で決まる（超えたらシーク、以内は速度補正）。
/// </summary>
public class D37cFollowStartAndRatePathTests
{
    // ── 穴 1: 追従開始はシークで着地する（B6b: 閾値を超える不足なら） ──────

    [Fact]
    public void FollowStart_FirstSyncRequest_ForDeficitBeyondTheThreshold_Seeks()
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
        h.SupplyLtc(2.5);                               // ずれ 1.5 秒（閾値 = 既定のシーク所要 1.0 秒を超える）

        // 速度補正ではなくシークで詰める（B6b: 追従開始の着地窓ではなく relocate の閾値で決まる）。
        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(2.5, 1e-9);
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
    public void MonitoringStart_WithSyncAlreadyEnabled_DeficitBeyondTheThreshold_Seeks()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();

        h.IsMonitoring = false;
        h.IsMonitoring = true;                          // 監視開始（同期は有効のまま）
        h.SupplyLtc(2.5);                               // 1.5 > 閾値 1.0（既定のシーク所要）

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(2.5, 1e-9);
    }

    [Fact]
    public void FollowStart_DeficitWithinLearnedSeekCost_UsesRateCatchUp()
    {
        // 検証機の M3: 学習済みシーク所要 ≈ 2.0 秒、追従開始時の不足 1.8 秒。
        // 旧（D37-c/e）は着地窓で強制シークし、行き先を LTC + 学習値（4.8）にしていた。
        // v0.5.4 B6b: 追従開始の特別扱いを畳んだので、定常と同じく 1.8 ≤ 閾値 2.0 は速度補正
        // （設計 B6b の区分表 §2 の D37-c 行: 閾値以内の強制シークは設計どおり消える）。
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
        h.SupplyLtc(2.8);                               // 不足 1.8 秒（< 学習済み 2.0 秒）。着地直後の 1 サンプル
        clock.Advance(TimeSpan.FromMilliseconds(40));
        h.SupplyLtc(2.8);                               // 補正を評価するサンプル

        h.Operations.Should().NotContain(o => o.Name == "seek", "閾値（学習値 2.0）以内は relocate しない");
        h.RateAttempts.Should().NotBeEmpty("閾値以内の不足は速度補正で詰める");
    }

    [Fact]
    public void FollowStart_DeficitBeyondLearnedSeekCost_SeeksToTheLtcWithoutLookahead()
    {
        // B6b: 閾値（学習値 2.0）を超える不足は relocate。先行量（D37-e）は畳んだので行き先は LTC。
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
        h.SupplyLtc(3.5);                               // 不足 2.5 秒（> 学習済み 2.0 秒）

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(3.5, 1e-9, "行き先は LTC（先行量なし）");
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

        h.SupplyLtc(2.8);                               // 追従開始イベントなし。着地直後の 1 サンプル（B6b）
        clock.Advance(TimeSpan.FromMilliseconds(40));
        h.SupplyLtc(2.8);                               // 補正を評価するサンプル

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

    /// <summary>着地の状態を直接動かして、シーク所要の学習値を作る（製品経路は通らない）。</summary>
    private static void LearnSeekDuration(SyncScenarioHarness harness, ManualTimeProvider clock, double seconds)
    {
        harness.SeekState.BeginSeek(1.0, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        // v0.5.4 段 B: 着地は配信の世代と位置の事象で取る（旧 門 6 の窓と cooldown は畳んだ）。
        harness.SeekState.ObserveLandingSample(
            new TimecodeSyncPlayer.Contracts.PlaybackPositionSample(
                1.0, TimecodeSyncPlayer.Contracts.PlaybackPositionBasis.Pipeline, 1, 1.0, 1, 1),
            0.24, clock.GetUtcNow().UtcDateTime);
        harness.SeekState.LearnedSeekDurationSeconds.Should().BeApproximately(seconds, 1e-6);
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
