using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;
using TimecodeSyncPlayer.Tests.LatchLifetime;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.4 K5（§6 の 15）: 利用者が止めている間は、境界ホールドの解除・信号断の復帰・ギャップの解除の
/// 3 経路が同じ判定（<see cref="SyncRules.ShouldResumeOnPolicyPauseRelease"/>）を通り、再開しない。
/// 境界ホールドの経路は <see cref="BoundaryHoldPauseOwnerTests"/> の 1 件。ここは残りの 2 経路。
/// </summary>
public sealed class UserPauseOwnerTests
{
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) ArrangeSingle()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
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

    [Fact]
    public void SignalLossRecovery_WhileUserClaimedPause_DoesNotResumePlayback()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeSingle();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AdvancePlayback(24.94);

        h.SupplyLtc(24.9);
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
        h.IsPaused.Should().BeTrue("前提: 信号断のポリシーが止めている");

        // 配線の確認のための配置: ポリシーが持つ一時停止に、利用者の「止めている」も重ねる。
        h.ManualPause();

        h.SupplyLtc(10.05);
        h.SupplyLtc(10.10);
        h.SupplyLtc(10.15);

        h.IsPaused.Should().BeTrue(
            "利用者が止めている間は、信号断の復帰で再開しない（§6 の 15）");
        h.Operations.Should().NotContain(o => o.Name == "signal-loss-resume",
            "復帰の副作用（playback resumed）を出さない");
    }

    [Fact]
    public void GapExit_WhileUserClaimedPause_DoesNotResumePlayback()
    {
        LatchLifetimeScenario s = LatchLifetimeScenario.Continue();
        SyncScenarioHarness h = s.Harness;
        h.GapBehavior = GapBehavior.Black;

        s.Frame(35.0);   // ギャップへ入る（Black はギャップが pause を持つ）
        h.IsGapActive.Should().BeTrue("前提: ギャップの中");
        h.IsPaused.Should().BeTrue("前提: ギャップが止めている");

        // 配線の確認のための配置: ギャップが持つ一時停止に、利用者の「止めている」も重ねる。
        h.ManualPause();
        h.SetSyncEnabled(false);   // ExitGapForManualControl の経路

        h.IsPaused.Should().BeTrue(
            "利用者が止めている間は、ギャップの解除で再開しない（§6 の 15）");
    }
}
