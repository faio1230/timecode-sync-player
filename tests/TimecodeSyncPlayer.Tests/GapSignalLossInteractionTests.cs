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

    [Fact]
    public void SignalRecoveryBlockedByGap_WhenOperatorPlays_ManualPlayWins()
    {
        const long start = 20_000;
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

        signalLoss.ObserveValidFrame(start, Context(false));
        ApplySignalAction(signalLoss.Evaluate(start + 250, Context(false)), ref paused);
        EnterGap(gap, paused);

        signalLoss.ObserveValidFrame(start + 300, Context(true))
            .Should().Be(LtcSignalLossAction.None);

        paused = false;
        signalLoss.Evaluate(start + 320, Context(true))
            .Should().Be(LtcSignalLossAction.None);

        gap.DecideGapExit().ShouldResumePlayback.Should().BeFalse();
        signalLoss.ObserveValidFrame(start + 340, Context(false))
            .Should().Be(LtcSignalLossAction.None);
        paused.Should().BeFalse("the operator's manual Play must win over both policies");
        signalLoss.ShouldSuppressSync.Should().BeFalse();
    }

    [Fact]
    public void SignalOwnedPause_WhenModeChangeExitsGap_RemainsOwnedBySignalPolicy()
    {
        const long start = 30_000;
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

        signalLoss.ObserveValidFrame(start, Context(false));
        ApplySignalAction(signalLoss.Evaluate(start + 250, Context(false)), ref paused);
        paused.Should().BeTrue();
        signalLoss.ShouldSuppressSync.Should().BeTrue();

        EnterGap(gap, wasPlaybackPaused: paused);
        GapStateExitPolicy.ShouldExit(
                syncEnabled: true,
                SyncMode.Single,
                gapStateActive: true)
            .Should().BeTrue();

        GapExitAction gapExit = gap.DecideGapExit();
        gapExit.ShouldResumePlayback.Should().BeFalse(
            "the gap did not acquire a pause that it may release");
        paused.Should().BeTrue();
        signalLoss.ShouldSuppressSync.Should().BeTrue(
            "a mode-triggered gap exit must not consume signal-loss ownership");

        ApplySignalAction(signalLoss.ObserveValidFrame(start + 300, Context(false)), ref paused);
        paused.Should().BeFalse();
        signalLoss.ShouldSuppressSync.Should().BeFalse();
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
            LtcSignalLossAction blockedRecovery =
                signalLoss.ObserveValidFrame(start + 300, Context(true));
            blockedRecovery.Should().Be(
                LtcSignalLossAction.None,
                "a gap blocks application and consumption of the policy-owned resume");
            ApplySignalAction(blockedRecovery, ref paused);
        }

        GapExitAction gapExit = gap.DecideGapExit();
        if (gapExit.ShouldResumePlayback)
            paused = false;

        ApplySignalAction(signalLoss.ObserveValidFrame(start + 340, Context(false)), ref paused);

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
