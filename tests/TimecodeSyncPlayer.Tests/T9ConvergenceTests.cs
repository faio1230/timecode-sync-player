using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// T9: 着地（トラック切替のロード成立・粗い同期シークの発行）から 1.0 秒だけ Smooth の
/// 速度上限を ±0.20 にする窓の検証。配線（LtcSyncController が着地を通知できること）と、
/// 窓が過ぎたら ±0.10 に戻ることをハーネス越しに固定する。
/// </summary>
public class T9ConvergenceTests
{
    [Fact]
    public void TrackSwitch_OpensTwentyPercentWindow_UntilOneSecondPasses()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();

        harness.SupplyLtc(1.0);                                   // clip1 へ切替（着地）
        harness.AdvancePlayback(1.1, renderedFrames: 2);
        harness.SupplyLtc(1.30);                                  // 残差 +200ms（窓の中）

        harness.AppliedRates.Should().NotBeEmpty();
        harness.AppliedRates[^1].Should().BeApproximately(1.20, 1e-9);

        clock.Advance(TimeSpan.FromSeconds(1.1));                 // 着地から 1.0 秒を過ぎる
        harness.AdvancePlayback(1.30, renderedFrames: 1);
        harness.SupplyLtc(1.50);                                  // 残差 +200ms（窓の外）

        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);
    }

    [Fact]
    public void CoarseSyncSeek_OpensTwentyPercentWindow_UntilOneSecondAfterIssued()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var harness = new SyncScenarioHarness(clock, enableCorrection: true);
        harness.AddTrack("clip1", 0);
        harness.ManualPlay();

        harness.SupplyLtc(1.0);                                   // clip1 へ切替（着地①）
        harness.AdvancePlayback(1.1, renderedFrames: 2);

        clock.Advance(TimeSpan.FromMilliseconds(400));            // ロード後デバウンス(250ms)を過ぎる
        harness.SupplyLtc(4.0);                                   // ロード成立。Jump は測定の乱れとして弾かれる
        clock.Advance(TimeSpan.FromMilliseconds(400));
        harness.SupplyLtc(4.0);                                   // D37-a: ゲート 1 サンプル目
        clock.Advance(TimeSpan.FromMilliseconds(100));
        harness.SupplyLtc(4.0);                                   // 2 サンプル目
        clock.Advance(TimeSpan.FromMilliseconds(100));
        harness.SupplyLtc(4.0);                                   // 粗い同期シーク発行（着地②、残差 2.9s）

        clock.Advance(TimeSpan.FromMilliseconds(600));
        harness.SupplyLtc(4.2);                                   // シークのセトル開始（まだ抑止中）

        clock.Advance(TimeSpan.FromMilliseconds(300));            // 着地②から 0.9 秒、セトル確定後
        harness.SupplyLtc(4.2);                                   // 最初の補正評価、残差 +200ms

        harness.AppliedRates.Should().NotBeEmpty();
        harness.AppliedRates[^1].Should().BeApproximately(1.20, 1e-9);

        clock.Advance(TimeSpan.FromMilliseconds(600));            // 着地②から 1.5 秒
        harness.AdvancePlayback(4.2, renderedFrames: 1);
        harness.SupplyLtc(4.4);                                   // 残差 +200ms、窓の外

        harness.AppliedRates[^1].Should().BeApproximately(1.10, 1e-9);
    }

    [Fact]
    public void ReportSeekSent_RaisesSeekIssued()
    {
        var service = new TimecodeSyncService(new SyncDecisionEngine(), new TimecodeSyncSeekState());
        int issued = 0;
        service.SeekIssued += () => issued++;

        service.ReportSeekSent(1.0);

        issued.Should().Be(1);
    }
}
