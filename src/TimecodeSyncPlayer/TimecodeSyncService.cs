namespace TimecodeSyncPlayer;

public sealed class TimecodeSyncService
{
    private readonly ISyncDecisionEngine _engine;
    private readonly ITimecodeSyncSeekState _seekState;
    private readonly TimeProvider _timeProvider;
    private readonly SeekLatencyCompensator _latencyCompensator;

    private DateTime _lastSyncSeekAt = DateTime.MinValue;
    private volatile bool _isLoadingFile;
    // D27-b: ロード解除を、解除を起こした呼び出しと別の呼び出し（保持 LTC の再適用）でも
    // ちょうど 1 回だけ回収できるようにする。解除が起きたら立て、回収したら下ろす。
    private bool _fileLoadReleasePending;
    private DateTime _fileLoadStartedAt = DateTime.MinValue;
    private double _fileLoadStartPositionSeconds;
    private long _fileLoadStartedRenderedFrames;
    private SyncActionType _lastLoggedSyncAction = SyncActionType.None;
    private bool _lastLoggedDefaultVideoFps;
    private bool _lastLoggedDefaultTimecodeFps;

    private const double SeekDebounceMs = 250.0;
    private const double FileLoadPlaybackProgressSeconds = 0.08;
    private const long FileLoadRenderedFrameProgress = 2;
    private static readonly TimeSpan FileLoadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// T9: 粗い同期シークを発行した時点の通知（<see cref="ReportSeekSent"/> と同じ）。
    /// LtcSyncController が着地直後の Smooth 速度上限の窓を開く。Jump の補正シークも
    /// このメソッドを通るが、窓は Smooth だけが参照する。
    /// </summary>
    internal event Action? SeekIssued;

    public TimecodeSyncService(
        ISyncDecisionEngine engine,
        ITimecodeSyncSeekState seekState,
        TimeProvider? timeProvider = null)
        : this(engine, seekState, timeProvider, null)
    {
    }

    internal TimecodeSyncService(
        ISyncDecisionEngine engine,
        ITimecodeSyncSeekState seekState,
        TimeProvider? timeProvider,
        SeekLatencyCompensator? latencyCompensator)
    {
        _engine = engine;
        _seekState = seekState;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _latencyCompensator = latencyCompensator ?? new SeekLatencyCompensator();
    }

    public SyncDecision EvaluateDecision(double ltcSeconds, SyncPlaybackState state)
    {
        SyncDecision decision = _engine.Decide(ltcSeconds, state);
        LogDecisionIfNeeded(decision, ltcSeconds, state.PlaybackSeconds);
        return decision;
    }

    public bool IsLoadingFile => _isLoadingFile;

    /// <summary>D27-b: 回収待ちのロード解除があるか。</summary>
    public bool HasPendingFileLoadRelease => _fileLoadReleasePending;

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds,
        double requestedTargetSeconds = double.NaN)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;

        if (_isLoadingFile)
        {
            if (now - _fileLoadStartedAt > FileLoadTimeout)
            {
                _isLoadingFile = false;    // 安全タイムアウト
                _lastSyncSeekAt = now;    // タイムアウト後もデバウンスを保護
            }
            else
                return true;               // ロード中は全シーク抑止
        }

        bool suppress = _seekState.ShouldSuppressSeek(playbackSeconds, toleranceSeconds, now,
            requestedTargetSeconds);

        if (_seekState.LastStatus is TimecodeSyncSeekPendingStatus.Settled or TimecodeSyncSeekPendingStatus.TimedOut)
        {
            Serilog.Log.Information(
                "Timecode sync pending {Status} playback={Playback:F3} tolerance={Tolerance:F4}",
                _seekState.LastStatus, playbackSeconds, toleranceSeconds);
        }

        return suppress;
    }

    public bool IsDebounced()
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        return (now - _lastSyncSeekAt).TotalMilliseconds < SeekDebounceMs;
    }

    public void ReportSeekSent(double targetSeconds)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _lastSyncSeekAt = now;
        _latencyCompensator.MarkSeekSent();
        _seekState.BeginSeek(targetSeconds, now);
        SeekIssued?.Invoke();
    }

    /// <summary>
    /// LoadFile 発行時に呼ぶ。シーク状態をクリアしロード中フラグを立てる。
    /// リファクタリング前の LoadFile 時 Clear() 動作を復元する。
    /// あわせて先行補償の着地測定を arm する（issuedQpc は LoadFile 発行直前の QPC、0 は現在時刻）。
    /// </summary>
    public void BeginFileLoad(double startPositionSeconds, long renderedFrameCount)
        => BeginFileLoad(startPositionSeconds, renderedFrameCount, loadIssuedQpc: 0);

    /// <summary>
    /// LoadFile 発行時に呼ぶ。loadIssuedQpc は LoadFile を発行した QPC（計測開始点）。
    /// </summary>
    internal void BeginFileLoad(double startPositionSeconds, long renderedFrameCount, long loadIssuedQpc)
    {
        _latencyCompensator.MarkLoadSent(loadIssuedQpc);
        _isLoadingFile = true;
        _fileLoadReleasePending = false;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _fileLoadStartedAt = now;
        _fileLoadStartPositionSeconds = Math.Max(0, startPositionSeconds);
        _fileLoadStartedRenderedFrames = Math.Max(0, renderedFrameCount);
        _lastSyncSeekAt = now;                // デバウンスを更新（2.3 fix）
        _seekState.Clear();                    // 古い保留シーク状態をクリア（2.1 fix）
    }

    /// <summary>
    /// HandleOnTrackSync で再生位置と描画フレームが進んだらロード状態を解除する。
    /// </summary>
    public bool TryMarkFileLoaded(double playbackSeconds, long renderedFrameCount)
    {
        if (!_isLoadingFile) return true;
        if (!double.IsFinite(playbackSeconds) || playbackSeconds < 0)
            return false;
        if (_timeProvider.GetUtcNow().UtcDateTime - _fileLoadStartedAt > FileLoadTimeout)
        {
            _isLoadingFile = false;
            _fileLoadReleasePending = true;
            _lastSyncSeekAt = _timeProvider.GetUtcNow().UtcDateTime;
            return true;
        }

        double playbackProgress = playbackSeconds - _fileLoadStartPositionSeconds;
        long renderedFrameProgress = renderedFrameCount - _fileLoadStartedRenderedFrames;
        if (playbackProgress < FileLoadPlaybackProgressSeconds ||
            renderedFrameProgress < FileLoadRenderedFrameProgress)
        {
            return false;
        }

        _isLoadingFile = false;
        _fileLoadReleasePending = true;
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        _lastSyncSeekAt = now;                // ロード後デバウンスを再スタート
        return true;
    }

    public void ClearSeekState()
    {
        _seekState.Clear();
    }

    /// <summary>
    /// D20-b: 保持 LTC（Duplicate）では通常の同期経路（ApplySync）が走らないため、
    /// ロード解除だけをここで観測できるようにする。解除された回だけ true を返す。
    /// D27-b: 解除が同期コーディネーター側の完了（TryMarkFileLoaded）で先に起きた場合も、
    /// 未回収の解除を 1 回だけ返す（保持 LTC の値の再適用を取りこぼさない）。
    /// </summary>
    public bool PollFileLoadRelease(double playbackSeconds, long renderedFrameCount)
    {
        if (_isLoadingFile && TryMarkFileLoaded(playbackSeconds, renderedFrameCount))
        {
            // この呼び出しが解除を回収する。未回収フラグは残さない。
            _fileLoadReleasePending = false;
            return true;
        }

        if (_fileLoadReleasePending)
        {
            _fileLoadReleasePending = false;
            return true;
        }

        return false;
    }

    public ITimecodeSyncSeekState SeekState => _seekState;

    /// <summary>先行補償の学習状態（トラックの引き当てとフレーム Ready 通知に使う）。</summary>
    internal SeekLatencyCompensator LatencyCompensator => _latencyCompensator;

    private void LogDecisionIfNeeded(SyncDecision decision, double ltcSeconds, double playbackSeconds)
    {
        bool shouldLog =
            decision.Action != _lastLoggedSyncAction ||
            decision.UsedDefaultVideoFps != _lastLoggedDefaultVideoFps ||
            decision.UsedDefaultTimecodeFps != _lastLoggedDefaultTimecodeFps;

        if (!shouldLog)
            return;

        _lastLoggedSyncAction = decision.Action;
        _lastLoggedDefaultVideoFps = decision.UsedDefaultVideoFps;
        _lastLoggedDefaultTimecodeFps = decision.UsedDefaultTimecodeFps;

        Serilog.Log.Information(
            "Timecode sync decision action={Action} ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} videoFps={VideoFps:F3} timecodeFps={TimecodeFps:F3} defaultVideoFps={DefaultVideoFps} defaultTimecodeFps={DefaultTimecodeFps}",
            decision.Action, ltcSeconds, playbackSeconds, decision.TargetSeconds,
            decision.DeltaSeconds, decision.ToleranceSeconds, decision.VideoFpsUsed,
            decision.TimecodeFpsUsed, decision.UsedDefaultVideoFps,
            decision.UsedDefaultTimecodeFps);
    }
}
