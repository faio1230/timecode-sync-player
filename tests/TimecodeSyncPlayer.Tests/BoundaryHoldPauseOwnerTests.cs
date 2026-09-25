using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2g-1: §6 の 12（境界ホールドの解除 <c>SetEndHold(false)</c> が、ほかの持ち主が
/// 止めていても再生を再開する）を再現する赤いテスト。段 2g-2（一時停止の持ち主の集合）で
/// 直すまで <see cref="FactAttribute.Skip"/> を付けたままコミットする。
///
/// 期待は「持ち主の集合が空のときだけ再開する」。今は <c>SingleModeSyncCoordinator.ReleaseBoundaryHold</c>
/// が <c>SetEndHold(false)</c> を無条件に呼ぶため、信号断のポリシーや利用者が止めていても
/// 再生が再開してしまう。
/// </summary>
public sealed class BoundaryHoldPauseOwnerTests
{
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { GapBehavior = GapBehavior.Black };
        h.AddTrack("A", 0, 200);
        h.ReloadProject();
        h.SetDurationSeconds(200);
        h.MediaInSeconds = 5.0;
        h.MediaOutSeconds = 25.0;
        h.ChangeMode(SyncMode.Single);
        h.ManualPlay();
        return (h, clock);
    }

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    [Fact(Skip = "v0.5.2 段 2g-2 で直す（§6 の 12）")]
    public void BoundaryHoldRelease_WhileSignalLossPaused_DoesNotResumePlayback()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AdvancePlayback(24.94);

        h.SupplyLtc(24.9);   // 有効フレーム（損失判定の時計を開始）
        Tick(h, clock, 3);   // 250ms 超で信号断（停止モード）→ 一時停止
        h.IsPaused.Should().BeTrue("前提: 信号断のポリシーが止めている");

        h.SupplyHeldLtc(40.0);   // 範囲外の保持 → 境界ホールド
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");

        h.SupplyHeldLtc(10.0);   // 範囲内へ → 境界ホールド解除
        h.Operations.Should().Contain(o => o.Name == "clip-end-release");

        h.IsPaused.Should().BeTrue(
            "信号断のポリシーが止めている間は、境界ホールドの解除で再生を再開しない（§6 の 12 の期待）");
    }

    [Fact(Skip = "v0.5.2 段 2g-2 で直す（§6 の 12）")]
    public void BoundaryHoldRelease_WhileUserPaused_DoesNotResumePlayback()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);
        h.ManualPause();

        h.SupplyLtc(40.0);   // 範囲外 → 終端ホールド（利用者の一時停止はそのまま）
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("前提: 利用者が止めている");

        h.SupplyLtc(10.0);   // 範囲内へ → 境界ホールド解除
        h.Operations.Should().Contain(o => o.Name == "clip-end-release");

        h.IsPaused.Should().BeTrue(
            "利用者の一時停止は、境界ホールドの解除で解除されない（§6 の 12 の期待。利用者はまだ持ち主として記録されていない）");
    }
}
