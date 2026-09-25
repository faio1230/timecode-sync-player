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
}
