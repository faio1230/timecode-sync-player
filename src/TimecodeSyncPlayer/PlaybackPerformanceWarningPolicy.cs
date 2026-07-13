namespace TimecodeSyncPlayer;

internal static class PlaybackPerformanceWarningPolicy
{
    private const double DisplayedFpsWarningThreshold = 0.85;
    private const double PlaybackRateSlowThreshold = 0.95;

    public static bool ShouldWarnDisplayedFps(PlaybackPerformanceSnapshot snapshot, double expectedFps) =>
        expectedFps > 0
        && snapshot.RenderedFrames > 0
        && snapshot.DisplayedFps < expectedFps * DisplayedFpsWarningThreshold;

    public static bool ShouldWarnPlaybackRate(PlaybackPerformanceSnapshot snapshot) =>
        snapshot.PlaybackRate is > 0 and < PlaybackRateSlowThreshold;
}
