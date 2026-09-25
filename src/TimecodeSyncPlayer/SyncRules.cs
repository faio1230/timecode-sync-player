namespace TimecodeSyncPlayer;

internal static class SyncRules
{
    internal static bool CanEvaluateCorrection(
        bool syncEnabled,
        bool isMonitoring,
        bool isPlaybackPaused,
        bool isSeeking,
        bool hasPendingSeek,
        bool isPlaybackPositionUsable) =>
        syncEnabled && isMonitoring && !isPlaybackPaused && !isSeeking &&
        !hasPendingSeek && isPlaybackPositionUsable;

    internal static bool CanApplySync(
        bool isPlayerReady,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking,
        bool shouldSuppressSync) =>
        isPlayerReady && isMonitoring && syncEnabled && !isSeeking && !shouldSuppressSync;

    internal static bool CanReapplyLastAccepted(
        bool hasAcceptedFrame,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking,
        bool shouldSuppressSync) =>
        hasAcceptedFrame && isMonitoring && syncEnabled && !isSeeking && !shouldSuppressSync;

    internal static bool CanReapplyAfterFileLoadRelease(
        bool isPlayerReady,
        bool isMonitoring,
        bool syncEnabled,
        bool isSeeking) =>
        isPlayerReady && isMonitoring && syncEnabled && !isSeeking;

    internal static bool ShouldSkipHeldLanding(bool isBoundaryHeld) => isBoundaryHeld;

    internal static bool CanPauseForSignalLoss(
        LtcSignalLossMode mode,
        bool syncEnabled,
        bool isGapActive,
        bool isPlaybackPaused,
        bool pausedByPolicy,
        bool manualResumeSuppressesPause) =>
        mode == LtcSignalLossMode.Stop && syncEnabled && !isGapActive &&
        !isPlaybackPaused && !pausedByPolicy && !manualResumeSuppressesPause;

    internal static bool CanResumeAfterSignalLoss(
        bool syncEnabled,
        bool isMonitoring,
        bool isGapActive) =>
        syncEnabled && isMonitoring && !isGapActive;

    internal static bool ShouldResumeOnBoundaryHoldRelease(PauseOwners otherOwners) =>
        otherOwners == PauseOwners.None;

    internal static PauseOwners CollectOtherPauseOwners(
        bool isSignalLossPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused) =>
        (isSignalLossPauseOwned ? PauseOwners.SignalLoss : PauseOwners.None) |
        (isGapPauseOwned ? PauseOwners.Gap : PauseOwners.None) |
        (isProjectRestorePaused ? PauseOwners.ProjectRestore : PauseOwners.None);
}
