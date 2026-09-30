using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 B4b（chase モデルの規則 2）: Smooth の誤差 e は、照会した再生位置ではなく
/// 配信したフレームの PTS（<c>PlaybackPositionSample.DeliveredSeconds</c>、着地の判定と
/// 同じ源）で測る。不感帯は 5ms 固定をやめ、1 映像フレーム（fps が不明なら LTC の 1 フレーム）
/// にする。B3 の実機で速度の変更が増えた原因（着地直後の 10〜60ms のずれを小さな段で詰める）
/// を、補正の入力そのものを直して閉じる。
/// </summary>
public class B4bCorrectionResidualTests
{
    [Theory]
    [InlineData(0.010)]
    [InlineData(0.030)]
    [InlineData(0.060)]
    public void Smooth_QueryPositionIsAheadButDeliveredMatchesLtc_DoesNotCorrect(double offsetSeconds)
    {
        // S-1 ほかの増加: 着地が早く確定し、照会した位置は 10〜60ms 先行するが、
        // 配信したフレームの PTS は LTC と一致している。
        var h = new SyncScenarioHarness(enableCorrection: true);
        h.AddTrack("clip1", 0);
        h.ManualPlay();

        h.SupplyLtc(1.0);                                             // clip1 へ切替（配信 1.0 で着地）
        h.Playback.SetPositionWithoutDelivery(1.0 + offsetSeconds);   // 照会位置だけ先行させる
        h.AppliedRates.Clear();

        h.SupplyLtc(1.0);                                             // LTC = 配信フレーム

        h.AppliedRates.Should().BeEmpty(
            "配信したフレームの PTS が LTC と一致しているなら、照会位置のずれで補正しない（規則 2）");
    }

    [Fact]
    public void Smooth_DifferenceUnderOneVideoFrame_DoesNotCorrect()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.AdvancePlayback(1.01, renderedFrames: 2);   // 配信 1.01（25fps。1 フレーム = 40ms）
        h.AppliedRates.Clear();

        // v0.6.1 段 A: LTC の値は 25fps のフレーム境界に乗る（1.03 は送れない）。1 フレーム未満の差は配信の側で作る。
        h.SupplyLtc(1.04);                            // 差 +30ms < 1 映像フレーム

        h.AppliedRates.Should().BeEmpty("1 映像フレーム未満の差では Smooth を動かさない");
    }

    [Fact]
    public void Smooth_DifferenceOverOneVideoFrame_Corrects()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var h = new SyncScenarioHarness(clock, enableCorrection: true, getQpc: QpcFrom(clock));
        h.AddTrack("track", 0, duration: 5);
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        h.AdvancePlayback(1.01, renderedFrames: 2);
        h.AppliedRates.Clear();

        // v0.6.1 段 A: LTC は実時間どおり 40ms ごとに +1 フレーム（値は 25fps のフレーム境界）。配信も同じだけ進め、
        // 差は配信の側で作る。残差ゲートは直近 250ms（7 標本）の中央値なので、+60ms の標本が過半（4 つ目）に
        // なった時点で、中央値が +60ms になって初めて補正する。
        int frame = 26;                               // 1.04 s
        for (int i = 0; i < 8; i++, frame++)          // 1 映像フレーム未満（+30ms）: 動かない
            SupplyFrame(h, clock, frame, lagSeconds: 0.03);
        for (int i = 0; i < 4; i++, frame++)          // +60ms > 1 映像フレーム: 補正する
            SupplyFrame(h, clock, frame, lagSeconds: 0.06);

        h.AppliedRates.Should().ContainSingle().Which.Should().BeApproximately(1.06, 1e-9);
    }

    /// <summary>v0.6.1 段 A: 40ms 進めて、配信を LTC より lagSeconds 遅れた位置に置き、LTC の 1 フレームを送る。</summary>
    private static void SupplyFrame(SyncScenarioHarness h, ManualTimeProvider clock, int frame, double lagSeconds)
    {
        clock.Advance(TimeSpan.FromMilliseconds(40));
        double ltc = frame / 25.0;
        h.AdvancePlayback(ltc - lagSeconds);
        h.SupplyLtc(ltc);
    }

    private static Func<long> QpcFrom(ManualTimeProvider clock)
    {
        DateTimeOffset origin = clock.GetUtcNow();
        return () => (long)((clock.GetUtcNow() - origin).TotalSeconds * Stopwatch.Frequency);
    }
}
