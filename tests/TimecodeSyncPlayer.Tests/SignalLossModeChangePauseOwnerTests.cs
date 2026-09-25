using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 段 3i: §6 の 9（停止モードで信号断により一時停止中にランスルーへ変えると、
/// 信号断の一時停止を解いて再開する）。再開はほかの一時停止の持ち主（境界ホールド・ギャップ・
/// プロジェクト復元）がいるときだけ見送る（2g の持ち主の集合と同じ判定）。
/// </summary>
public sealed class SignalLossModeChangePauseOwnerTests
{
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true) { SignalLossMode = LtcSignalLossMode.Stop };
        h.AddTrack("A", 0, 200);
        h.SetDurationSeconds(200);
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

    [Fact]
    public void SignalLossModeChange_ToRunThrough_WhilePolicyPaused_ResumesPlayback()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.AdvancePlayback(12.0, 5);
        h.SupplyLtc(12.0);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue("前提: 停止モードの信号断でポリシーが止めている");
        h.Controller.SignalLossLatchSnapshot()["pausedByPolicy"].Should().BeTrue("前提: ポリシー所有の一時停止");
        h.Operations.Clear();

        h.SignalLossMode = LtcSignalLossMode.RunThrough;
        h.Controller.SignalLossModeChanged();

        h.IsPaused.Should().BeFalse("ランスルーへ変えると信号断の一時停止を解いて再開する（§6 の 9）");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
        h.Controller.SignalLossLatchSnapshot()["pausedByPolicy"].Should().BeFalse();
        h.Controller.SignalLossLatchSnapshot()["lost"].Should().BeTrue(
            "損失の印は残す（ランスルーでは損失中でも ShouldSuppressSync は立たない）");
        h.Controller.SignalLossLatchSnapshot()["manualResumeSuppressesPause"].Should().BeFalse();
    }

    [Fact]
    public void SignalLossModeChange_ToRunThrough_WhileBoundaryHeld_KeepsPlaybackPaused()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.MediaInSeconds = 5.0;
        h.MediaOutSeconds = 25.0;
        h.AdvancePlayback(24.94, 5);
        h.SupplyLtc(24.9);
        Tick(h, clock, 3);
        h.IsPaused.Should().BeTrue("前提: 信号断のポリシーが止めている");

        h.SupplyHeldLtc(40.0);   // 範囲外の保持 → 境界ホールド（SetEndHold(true)）
        h.Single.LatchSnapshot()["clipBoundaryHeld"].Should().BeTrue("前提: 境界ホールドが止めている");
        h.Operations.Clear();

        h.SignalLossMode = LtcSignalLossMode.RunThrough;
        h.Controller.SignalLossModeChanged();

        h.IsPaused.Should().BeTrue("ほかの持ち主（境界ホールド）が止めている間は再開しない");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume");
        h.Controller.SignalLossLatchSnapshot()["pausedByPolicy"].Should().BeFalse(
            "ポリシーの一時停止は解く（再開だけを見送る）");
    }
}
