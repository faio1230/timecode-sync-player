using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D37-e: 追従開始のシークは「LTC + 学習済みシーク所要」を狙う（上限なし・クリップ範囲でクランプ）。
/// 定常の補正シークとギャップ明け・切替には広げない。
/// </summary>
public class D37eFollowStartLookaheadTests
{
    [Fact]
    public void FollowStart_WithLearnedSeekCost_TargetsLtcPlusLearnedCost()
    {
        // 検証機 M3: 所要 1.8〜2.0 秒に対し、着地時の LTC ぶんを先に狙う。
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
        h.SupplyLtc(2.8);                               // 不足 1.8（> 0.5 × 学習値 2.0）

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(4.8, 1e-9, "LTC 2.8 + 学習済み所要 2.0");
    }

    [Fact]
    public void FollowStart_WithoutLearnedSeekCost_AndWithoutHint_TargetsLtc()
    {
        // 学習値もスキャンの見積もりも無ければ先行しない（旧 DLL 等でスキャンできない場合）。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 10);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.SetSyncEnabled(false);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SetSyncEnabled(true);
        h.SupplyLtc(2.8);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(2.8, 1e-9, "見積もりが無ければ LTC を狙う");
    }


    [Fact]
    public void FollowStart_WithoutLearnedSeekCost_UsesScanHint()
    {
        // D37-f: ロード直後の 1 回目は学習値が無い（ロードで ResetLearning するため）。
        // 検証機の実測では、この 1 回目のシーク先が LTC と完全に一致しており
        // （7.363 / 9.497 / 12.238）、先行分が 1 秒も足されていなかった。
        // その 1 回目の所要 1.79 秒がそのまま残差になり、追従開始が 16 秒かかっていた。
        // 0.4.5-C3 のスキャンから見積もりを渡し、1 回目から先行させる。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 20);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.SetSyncEnabled(false);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        // M3 相当: 最大ギャップ 6.633 秒 × 0.3 = 1.99 秒。
        h.SyncService.SetSeekCostHintSeconds(1.99);

        h.SetSyncEnabled(true);
        h.SupplyLtc(2.8);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(4.79, 1e-9,
                "LTC 2.8 + 見積もり 1.99。検証機で観測された「シーク先 == LTC」を再発させない");
    }

    [Fact]
    public void FollowStart_LearnedSeekCostWinsOverScanHint()
    {
        // 実測が入ったら見積もりより学習値を使う。
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 20);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        LearnSeekDuration(h, clock, seconds: 2.0);
        h.SyncService.SetSeekCostHintSeconds(0.5);   // 見積もりは外れている
        h.SetSyncEnabled(false);
        h.AdvancePlayback(1.0, renderedFrames: 2);
        h.Operations.Clear();
        h.RateAttempts.Clear();

        h.SetSyncEnabled(true);
        h.SupplyLtc(2.8);

        h.Operations.Where(o => o.Name == "seek").Should().ContainSingle()
            .Which.Value.Should().BeApproximately(4.8, 1e-9, "学習値 2.0 が見積もり 0.5 に勝つ");
    }

    /// <summary>保留状態を直接動かして、シーク所要の学習値を作る（製品経路は通らない）。</summary>
    private static void LearnSeekDuration(SyncScenarioHarness harness, ManualTimeProvider clock, double seconds)
    {
        harness.SeekState.BeginSeek(1.0, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromSeconds(seconds));
        harness.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        harness.SeekState.ShouldSuppressSeek(1.0, 0.24, clock.GetUtcNow().UtcDateTime);
        harness.SeekState.LearnedSeekDurationSeconds.Should().BeApproximately(seconds, 1e-6);
        // settle 後の PostSettleSuppress（500ms）を追い越す。
        clock.Advance(TimeSpan.FromMilliseconds(600));
    }

    /// <summary>ManualTimeProvider と同じ時計を QPC 秒として渡す（ゲートの dt を決定的にする）。</summary>
    private static Func<long> QpcFrom(ManualTimeProvider clock)
    {
        DateTimeOffset origin = clock.GetUtcNow();
        return () => (long)((clock.GetUtcNow() - origin).TotalSeconds * Stopwatch.Frequency);
    }
}
