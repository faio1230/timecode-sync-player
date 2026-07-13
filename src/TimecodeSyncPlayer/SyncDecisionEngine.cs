namespace TimecodeSyncPlayer;

internal sealed class SyncDecisionEngine : ISyncDecisionEngine
{
    private readonly SyncDecisionOptions _options;

    public SyncDecisionEngine()
        : this(new SyncDecisionOptions())
    {
    }

    public SyncDecisionEngine(SyncDecisionOptions options)
    {
        _options = options;
    }

    public SyncDecision Decide(double ltcSeconds, SyncPlaybackState state)
    {
        SyncFpsResolution fps = ResolveFps(state.VideoFps, state.TimecodeFps);
        double toleranceSeconds = Math.Max(fps.VideoFrameSeconds, fps.TimecodeFrameSeconds) * _options.ToleranceFrames;

        if (!state.SyncEnabled || !state.HasCurrentTrack || state.IsSeeking)
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        if (!IsFinite(ltcSeconds) || !IsFinite(state.PlaybackSeconds))
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        if (!SeekBarUpdateState.IsUsableDuration(state.DurationSeconds))
            return SyncDecision.NoneWith(fps, toleranceSeconds);

        double target = Math.Clamp(ltcSeconds, 0.0, state.DurationSeconds);
        double delta = target - state.PlaybackSeconds;
        if (Math.Abs(delta) <= toleranceSeconds)
            return SyncDecision.NoneWith(fps, toleranceSeconds);

        return new SyncDecision(
            SyncActionType.Seek,
            target,
            delta,
            toleranceSeconds,
            fps.VideoFps,
            fps.TimecodeFps,
            fps.UsedDefaultVideoFps,
            fps.UsedDefaultTimecodeFps);
    }

    private SyncFpsResolution ResolveFps(double videoFps, double timecodeFps)
    {
        bool usedDefaultVideoFps = !IsUsableFps(videoFps);
        bool usedDefaultTimecodeFps = !IsUsableFps(timecodeFps);

        double resolvedVideoFps = usedDefaultVideoFps ? _options.DefaultVideoFps : videoFps;
        double resolvedTimecodeFps = usedDefaultTimecodeFps ? _options.DefaultTimecodeFps : timecodeFps;

        if (!IsUsableFps(resolvedVideoFps))
            resolvedVideoFps = 30.0;
        if (!IsUsableFps(resolvedTimecodeFps))
            resolvedTimecodeFps = 30.0;

        return new SyncFpsResolution(
            resolvedVideoFps,
            resolvedTimecodeFps,
            usedDefaultVideoFps,
            usedDefaultTimecodeFps);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static bool IsUsableFps(double fps) =>
        IsFinite(fps) && fps > 0;
}

public sealed record SyncDecisionOptions(
    double ToleranceFrames = 6.0,
    double DefaultVideoFps = 30.0,
    double DefaultTimecodeFps = 30.0);

public sealed record SyncPlaybackState(
    bool SyncEnabled,
    bool HasCurrentTrack,
    bool IsSeeking,
    double PlaybackSeconds,
    double DurationSeconds,
    double VideoFps = 0.0,
    double TimecodeFps = 0.0);

public enum SyncActionType
{
    None,
    Seek
}

public sealed record SyncDecision(
    SyncActionType Action,
    double TargetSeconds,
    double DeltaSeconds,
    double ToleranceSeconds,
    double VideoFpsUsed,
    double TimecodeFpsUsed,
    bool UsedDefaultVideoFps,
    bool UsedDefaultTimecodeFps)
{
    public static SyncDecision None { get; } = new(
        SyncActionType.None,
        0.0,
        0.0,
        0.0,
        0.0,
        0.0,
        false,
        false);

    public static SyncDecision NoneWith(SyncFpsResolution fps, double toleranceSeconds) => new(
        SyncActionType.None,
        0.0,
        0.0,
        toleranceSeconds,
        fps.VideoFps,
        fps.TimecodeFps,
        fps.UsedDefaultVideoFps,
        fps.UsedDefaultTimecodeFps);
}

public sealed record SyncFpsResolution(
    double VideoFps,
    double TimecodeFps,
    bool UsedDefaultVideoFps,
    bool UsedDefaultTimecodeFps)
{
    public double VideoFrameSeconds => 1.0 / VideoFps;
    public double TimecodeFrameSeconds => 1.0 / TimecodeFps;
}
