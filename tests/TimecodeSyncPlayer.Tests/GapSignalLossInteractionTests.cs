using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapSignalLossInteractionTests
{
    [Fact]
    public void SignalRecoveryAndGapEntry_InEitherOrder_ResumeAfterGapExit()
    {
        bool recoveryThenGapPaused = RunRecoveryAndGapEntry(recoveryBeforeGap: true);
        bool gapThenRecoveryPaused = RunRecoveryAndGapEntry(recoveryBeforeGap: false);

        recoveryThenGapPaused.Should().BeFalse();
        gapThenRecoveryPaused.Should().BeFalse(
            "recovered signal and a completed gap leave no remaining pause owner");
    }

    private static bool RunRecoveryAndGapEntry(bool recoveryBeforeGap)
    {
        const long start = 10_000;
        var signalLoss = new LtcSignalLossPolicy(
            TimeSpan.FromMilliseconds(250),
            resumeFrameCount: 1);
        var gap = new GapFreezeHandler();
        bool paused = false;

        LtcSignalLossContext Context(bool isGapActive) => new(
            LtcSignalLossMode.Stop,
            SyncEnabled: true,
            IsMonitoring: true,
            IsGapActive: isGapActive,
            IsPlaybackPaused: paused);

        signalLoss.ObserveValidFrame(start, Context(isGapActive: false));
        signalLoss.Evaluate(start + 250, Context(isGapActive: false))
            .Should().Be(LtcSignalLossAction.Pause);
        paused = true;

        if (recoveryBeforeGap)
        {
            ApplySignalAction(signalLoss.ObserveValidFrame(start + 300, Context(false)), ref paused);
            EnterGap(gap, paused);
            paused = true;
        }
        else
        {
            EnterGap(gap, paused);
            ApplySignalAction(signalLoss.ObserveValidFrame(start + 300, Context(true)), ref paused);
        }

        GapExitAction gapExit = gap.DecideGapExit();
        if (gapExit.ShouldResumePlayback)
            paused = false;

        return paused;
    }

    private static void EnterGap(GapFreezeHandler gap, bool wasPlaybackPaused)
    {
        gap.CurrentState = GapState.BlackFrameActive;
        gap.RecordPauseOwnership(wasPlaybackPaused);
    }

    private static void ApplySignalAction(LtcSignalLossAction action, ref bool paused)
    {
        if (action == LtcSignalLossAction.Pause)
            paused = true;
        else if (action == LtcSignalLossAction.ResumeAndSync)
            paused = false;
    }
}
