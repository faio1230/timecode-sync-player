using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SyncRulesTests
{
    [Fact]
    public void CanEvaluateCorrection_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanEvaluateCorrection(
            syncEnabled: true,
            isMonitoring: true,
            isPlaybackPaused: false,
            isSeeking: false,
            hasPendingSeek: false,
            isPlaybackPositionUsable: true).Should().BeTrue();

    [Theory]
    [InlineData(false, true, false, false, false, true)]  // syncEnabled
    [InlineData(true, false, false, false, false, true)]  // isMonitoring
    [InlineData(true, true, true, false, false, true)]    // isPlaybackPaused
    [InlineData(true, true, false, true, false, true)]    // isSeeking
    [InlineData(true, true, false, false, true, true)]    // hasPendingSeek
    [InlineData(true, true, false, false, false, false)]  // isPlaybackPositionUsable
    public void CanEvaluateCorrection_OneTermFails_ReturnsFalse(
        bool syncEnabled,
        bool isMonitoring,
        bool isPlaybackPaused,
        bool isSeeking,
        bool hasPendingSeek,
        bool isPlaybackPositionUsable) =>
        SyncRules.CanEvaluateCorrection(
            syncEnabled, isMonitoring, isPlaybackPaused, isSeeking,
            hasPendingSeek, isPlaybackPositionUsable).Should().BeFalse();

    [Fact]
    public void CanApplySync_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanApplySync(
            isPlayerReady: true,
            isMonitoring: true,
            syncEnabled: true,
            isSeeking: false,
            shouldSuppressSync: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, true, false, false)]  // isPlayerReady
    [InlineData(true, false, true, false, false)]  // isMonitoring
    [InlineData(true, true, false, false, false)]  // syncEnabled
    [InlineData(true, true, true, true, false)]    // isSeeking
    [InlineData(true, true, true, false, true)]    // shouldSuppressSync
    public void CanApplySync_OneTermFails_ReturnsFalse(
        bool isPlayerReady,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking,
        bool shouldSuppressSync) =>
        SyncRules.CanApplySync(
            isPlayerReady, isMonitoring, syncEnabled, isSeeking, shouldSuppressSync)
            .Should().BeFalse();

    [Fact]
    public void CanReapplyLastAccepted_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanReapplyLastAccepted(
            hasAcceptedFrame: true,
            isMonitoring: true,
            syncEnabled: true,
            isSeeking: false,
            shouldSuppressSync: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, true, false, false)]  // hasAcceptedFrame
    [InlineData(true, false, true, false, false)]  // isMonitoring
    [InlineData(true, true, false, false, false)]  // syncEnabled
    [InlineData(true, true, true, true, false)]    // isSeeking
    [InlineData(true, true, true, false, true)]    // shouldSuppressSync
    public void CanReapplyLastAccepted_OneTermFails_ReturnsFalse(
        bool hasAcceptedFrame,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking,
        bool shouldSuppressSync) =>
        SyncRules.CanReapplyLastAccepted(
            hasAcceptedFrame, isMonitoring, syncEnabled, isSeeking, shouldSuppressSync)
            .Should().BeFalse();

    [Fact]
    public void CanApplySync_And_CanReapplyLastAccepted_DifferOnIsPlayerReady()
    {
        SyncRules.CanApplySync(
            isPlayerReady: false, isMonitoring: true, syncEnabled: true,
            isSeeking: false, shouldSuppressSync: false).Should().BeFalse();
        SyncRules.CanReapplyLastAccepted(
            hasAcceptedFrame: true, isMonitoring: true, syncEnabled: true,
            isSeeking: false, shouldSuppressSync: false).Should().BeTrue();
    }

    [Fact]
    public void CanReapplyAfterFileLoadRelease_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanReapplyAfterFileLoadRelease(
            isPlayerReady: true,
            isMonitoring: true,
            syncEnabled: true,
            isSeeking: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, true, false)]  // isPlayerReady
    [InlineData(true, false, true, false)]  // isMonitoring
    [InlineData(true, true, false, false)]  // syncEnabled
    [InlineData(true, true, true, true)]    // isSeeking
    public void CanReapplyAfterFileLoadRelease_OneTermFails_ReturnsFalse(
        bool isPlayerReady,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking) =>
        SyncRules.CanReapplyAfterFileLoadRelease(
            isPlayerReady, isMonitoring, syncEnabled, isSeeking).Should().BeFalse();

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldSkipHeldLanding_ReturnsBoundaryHoldState(bool isBoundaryHeld, bool expected) =>
        SyncRules.ShouldSkipHeldLanding(isBoundaryHeld).Should().Be(expected);

    [Fact]
    public void CanPauseForSignalLoss_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanPauseForSignalLoss(
            mode: LtcSignalLossMode.Stop,
            syncEnabled: true,
            isGapActive: false,
            isPlaybackPaused: false,
            pausedByPolicy: false,
            manualResumeSuppressesPause: false).Should().BeTrue();

    [Theory]
    [InlineData(LtcSignalLossMode.RunThrough, true, false, false, false, false)]  // mode
    [InlineData(LtcSignalLossMode.Stop, false, false, false, false, false)]       // syncEnabled
    [InlineData(LtcSignalLossMode.Stop, true, true, false, false, false)]         // isGapActive
    [InlineData(LtcSignalLossMode.Stop, true, false, true, false, false)]         // isPlaybackPaused
    [InlineData(LtcSignalLossMode.Stop, true, false, false, true, false)]         // pausedByPolicy
    [InlineData(LtcSignalLossMode.Stop, true, false, false, false, true)]         // manualResumeSuppressesPause
    public void CanPauseForSignalLoss_OneTermFails_ReturnsFalse(
        LtcSignalLossMode mode,
        bool syncEnabled,
        bool isGapActive,
        bool isPlaybackPaused,
        bool pausedByPolicy,
        bool manualResumeSuppressesPause) =>
        SyncRules.CanPauseForSignalLoss(
            mode, syncEnabled, isGapActive, isPlaybackPaused,
            pausedByPolicy, manualResumeSuppressesPause).Should().BeFalse();

    [Fact]
    public void CanResumeAfterSignalLoss_AllTermsSatisfied_ReturnsTrue() =>
        SyncRules.CanResumeAfterSignalLoss(
            syncEnabled: true,
            isMonitoring: true,
            isGapActive: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, false)]  // syncEnabled
    [InlineData(true, false, false)]  // isMonitoring
    [InlineData(true, true, true)]    // isGapActive
    public void CanResumeAfterSignalLoss_OneTermFails_ReturnsFalse(
        bool syncEnabled,
        bool isMonitoring,
        bool isGapActive) =>
        SyncRules.CanResumeAfterSignalLoss(syncEnabled, isMonitoring, isGapActive)
            .Should().BeFalse();

    [Theory]
    [InlineData((int)PauseOwners.None, true)]
    [InlineData((int)PauseOwners.SignalLoss, false)]
    [InlineData((int)PauseOwners.BoundaryHold, false)]
    [InlineData((int)PauseOwners.Gap, false)]
    [InlineData((int)PauseOwners.ProjectRestore, false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.Gap), false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.BoundaryHold |
        PauseOwners.Gap | PauseOwners.ProjectRestore), false)]
    public void ShouldResumeOnBoundaryHoldRelease_TruthTable(int otherOwners, bool expected) =>
        SyncRules.ShouldResumeOnBoundaryHoldRelease((PauseOwners)otherOwners).Should().Be(expected);

    [Theory]
    [InlineData(false, false, false, (int)PauseOwners.None)]
    [InlineData(true, false, false, (int)PauseOwners.SignalLoss)]
    [InlineData(false, true, false, (int)PauseOwners.Gap)]
    [InlineData(false, false, true, (int)PauseOwners.ProjectRestore)]
    [InlineData(true, true, true,
        (int)(PauseOwners.SignalLoss | PauseOwners.Gap | PauseOwners.ProjectRestore))]
    public void CollectOtherPauseOwners_MapsEachFlag(
        bool isSignalLossPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused,
        int expected) =>
        SyncRules.CollectOtherPauseOwners(
            isSignalLossPauseOwned, isGapPauseOwned, isProjectRestorePaused)
            .Should().Be((PauseOwners)expected);

    [Theory]
    [InlineData((int)PauseOwners.None, true)]
    [InlineData((int)PauseOwners.BoundaryHold, false)]
    [InlineData((int)PauseOwners.Gap, false)]
    [InlineData((int)PauseOwners.ProjectRestore, false)]
    [InlineData((int)(PauseOwners.BoundaryHold | PauseOwners.Gap), false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.BoundaryHold |
        PauseOwners.Gap | PauseOwners.ProjectRestore), false)]
    public void ShouldResumeOnPolicyPauseRelease_TruthTable(int otherOwners, bool expected) =>
        SyncRules.ShouldResumeOnPolicyPauseRelease((PauseOwners)otherOwners).Should().Be(expected);

    [Theory]
    [InlineData(false, false, false, (int)PauseOwners.None)]
    [InlineData(true, false, false, (int)PauseOwners.BoundaryHold)]
    [InlineData(false, true, false, (int)PauseOwners.Gap)]
    [InlineData(false, false, true, (int)PauseOwners.ProjectRestore)]
    [InlineData(true, true, true,
        (int)(PauseOwners.BoundaryHold | PauseOwners.Gap | PauseOwners.ProjectRestore))]
    public void CollectPauseOwnersExceptSignalLoss_MapsEachFlag(
        bool isBoundaryHoldPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused,
        int expected) =>
        SyncRules.CollectPauseOwnersExceptSignalLoss(
            isBoundaryHoldPauseOwned, isGapPauseOwned, isProjectRestorePaused)
            .Should().Be((PauseOwners)expected);
}
