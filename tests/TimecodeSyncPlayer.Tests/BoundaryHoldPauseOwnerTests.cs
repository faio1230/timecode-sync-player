using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.2 段 2g-1/2g-2: §6 の 12（境界ホールドの解除 <c>SetEndHold(false)</c> が、ほかの持ち主が
/// 止めていても再生を再開する）を固定する。2g-2 で解除は
/// <see cref="SyncRules.ShouldResumeOnBoundaryHoldRelease"/> を通り、信号断が止めている間は
/// 再開しない。利用者が止めている件は v0.5.4 K5（§6 の 15）で利用者を持ち主として記録する。
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

    [Fact]
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

    [Fact]
    public void BoundaryHoldRelease_WhileSignalLossPaused_ResumesWhenSignalReturns()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.SignalLossMode = LtcSignalLossMode.Stop;
        h.AdvancePlayback(24.94);

        h.SupplyLtc(24.9);
        Tick(h, clock, 3);
        h.SupplyHeldLtc(40.0);
        h.SupplyHeldLtc(10.0);   // 解除。信号断が持っているので止まったまま
        h.IsPaused.Should().BeTrue("前提: 直した後は止まったまま");

        h.SupplyLtc(10.05);      // 有効フレーム 3 枚（resumeFrames=3）で信号断から復帰
        h.SupplyLtc(10.10);
        h.SupplyLtc(10.15);

        h.IsPaused.Should().BeFalse("信号が戻ったら #2（信号断の復帰）の経路で再開する");
        h.Operations.Should().Contain(o => o.Name == "signal-loss-resume");
    }

    [Fact]
    public void FileLoad_WhileBoundaryHeld_ReleasesWithResumeAndCleanup()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);

        h.SupplyLtc(40.0);   // 範囲外 → 終端ホールド（一時停止）
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("前提: 境界ホールドが止めている");
        h.Single.LatchSnapshot()["clipBoundaryHeld"].Should().BeTrue("前提: ホールドのラッチ");
        h.Operations.Clear();

        h.BeginManualFileLoad();   // FileLoad（読み込み）

        h.Single.LatchSnapshot()["clipBoundaryHeld"].Should().BeFalse(
            "v0.5.4 K5: 読み込みで前のファイルの境界ホールドを持ち越さない（§6 の 1）");
        h.Operations.Should().Contain(o => o.Name == "clip-end-release",
            "解除の副作用（再開・片付け）を落とさない（ラッチを消すだけにしない。段 3c の教訓）");
        h.IsPaused.Should().BeFalse("ほかの持ち主がいなければ解除で再開する");
    }

    [Fact]
    public void SyncDisabled_WhileBoundaryHeld_ClearsLatchAndKeepsPause()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);

        h.SupplyLtc(40.0);   // 範囲外 → 終端ホールド（一時停止）
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("前提: 境界ホールドが止めている");
        h.Single.LatchSnapshot()["clipBoundaryHeld"].Should().BeTrue("前提: ホールドのラッチ");

        h.SetSyncEnabled(false);

        h.Single.LatchSnapshot()["clipBoundaryHeld"].Should().BeFalse(
            "v0.5.3 段 3c: 同期の無効化でホールドのラッチを消す（§6 の 1）");
        h.IsPaused.Should().BeTrue(
            "ラッチだけを消し、境界ホールドの一時停止は利用者の再生までそのまま（SetEndHold(false) を呼ばない）");
        h.Operations.Should().NotContain(o => o.Name == "clip-end-release",
            "解除のできごと（SetEndHold(false) / BoundaryHoldReleased）は出さない");
    }

    [Fact]
    public void BoundaryHoldRelease_WhileUserPaused_DoesNotResumePlayback()
    {
        (SyncScenarioHarness h, _) = Arrange();
        h.AdvancePlayback(25.0);
        h.ManualPause();

        h.SupplyLtc(40.0);   // 範囲外 → 終端ホールド（利用者の一時停止はそのまま）
        h.Operations.Should().Contain(o => o.Name == "clip-end-hold");
        h.IsPaused.Should().BeTrue("前提: 利用者が止めている");

        h.SupplyLtc(10.0);   // 範囲内へ（Jump とその確認の同値）→ 境界ホールド解除
        h.SupplyLtc(10.0);
        h.Operations.Should().Contain(o => o.Name == "clip-end-release");

        h.IsPaused.Should().BeTrue(
            "利用者の一時停止は、境界ホールドの解除で解除されない（§6 の 15。利用者も持ち主として記録する）");
    }
}
