using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 D38: 保持後のジャンプの着地の遅れ（`docs/design/v0.5.3-d38-seek-gates.md` §6）。
/// (a) 同期を適用しないフレーム（保持の Duplicate）でも、保留の着地を観測して位置の信頼を戻す。
/// (b) 位置が未信頼でも、pending から 4×tolerance を超える新しい要求は置き換える。
/// 門 3（JumpAppliedOnce のラッチ）は v0.5.4 U1 で消した（このファイルの該当テストも削除）。
/// </summary>
[Collection("Serilog global logger")]
public sealed class D38SeekGatesTests
{
    private static void Raw(SyncScenarioHarness h, double seconds, long at, double detectedFps = 25.0)
    {
        int frame = (int)Math.Round(seconds * 25.0);
        var timecode = new LtcTimecode(
            frame / (25 * 3600), (frame / (25 * 60)) % 60, (frame / 25) % 60, frame % 25, false);
        h.Controller.ReceiveFrame(new LtcFrameReceivedEventArgs(timecode, detectedFps, seconds, 0, 0), at);
    }

    [Fact]
    public void HeldDuplicate_WhenPlaybackReachesPendingTarget_SettlesAndRestoresPositionTrust()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true);
        h.AddTrack("A", 0, 30);
        h.ManualPlay();
        h.AdvancePlayback(10.0);

        // 着地シークを発行した状態（保留 + 位置は未信頼）。
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue("前提: シークの保留がある");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: シーク中は位置を信頼しない");

        // 保持の Duplicate。位置は既に着地の窓（10.0±tol）の中。200ms の cooldown を跨ぐ。
        h.SupplyHeldLtc(5.0);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        h.SupplyHeldLtc(5.0);

        h.SyncService.SeekState.HasPendingSeek.Should().BeFalse(
            "保持の Duplicate でも着地を観測して Settled になる（D38 (a)）");
        h.SyncService.IsPlaybackPositionUsable.Should().BeTrue(
            "Settled で位置の信頼が戻る（D38 (a)）");
    }

    [Fact]
    public void Untrusted_FarNewRequest_DiscardsUnreachablePendingWithoutWaitingForTimeout()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true);
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(5.0);

        // 保留の目標 10.0（再生位置 5.0 からは届かない）。位置は未信頼。
        h.SyncService.ReportSeekSent(10.0);
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse("前提: シーク中は位置を信頼しない");

        // 未信頼のまま、pending の目標から 4×tolerance を超える要求（LTC 20.0 → 目標 20.0）。
        h.SupplyLtc(20.0);

        // v0.5.4 段 B / 門 8 / §9-2: 着地待ちの目標を置き換え、その場で置き換えのシークを出す
        // （2 秒のタイムアウトを待たない）。位置は置き換えの着地まで使わない。
        h.SyncService.SeekState.TargetSeconds.Should().BeApproximately(20.0, 1e-6,
            "未信頼でも 4×tolerance を超える新しい要求は着地待ちの目標を置き換える（D38 (b)）");
        h.SyncService.SeekState.HasPendingSeek.Should().BeTrue(
            "着地待ちは維持する（位置を使わないまま、同じ手順で新しい着地待ちに入る）");
        h.SyncService.IsPlaybackPositionUsable.Should().BeFalse(
            "置き換えの着地まで位置を使わない（古い位置で判定・補正をしない）");

        // デバウンス（250ms）を明けて、置き換えのシークがその場で出ることを確かめる。
        clock.Advance(TimeSpan.FromMilliseconds(300));
        h.Tick100Milliseconds();
        h.Operations.Should().Contain(o => o.Name == "seek" && Math.Abs((o.Value ?? 0) - 20.0) < 1e-6,
            "2 秒のタイムアウトを待たずに置き換えた目標へ着地する");
    }
}
