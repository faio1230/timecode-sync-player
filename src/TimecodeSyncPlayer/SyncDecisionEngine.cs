using System.Diagnostics;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

internal sealed class SyncDecisionEngine : ISyncDecisionEngine
{
    private readonly SyncDecisionOptions _options;
    private readonly SeekLatencyCompensator? _latencyCompensator;

    public SyncDecisionEngine()
        : this(new SyncDecisionOptions(), null)
    {
    }

    public SyncDecisionEngine(SyncDecisionOptions options)
        : this(options, null)
    {
    }

    public SyncDecisionEngine(SyncDecisionOptions options, SeekLatencyCompensator? latencyCompensator)
    {
        _options = options;
        _latencyCompensator = latencyCompensator;
    }

    public SyncDecision Decide(double ltcSeconds, SyncPlaybackState state)
    {
        SyncFpsResolution fps = ResolveFps(state.VideoFps, state.TimecodeFps);
        double toleranceSeconds = ToleranceSeconds(fps.VideoFps, fps.TimecodeFps, _options.ToleranceFrames);
        // 計測専用（出力トレース有効時のみ）。既定経路では読み取り 1 回だけで、文字列は作らない。
        bool traceEnabled = OutputTrace.Current.IsEnabled;

        if (!state.SyncEnabled || !state.HasCurrentTrack || state.IsSeeking)
        {
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, RawDelta(ltcSeconds, state),
                    !state.SyncEnabled ? "disabled" : !state.HasCurrentTrack ? "no-track" : "seeking");
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        }
        if (!IsFinite(ltcSeconds) || !IsFinite(state.PlaybackSeconds))
        {
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, null, "not-finite");
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        }
        if (!SeekBarUpdateState.IsUsableDuration(state.DurationSeconds))
        {
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, RawDelta(ltcSeconds, state), "bad-duration");
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        }

        // D29: Single の LTC → 素材位置はトラックの範囲に収める。MediaOut 未設定は尺、
        // MediaIn 未設定は 0。Continue はタイムライン写像（FindTrackAtTimelinePosition）で
        // 既に範囲内のため、ここでは no-op になる。
        (double clipIn, double clipOut) = ClipRange(
            state.MediaInSeconds, state.MediaOutSeconds, state.DurationSeconds);
        double target = Math.Clamp(ltcSeconds, clipIn, clipOut);
        double delta = target - state.PlaybackSeconds;
        if (Math.Abs(delta) <= toleranceSeconds)
        {
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, delta, "within-tolerance");
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        }

        // 行き先だけを先行補償する。シーク可否（delta と tolerance）は補償前の値で判定する。
        // 補償後もトラックの範囲（D29）へ収める。
        double compensatedTarget = _latencyCompensator is null
            ? target
            : Math.Clamp(
                _latencyCompensator.CompensateTarget(ltcSeconds, state.DurationSeconds),
                clipIn, clipOut);
        long decideQpc = traceEnabled || _latencyCompensator != null ? Stopwatch.GetTimestamp() : 0;
        _latencyCompensator?.MarkSeekDecision(decideQpc);

        // 計測専用（出力トレース有効時のみ）。シークを決めた時刻 a を同じ QPC で残す。
        // value は seek.issue と同じ補償後のターゲットに揃える（解析側の decide/issue 対応付けを維持）。
        if (traceEnabled)
        {
            RecordEvaluate(ltcSeconds, state, toleranceSeconds, delta, "seek");
            OutputTrace.Current.Record(new("seek.decide", "SYNC", decideQpc,
                Value: (long)Math.Round(compensatedTarget * 1_000_000.0),
                Detail: FormattableString.Invariant(
                    $"delta={delta:F6} ltc={ltcSeconds:F6} playback={state.PlaybackSeconds:F6} tolerance={toleranceSeconds:F6} compensation={_latencyCompensator?.CompensationSeconds ?? 0.0:F6}")));
        }

        return new SyncDecision(
            SyncActionType.Seek,
            compensatedTarget,
            delta,
            toleranceSeconds,
            fps.VideoFps,
            fps.TimecodeFps,
            fps.UsedDefaultVideoFps,
            fps.UsedDefaultTimecodeFps);
    }

    // 早期 return では判定用 delta（target でクランプ後）を計算していないため、生の差を記録する（有限のときだけ）。
    private static double? RawDelta(double ltcSeconds, SyncPlaybackState state) =>
        IsFinite(ltcSeconds) && IsFinite(state.PlaybackSeconds) ? ltcSeconds - state.PlaybackSeconds : null;

    // 計測専用（呼び出し側で IsEnabled を確認済み）。reason は None を返した理由を 1 語で残す。
    private static void RecordEvaluate(double ltcSeconds, SyncPlaybackState state, double toleranceSeconds,
        double? delta, string reason)
    {
        OutputTrace.Current.Record(new("sync.evaluate", "SYNC", Stopwatch.GetTimestamp(),
            Value: ToMicroseconds(ltcSeconds),
            Detail: FormattableString.Invariant(
                $"playback={state.PlaybackSeconds:F6} delta={delta ?? double.NaN:F6} tolerance={toleranceSeconds:F6} syncEnabled={state.SyncEnabled} hasTrack={state.HasCurrentTrack} isSeeking={state.IsSeeking} reason={reason}")));
    }

    private static long ToMicroseconds(double seconds) =>
        IsFinite(seconds) ? (long)Math.Round(seconds * 1_000_000.0) : 0;

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

    /// <summary>
    /// D29/D33: Single の LTC → 素材位置の範囲。MediaIn 未設定（0 以下）は 0、
    /// MediaOut 未設定（null）は尺。範囲が逆転していれば In に合わせる。
    /// </summary>
    internal static (double In, double Out) ClipRange(
        double mediaInSeconds, double? mediaOutSeconds, double durationSeconds)
    {
        double clipIn = IsFinite(mediaInSeconds) && mediaInSeconds > 0.0 ? mediaInSeconds : 0.0;
        double clipOut = mediaOutSeconds is { } mediaOut && IsFinite(mediaOut)
            ? mediaOut : durationSeconds;
        if (clipOut < clipIn)
            clipOut = clipIn;
        return (clipIn, clipOut);
    }

    /// <summary>D33: 素材位置を [MediaIn, MediaOut ?? 尺] に収める（範囲内なら no-op）。</summary>
    internal static double ClampToClip(
        double seconds, double mediaInSeconds, double? mediaOutSeconds, double durationSeconds)
    {
        (double clipIn, double clipOut) = ClipRange(mediaInSeconds, mediaOutSeconds, durationSeconds);
        return Math.Clamp(seconds, clipIn, clipOut);
    }

    /// <summary>
    /// D20-b: 一致判定の許容秒。Decide と同じ規則（動画/タイムコード fps の大きいフレーム幅 ×
    /// ToleranceFrames、fps 不明は 30）を、保持 LTC の再適用判定と共有する。
    /// </summary>
    internal static double ToleranceSeconds(double videoFps, double timecodeFps, double toleranceFrames)
    {
        double resolvedVideoFps = IsUsableFps(videoFps) ? videoFps : 30.0;
        double resolvedTimecodeFps = IsUsableFps(timecodeFps) ? timecodeFps : 30.0;
        return Math.Max(1.0 / resolvedVideoFps, 1.0 / resolvedTimecodeFps) * toleranceFrames;
    }

    internal static double ToleranceSeconds(double videoFps, double timecodeFps) =>
        ToleranceSeconds(videoFps, timecodeFps, new SyncDecisionOptions().ToleranceFrames);
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
    double TimecodeFps = 0.0,
    // D29: Single の LTC → 素材位置の範囲。MediaIn はクリップ開始（既定 0）、
    // MediaOut はクリップ終端（未設定 = null で尺を使う）。
    double MediaInSeconds = 0.0,
    double? MediaOutSeconds = null);

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
