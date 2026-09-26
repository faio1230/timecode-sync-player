using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using TimecodeSyncPlayer.Tests.Integration;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// v0.5.3 段 3j: §6 の 4 の再現。保持（Duplicate）が理由の損失中に値が動いた Jump 1 枚で
/// 即時復帰（D27-b）しても、保持値 2 つ（LastHeldEffective / HeldLossLanding）は下りず、
/// Jump の直後に再び無音で損失すると古い保持値へ着地していた（段 0 の表は Keeps・意図 Clear）。
/// v0.5.3 段 3k で直した（Jump の即時復帰で保持値 2 つを下ろす）。
/// </summary>
public sealed class HeldLandingAfterJumpReproTests
{
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) Arrange()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 26, 0, 0, 0, TimeSpan.Zero));
        var h = new SyncScenarioHarness(clock, enableCorrection: true)
        {
            SignalLossMode = LtcSignalLossMode.Stop,
            GapBehavior = GapBehavior.Black,
        };
        h.AddTrack("A", 0, 30);
        h.ReloadProject();
        h.SetDurationSeconds(30);
        h.ManualPlay();
        h.AdvancePlayback(20.18);   // D35 の失敗帯（許容内の行き過ぎ）でも明示着地する位置
        return (h, clock);
    }

    /// <summary>保持フレームを供給しつつ有効フレームの時計を進め、timeout（250ms）超えで損失を確定させる。</summary>
    private static void HoldPastTimeout(SyncScenarioHarness h, double heldSeconds)
    {
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
        h.Tick100Milliseconds();
        h.SupplyHeldLtc(heldSeconds);
    }

    private static void SupplyJump(SyncScenarioHarness h, double seconds, long receivedAt) =>
        h.Controller.ReceiveProcessedFrame(
            new LtcFrameProcessingResult(
                "scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
                new TimecodeFrameDiagnosticResult(TimecodeFrameDiagnosticStatus.Jump, 0, 0),
                ShouldApplySync: false, ShouldLogFps: false),
            receivedAt);

    private static IReadOnlyList<double> SeekTargets(SyncScenarioHarness h) =>
        h.Operations.Where(o => o.Name == "seek").Select(o => o.Value ?? double.NaN).ToList();

    private static void Tick(SyncScenarioHarness h, ManualTimeProvider clock, int count)
    {
        for (int i = 0; i < count; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }
    }

    /// <summary>保持値 20.0 で停止・着地した後、値が動いた Jump 23.0 の 1 枚で復帰したところまで進める。</summary>
    private static (SyncScenarioHarness Harness, ManualTimeProvider Clock) ArrangeRecoveredFromHeldLoss()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = Arrange();
        h.SupplyLtc(20.0);
        h.Operations.Clear();

        HoldPastTimeout(h, 20.0);
        h.Tick100Milliseconds();   // 10_300: 保持の損失で一時停止し、保持値 20.0 へ着地
        h.IsPaused.Should().BeTrue("前提: 保持の損失で停止している");
        SeekTargets(h).Should().Equal(new[] { 20.0 }, "前提: 保持値 20.0 へ着地した");
        h.Operations.Clear();

        SupplyJump(h, 23.0, 10_300);   // D27-b: 保持が理由の損失中の Jump 1 枚で即時復帰
        h.IsPaused.Should().BeFalse("前提: Jump で即時復帰する");
        h.Operations.Clear();          // 復帰時のシーク（23.0）は見ない
        return (h, clock);
    }

    [Fact]
    public void HeldLossJumpRecovery_ThenSilentLoss_DoesNotLandOnTheOldHeldValue()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeRecoveredFromHeldLoss();

        // B から数フレーム進む（Normal は入れない）。3 回目の Tick で無音の損失が確定する。
        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.AdvancePlayback(h.PlaybackSeconds + 0.04, 1);
            h.Tick100Milliseconds();
        }

        h.IsPaused.Should().BeTrue("2 度目の無音損失で停止する");
        SeekTargets(h).Should().BeEmpty(
            "Jump の復帰で保持値 2 つが下りていれば、無音の損失は古い保持値へ着地しない（§6 の 4 の意図）");
    }

    [Fact]
    public void HeldLossJumpRecovery_ThenNormalFrames_ThenSilentLoss_DoesNotLandOnTheOldHeldValue()
    {
        (SyncScenarioHarness h, ManualTimeProvider clock) = ArrangeRecoveredFromHeldLoss();

        // B から数フレーム進む（Normal を入れる。OnNormalFrame が保持値 2 つを下ろす）。
        for (int i = 1; i <= 3; i++)
        {
            h.AdvancePlayback(23.0 + i * 0.04, 1);
            h.SupplyLtc(23.0 + i * 0.04);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            h.Tick100Milliseconds();
        }

        Tick(h, clock, 3);   // 無音の 2 度目の損失

        h.IsPaused.Should().BeTrue("2 度目の無音損失で停止する");
        SeekTargets(h).Should().BeEmpty("Normal で保持値が明けているため、古い保持値へ着地しない");
    }
}
