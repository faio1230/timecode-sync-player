namespace TimecodeSyncPlayer.Strategies;

internal static class SyncDecisionEngineExtensions
{
    public static SyncDecision DecideSeekTarget(this SyncDecisionEngine engine, double playbackSeconds, double ltcSeconds, double fps, TimecodeSyncSeekState seekState)
    {
        var state = new SyncPlaybackState(
            SyncEnabled: true,
            HasCurrentTrack: true,
            IsSeeking: seekState.HasPendingSeek,
            PlaybackSeconds: playbackSeconds,
            // Duration is unknown for seek-target decisions; using MaxValue ensures all LTC positions are valid targets.
            // This is safe because the Decider checks IsUsableDuration (MaxValue is usable) and Clamp(target, 0, MaxValue)
            // effectively acts as Max(target, 0), which is the desired behavior.
            DurationSeconds: double.MaxValue,
            VideoFps: fps,
            TimecodeFps: fps);

        return engine.Decide(ltcSeconds, state);
    }
}
