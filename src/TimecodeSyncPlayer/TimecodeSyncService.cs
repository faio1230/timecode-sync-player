namespace TimecodeSyncPlayer;

public sealed class TimecodeSyncService
{
    private readonly ISyncDecisionEngine _engine;
    private readonly ITimecodeSyncSeekState _seekState;

    private DateTime _lastSyncSeekAt = DateTime.MinValue;
    private volatile bool _isLoadingFile;
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

    public TimecodeSyncService(ISyncDecisionEngine engine, ITimecodeSyncSeekState seekState)
    {
        _engine = engine;
        _seekState = seekState;
    }

    public SyncDecision EvaluateDecision(double ltcSeconds, SyncPlaybackState state)
    {
        SyncDecision decision = _engine.Decide(ltcSeconds, state);
        LogDecisionIfNeeded(decision, ltcSeconds, state.PlaybackSeconds);
        return decision;
    }

    public bool IsLoadingFile => _isLoadingFile;

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds)
    {
        DateTime now = DateTime.UtcNow;

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

        bool suppress = _seekState.ShouldSuppressSeek(playbackSeconds, toleranceSeconds, now);

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
        DateTime now = DateTime.UtcNow;
        return (now - _lastSyncSeekAt).TotalMilliseconds < SeekDebounceMs;
    }

    public void ReportSeekSent(double targetSeconds)
    {
        DateTime now = DateTime.UtcNow;
        _lastSyncSeekAt = now;
        _seekState.BeginSeek(targetSeconds, now);
    }

    /// <summary>
    /// LoadFile 発行時に呼ぶ。シーク状態をクリアしロード中フラグを立てる。
    /// リファクタリング前の LoadFile 時 Clear() 動作を復元する。
    /// </summary>
    public void BeginFileLoad(double startPositionSeconds, long renderedFrameCount)
    {
        _isLoadingFile = true;
        DateTime now = DateTime.UtcNow;
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

        double playbackProgress = playbackSeconds - _fileLoadStartPositionSeconds;
        long renderedFrameProgress = renderedFrameCount - _fileLoadStartedRenderedFrames;
        if (playbackProgress < FileLoadPlaybackProgressSeconds ||
            renderedFrameProgress < FileLoadRenderedFrameProgress)
        {
            return false;
        }

        _isLoadingFile = false;
        DateTime now = DateTime.UtcNow;
        _lastSyncSeekAt = now;                // ロード後デバウンスを再スタート
        return true;
    }

    public void ClearSeekState()
    {
        _seekState.Clear();
    }

    public ITimecodeSyncSeekState SeekState => _seekState;

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
