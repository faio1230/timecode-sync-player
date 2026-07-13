namespace TimecodeSyncPlayer;

internal static class PlaybackTimelinePositionPolicy
{
    public static double? GetGapTimelinePosition(
        bool isGapInactive,
        SyncMode syncMode,
        double lastLtcSeconds)
    {
        if (!isGapInactive && syncMode == SyncMode.Continue && lastLtcSeconds > 0)
            return lastLtcSeconds;

        return null;
    }

    public static double GetNormalTimelinePosition(
        SyncMode syncMode,
        bool syncEnabled,
        double lastLtcSeconds,
        double playbackSeconds)
    {
        if (syncMode == SyncMode.Continue && syncEnabled && lastLtcSeconds > 0)
            return lastLtcSeconds;

        return playbackSeconds;
    }
}
