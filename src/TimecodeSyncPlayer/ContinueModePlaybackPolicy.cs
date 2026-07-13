namespace TimecodeSyncPlayer;

internal enum ContinueModeEndAdvanceAction
{
    None,
    LoadNextTrack,
    EnterNoTracks
}

internal static class ContinueModePlaybackPolicy
{
    public static bool ShouldAutoAdvanceAtMediaEnd(SyncMode syncMode, bool timecodeSyncEnabled) =>
        syncMode != SyncMode.Continue || !timecodeSyncEnabled;

    public static ContinueModeEndAdvanceAction DecideEndAdvanceAction(
        double positionSeconds,
        double effectiveDurationSeconds,
        bool hasNextEnabledTrack,
        bool alreadyTriggered,
        bool isPaused,
        bool isSeeking,
        double thresholdSeconds)
    {
        if (isPaused || isSeeking || alreadyTriggered)
            return ContinueModeEndAdvanceAction.None;

        if (!double.IsFinite(positionSeconds) ||
            !double.IsFinite(effectiveDurationSeconds) ||
            effectiveDurationSeconds <= 0 ||
            thresholdSeconds < 0)
        {
            return ContinueModeEndAdvanceAction.None;
        }

        if (positionSeconds < effectiveDurationSeconds - thresholdSeconds)
            return ContinueModeEndAdvanceAction.None;

        return hasNextEnabledTrack
            ? ContinueModeEndAdvanceAction.LoadNextTrack
            : ContinueModeEndAdvanceAction.EnterNoTracks;
    }

    public static bool ShouldLoadPreviousTrackForGapFreeze(Guid? loadedTrackId, Guid? previousTrackId) =>
        previousTrackId.HasValue &&
        (!loadedTrackId.HasValue || loadedTrackId.Value != previousTrackId.Value);

    public static bool ShouldRenderBlackForGapFreeze(Guid? previousTrackId) =>
        !previousTrackId.HasValue;

    public static bool ShouldRenderBlackWhileGapActive(GapBehavior gapBehavior, bool forceBlackFrame) =>
        gapBehavior == GapBehavior.Black || forceBlackFrame;

    public static bool ShouldCaptureFreezeFrameAfterFrameStep(
        bool timePosReadSucceeded,
        double actualPositionSeconds,
        double targetSeconds,
        double frameSeconds)
    {
        if (!timePosReadSucceeded)
            return true;

        if (!double.IsFinite(actualPositionSeconds) ||
            !double.IsFinite(targetSeconds) ||
            !double.IsFinite(frameSeconds) ||
            frameSeconds <= 0)
        {
            return true;
        }

        return actualPositionSeconds >= targetSeconds - frameSeconds * 2.0;
    }

    public static bool ShouldCopyRenderedFrameToFreezeBuffer(GapState gapState) =>
        gapState is GapState.EnteringFreeze or GapState.WaitingForFrameStep;

    public static bool CanReuseFrozenFrame(
        Guid? cachedTrackId,
        double cachedTargetSeconds,
        Guid? requestedTrackId,
        double requestedTargetSeconds,
        double toleranceSeconds = 1.0 / 24.0) =>
        cachedTrackId.HasValue &&
        requestedTrackId.HasValue &&
        cachedTrackId.Value == requestedTrackId.Value &&
        Math.Abs(cachedTargetSeconds - requestedTargetSeconds) <= toleranceSeconds;

    public static bool IsExpectedMediaPath(string currentPath, string expectedPath)
    {
        static string Normalize(string path) =>
            path.Replace('\\', '/').Trim().Trim('"');

        return string.Equals(
            Normalize(currentPath),
            Normalize(expectedPath),
            StringComparison.OrdinalIgnoreCase);
    }
}
