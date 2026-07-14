using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// Single モード（1トラック内絶対シーク）の同期判定・シーク発行を担う。
/// MainWindow.ApplySingleModeSync から抽出。判定条件・実行順序・早期return・
/// ログテンプレートは抽出前と完全に一致させること。
/// </summary>
internal sealed class SingleModeSyncCoordinator
{
    private readonly TimecodeSyncService _syncService;
    private readonly Func<(int rc, double playbackSeconds)> _getTimePos;
    private readonly Func<double, SyncPlaybackState> _buildPlaybackState;
    private readonly Func<double, bool> _seekTo;

    public SingleModeSyncCoordinator(
        TimecodeSyncService syncService,
        Func<(int rc, double playbackSeconds)> getTimePos,
        Func<double, SyncPlaybackState> buildPlaybackState,
        Func<double, bool> seekTo)
    {
        _syncService = syncService;
        _getTimePos = getTimePos;
        _buildPlaybackState = buildPlaybackState;
        _seekTo = seekTo;
    }

    public void Apply(double ltcSeconds)
    {
        (int timePosRc, double playbackSeconds) = _getTimePos();
        if (timePosRc != 0) return;

        SyncPlaybackState state = _buildPlaybackState(playbackSeconds);

        SyncDecision decision = _syncService.EvaluateDecision(ltcSeconds, state);
        if (decision.Action != SyncActionType.Seek)
            return;

        bool suppressSeek = _syncService.ShouldSuppressSeek(playbackSeconds, decision.ToleranceSeconds);

        if (suppressSeek)
        {
            Log.Debug(
                "Timecode sync seek suppressed pendingTarget={PendingTarget:F3} playback={Playback:F3} ltc={Ltc:F3} requestedTarget={RequestedTarget:F3} tolerance={Tolerance:F4}",
                _syncService.SeekState.TargetSeconds, playbackSeconds, ltcSeconds,
                decision.TargetSeconds, decision.ToleranceSeconds);
            return;
        }

        if (_syncService.IsDebounced())
            return;

        bool success = _seekTo(decision.TargetSeconds);
        if (success)
            _syncService.ReportSeekSent(decision.TargetSeconds);
        Log.Information(
            "Timecode sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} videoFps={VideoFps:F3} timecodeFps={TimecodeFps:F3} defaultVideoFps={DefaultVideoFps} defaultTimecodeFps={DefaultTimecodeFps} success={Success}",
            ltcSeconds, playbackSeconds, decision.TargetSeconds, decision.DeltaSeconds,
            decision.ToleranceSeconds, decision.VideoFpsUsed, decision.TimecodeFpsUsed,
            decision.UsedDefaultVideoFps, decision.UsedDefaultTimecodeFps, success);
    }
}
