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
            isWaitingForLanding: false).Should().BeTrue();

    [Theory]
    [InlineData(false, true, false, false, false)]  // syncEnabled
    [InlineData(true, false, false, false, false)]  // isMonitoring
    [InlineData(true, true, true, false, false)]    // isPlaybackPaused
    [InlineData(true, true, false, true, false)]    // isSeeking
    [InlineData(true, true, false, false, true)]    // isWaitingForLanding（v0.5.4 U4 で 1 つに）
    public void CanEvaluateCorrection_OneTermFails_ReturnsFalse(
        bool syncEnabled,
        bool isMonitoring,
        bool isPlaybackPaused,
        bool isSeeking,
        bool isWaitingForLanding) =>
        SyncRules.CanEvaluateCorrection(
            syncEnabled, isMonitoring, isPlaybackPaused, isSeeking,
            isWaitingForLanding).Should().BeFalse();

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
    [InlineData((int)PauseOwners.None, false)]
    [InlineData((int)PauseOwners.BoundaryHold, true)]
    [InlineData((int)PauseOwners.SignalLoss, false)]
    [InlineData((int)PauseOwners.Gap, false)]
    [InlineData((int)PauseOwners.ProjectRestore, false)]
    [InlineData((int)PauseOwners.User, false)]
    [InlineData((int)(PauseOwners.BoundaryHold | PauseOwners.Gap), true)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.BoundaryHold |
        PauseOwners.Gap | PauseOwners.ProjectRestore | PauseOwners.User), true)]
    public void ShouldSkipHeldLanding_TrueWhenBoundaryHoldIsAPauseOwner(int owners, bool expected) =>
        SyncRules.ShouldSkipHeldLanding((PauseOwners)owners).Should().Be(expected);

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
    [InlineData((int)PauseOwners.User, false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.Gap), false)]
    [InlineData((int)(PauseOwners.User | PauseOwners.Gap), false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.BoundaryHold |
        PauseOwners.Gap | PauseOwners.ProjectRestore | PauseOwners.User), false)]
    public void ShouldResumeOnBoundaryHoldRelease_TruthTable(int otherOwners, bool expected) =>
        SyncRules.ShouldResumeOnBoundaryHoldRelease((PauseOwners)otherOwners).Should().Be(expected);

    [Theory]
    [InlineData(false, false, false, false, (int)PauseOwners.None)]
    [InlineData(true, false, false, false, (int)PauseOwners.SignalLoss)]
    [InlineData(false, true, false, false, (int)PauseOwners.Gap)]
    [InlineData(false, false, true, false, (int)PauseOwners.ProjectRestore)]
    [InlineData(false, false, false, true, (int)PauseOwners.User)]
    [InlineData(true, true, true, true,
        (int)(PauseOwners.SignalLoss | PauseOwners.Gap | PauseOwners.ProjectRestore | PauseOwners.User))]
    public void CollectOtherPauseOwners_MapsEachFlag(
        bool isSignalLossPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused,
        bool isUserPaused,
        int expected) =>
        SyncRules.CollectOtherPauseOwners(
            isSignalLossPauseOwned, isGapPauseOwned, isProjectRestorePaused, isUserPaused)
            .Should().Be((PauseOwners)expected);

    [Fact]
    public void CollectOtherPauseOwners_UserIsAnOwnerAndBlocksBothResumeDecisions()
    {
        PauseOwners owners = SyncRules.CollectOtherPauseOwners(
            isSignalLossPauseOwned: false, isGapPauseOwned: false, isProjectRestorePaused: false,
            isUserPaused: true);

        owners.Should().Be(PauseOwners.User, "利用者も一時停止の持ち主として数える（§6 の 15）");
        SyncRules.ShouldResumeOnBoundaryHoldRelease(owners).Should().BeFalse(
            "境界ホールドの解除の判定を通す");
        SyncRules.ShouldResumeOnPolicyPauseRelease(owners).Should().BeFalse(
            "信号断の復帰・ギャップの解除と同じ判定");
    }

    [Theory]
    [InlineData((int)PauseOwners.None, true)]
    [InlineData((int)PauseOwners.BoundaryHold, false)]
    [InlineData((int)PauseOwners.Gap, false)]
    [InlineData((int)PauseOwners.ProjectRestore, false)]
    [InlineData((int)PauseOwners.User, false)]
    [InlineData((int)(PauseOwners.BoundaryHold | PauseOwners.Gap), false)]
    [InlineData((int)(PauseOwners.User | PauseOwners.BoundaryHold), false)]
    [InlineData((int)(PauseOwners.SignalLoss | PauseOwners.BoundaryHold |
        PauseOwners.Gap | PauseOwners.ProjectRestore | PauseOwners.User), false)]
    public void ShouldResumeOnPolicyPauseRelease_TruthTable(int otherOwners, bool expected) =>
        SyncRules.ShouldResumeOnPolicyPauseRelease((PauseOwners)otherOwners).Should().Be(expected);

    [Theory]
    [InlineData(false, false, false, false, (int)PauseOwners.None)]
    [InlineData(true, false, false, false, (int)PauseOwners.BoundaryHold)]
    [InlineData(false, true, false, false, (int)PauseOwners.Gap)]
    [InlineData(false, false, true, false, (int)PauseOwners.ProjectRestore)]
    [InlineData(false, false, false, true, (int)PauseOwners.User)]
    [InlineData(true, true, true, true,
        (int)(PauseOwners.BoundaryHold | PauseOwners.Gap | PauseOwners.ProjectRestore | PauseOwners.User))]
    public void CollectPauseOwnersExceptSignalLoss_MapsEachFlag(
        bool isBoundaryHoldPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused,
        bool isUserPaused,
        int expected) =>
        SyncRules.CollectPauseOwnersExceptSignalLoss(
            isBoundaryHoldPauseOwned, isGapPauseOwned, isProjectRestorePaused, isUserPaused)
            .Should().Be((PauseOwners)expected);
}
