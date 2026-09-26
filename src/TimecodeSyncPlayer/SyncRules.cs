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

    /// <summary>
    /// v0.5.3 段 3i: 信号断のポリシーが自分の一時停止を解いた（ランスルーへ変更）とき、
    /// ほかの持ち主がいなければ再生を再開してよい（§6 の 9。境界ホールドの解除と同じ判定）。
    /// </summary>
    internal static bool ShouldResumeOnPolicyPauseRelease(PauseOwners otherOwners) =>
        otherOwners == PauseOwners.None;

    /// <summary>
    /// v0.5.3 段 3i: 信号断のポリシー以外の一時停止の持ち主（境界ホールド・ギャップ・
    /// プロジェクト復元）を組み立てる。MainWindow とハーネスが同じ関数を使う。
    /// </summary>
    internal static PauseOwners CollectPauseOwnersExceptSignalLoss(
        bool isBoundaryHoldPauseOwned,
        bool isGapPauseOwned,
        bool isProjectRestorePaused) =>
        (isBoundaryHoldPauseOwned ? PauseOwners.BoundaryHold : PauseOwners.None) |
        (isGapPauseOwned ? PauseOwners.Gap : PauseOwners.None) |
        (isProjectRestorePaused ? PauseOwners.ProjectRestore : PauseOwners.None);
}
