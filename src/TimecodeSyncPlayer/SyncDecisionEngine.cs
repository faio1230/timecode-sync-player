using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

internal sealed class SyncDecisionEngine : ISyncDecisionEngine
{
    private readonly SyncDecisionOptions _options;
    private readonly SeekLatencyCompensator? _latencyCompensator;
    private readonly Func<double> _clockSeconds;
    // D37-a: 瞬間値の跳ねで粗いシークを出さないためのゲート。位置が飛ぶ操作の後は Reset する。
    private readonly SeekDecisionGate _seekGate = new();
    private bool _rejectedSampleLogged;
    private bool _gatedSeekLogged;
    // 起動後の最初の 1 サンプルだけは履歴が無い。追従開始の大きなずれに即応するため、
    // この 1 回だけ瞬間値で判定する（ResetSeekGate では戻さない。シーク後・ロード後まで
    // 例外を広げると、位置が飛んだ直後の 1 サンプルで連鎖が始まる）。
    private bool _gateWarmed;

    public SyncDecisionEngine()
        : this(new SyncDecisionOptions(), null)
    {
    }

    public SyncDecisionEngine(SyncDecisionOptions options)
        : this(options, null)
    {
    }

    public SyncDecisionEngine(SyncDecisionOptions options, SeekLatencyCompensator? latencyCompensator)
        : this(options, latencyCompensator, null)
    {
    }

    /// <summary>時計を差し替えられるのは単体テスト用（既定は QPC 秒）。</summary>
    internal SyncDecisionEngine(
        SyncDecisionOptions options,
        SeekLatencyCompensator? latencyCompensator,
        Func<double>? clockSeconds)
    {
        _options = options;
        _latencyCompensator = latencyCompensator;
        _clockSeconds = clockSeconds ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    }

    /// <summary>
    /// D37-a: ゲートの系列を切る。シークの発行・ロード・手動移動の後に呼ぶ。
    /// </summary>
    public void ResetSeekGate()
    {
        _seekGate.Reset();
        _rejectedSampleLogged = false;
        _gatedSeekLogged = false;
    }

    public SyncDecision Decide(double ltcSeconds, SyncPlaybackState state)
    {
        SyncFpsResolution fps = ResolveFps(state.VideoFps, state.TimecodeFps);
        double toleranceSeconds = ToleranceSeconds(fps.VideoFps, fps.TimecodeFps, _options.ToleranceFrames);
        // 計測専用（出力トレース有効時のみ）。既定経路では読み取り 1 回だけで、文字列は作らない。
        bool traceEnabled = OutputTrace.Current.IsEnabled;

        if (!state.SyncEnabled || !state.HasCurrentTrack || state.IsSeeking)
        {
            // D37-a: 手動移動中は位置が飛ぶため、ゲートの系列を切る（離した後の 1 サンプル目から
            // 新しい位置で測り直す）。
            if (state.IsSeeking)
                ResetSeekGate();
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

        // D37-a: 瞬間値では Seek を出さない。直近の窓の中央値（または許容超えの連続）が
        // 条件を満たすまで待ち、物理的にありえない変化のサンプルは測定の乱れとして弾く。
        double ltcGranularitySeconds = 1.0 / fps.TimecodeFps;
        SeekDecisionGate.Result gate = _seekGate.Observe(
            delta, toleranceSeconds, _clockSeconds(), ltcGranularitySeconds);
        if (gate.Rejected)
        {
            LogRejectedSample(gate);
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, delta, "unstable", GateDetail(gate));
            return SyncDecision.NoneWith(fps, toleranceSeconds, gateDeferred: true);
        }
        _rejectedSampleLogged = false;
        bool gateWasCold = !_gateWarmed;
        _gateWarmed = true;

        if (Math.Abs(delta) <= toleranceSeconds)
        {
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, delta, "within-tolerance");
            return SyncDecision.NoneWith(fps, toleranceSeconds);
        }
        if (!gate.ShouldSeek && !gateWasCold)
        {
            LogGatedSeek(gate, toleranceSeconds);
            if (traceEnabled)
                RecordEvaluate(ltcSeconds, state, toleranceSeconds, delta, "seek-gated", GateDetail(gate));
            return SyncDecision.NoneWith(fps, toleranceSeconds, gateDeferred: true);
        }
        _gatedSeekLogged = false;

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
        double? delta, string reason, string? gateDetail = null)
    {
        string detail = FormattableString.Invariant(
            $"playback={state.PlaybackSeconds:F6} delta={delta ?? double.NaN:F6} tolerance={toleranceSeconds:F6} syncEnabled={state.SyncEnabled} hasTrack={state.HasCurrentTrack} isSeeking={state.IsSeeking} reason={reason}");
        if (!string.IsNullOrEmpty(gateDetail))
            detail = detail + " " + gateDetail;
        OutputTrace.Current.Record(new("sync.evaluate", "SYNC", Stopwatch.GetTimestamp(),
            Value: ToMicroseconds(ltcSeconds),
            Detail: detail));
    }

    private static string GateDetail(SeekDecisionGate.Result gate) =>
        FormattableString.Invariant(
            $"gate_samples={gate.Samples} gate_median={gate.MedianSeconds:F6} gate_consecutive={gate.ConsecutiveExceeded} gate_rejected={gate.RejectedTotal}");

    /// <summary>D37-a: ありえない跳ねを弾いたことを、乱れの切れ目に 1 回だけ残す。</summary>
    private void LogRejectedSample(SeekDecisionGate.Result gate)
    {
        if (_rejectedSampleLogged)
            return;
        _rejectedSampleLogged = true;
        Log.Information(
            "Seek decision gate: rejected unstable sample deltaMs={DeltaMs:F1} previousMs={PreviousMs:F1} changeMs={ChangeMs:F1} allowedMs={AllowedMs:F1} dtMs={DtMs:F1} rejectedTotal={RejectedTotal}",
            gate.DeltaSeconds * 1000.0, gate.PreviousDeltaSeconds * 1000.0, gate.ChangeSeconds * 1000.0,
            gate.AllowedChangeSeconds * 1000.0, gate.DtSeconds * 1000.0, gate.RejectedTotal);
    }

    /// <summary>D37-a: 瞬間値では超えているがゲートが抑えたことを、抑えの切れ目に 1 回だけ残す。</summary>
    private void LogGatedSeek(SeekDecisionGate.Result gate, double toleranceSeconds)
    {
        if (_gatedSeekLogged)
            return;
        _gatedSeekLogged = true;
        Log.Information(
            "Timecode sync seek gated medianMs={MedianMs:F1} consecutive={Consecutive} samples={Samples} rejectedTotal={RejectedTotal} deltaMs={DeltaMs:F1} toleranceMs={ToleranceMs:F1}",
            gate.MedianSeconds * 1000.0, gate.ConsecutiveExceeded, gate.Samples, gate.RejectedTotal,
            gate.DeltaSeconds * 1000.0, toleranceSeconds * 1000.0);
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
    bool UsedDefaultTimecodeFps,
    // D37-a: ゲートが Seek を保留した（瞬間値では許容を超えるが、窓が埋まるまで待つ）。
    // Action は None のままだが、呼び出し側は要求を Deferred のまま維持し、次の評価で再試行する。
    bool GateDeferred = false)
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

    public static SyncDecision NoneWith(SyncFpsResolution fps, double toleranceSeconds,
        bool gateDeferred = false) => new(
        SyncActionType.None,
        0.0,
        0.0,
        toleranceSeconds,
        fps.VideoFps,
        fps.TimecodeFps,
        fps.UsedDefaultVideoFps,
        fps.UsedDefaultTimecodeFps,
        gateDeferred);
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
