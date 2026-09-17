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
    private readonly SingleModeSyncEffects _effects;

    public SingleModeSyncCoordinator(
        TimecodeSyncService syncService,
        SingleModeSyncEffects effects)
    {
        _syncService = syncService;
        _effects = effects;
    }

    public SyncRequestResult Apply(double ltcSeconds)
    {
        // During a native seek, time-pos can still be the synthetic requested target.
        // Do not let it settle the pending seek or complete file-load stability checks.
        if (_effects.IsNativeSeeking?.Invoke() == true)
            return SyncRequestResult.Deferred;

        (int timePosRc, double playbackSeconds) = _effects.GetTimePos();
        if (timePosRc != 0) return SyncRequestResult.Deferred;

        SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);

        if (_syncService.IsLoadingFile && _effects.GetTotalRenderedFrames != null &&
            !_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            return SyncRequestResult.Deferred;

        SyncDecision decision = _syncService.EvaluateDecision(ltcSeconds, state);
        // None の decision は TargetSeconds=0 のため、シーク要求として渡さない（D20-b (ii)）。
        double requestedTarget = decision.Action == SyncActionType.Seek ? decision.TargetSeconds : double.NaN;
        bool suppressSeek = _syncService.ShouldSuppressSeek(playbackSeconds, decision.ToleranceSeconds,
            requestedTarget);
        if (decision.Action != SyncActionType.Seek)
            return _syncService.SeekState.HasPendingSeek
                ? SyncRequestResult.Deferred : SyncRequestResult.Complete;

        if (suppressSeek)
        {
            Log.Debug(
                "Timecode sync seek suppressed pendingTarget={PendingTarget:F3} playback={Playback:F3} ltc={Ltc:F3} requestedTarget={RequestedTarget:F3} tolerance={Tolerance:F4}",
                _syncService.SeekState.TargetSeconds, playbackSeconds, ltcSeconds,
                decision.TargetSeconds, decision.ToleranceSeconds);
            return SyncRequestResult.Deferred;
        }

        if (_syncService.IsDebounced())
            return SyncRequestResult.Deferred;

        bool success = _effects.SeekTo(decision.TargetSeconds);
        if (success)
            _syncService.ReportSeekSent(decision.TargetSeconds);
        Log.Information(
            "Timecode sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} videoFps={VideoFps:F3} timecodeFps={TimecodeFps:F3} defaultVideoFps={DefaultVideoFps} defaultTimecodeFps={DefaultTimecodeFps} success={Success}",
            ltcSeconds, playbackSeconds, decision.TargetSeconds, decision.DeltaSeconds,
            decision.ToleranceSeconds, decision.VideoFpsUsed, decision.TimecodeFpsUsed,
            decision.UsedDefaultVideoFps, decision.UsedDefaultTimecodeFps, success);
        return success ? SyncRequestResult.Complete : SyncRequestResult.Deferred;
    }
}

/// <summary>
/// <see cref="SingleModeSyncCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッドをフェイク可能な形で注入する。
/// </summary>
internal sealed record SingleModeSyncEffects(
    Func<(int rc, double playbackSeconds)> GetTimePos,
    Func<double, SyncPlaybackState> BuildPlaybackState,
    Func<double, bool> SeekTo,
    Func<long>? GetTotalRenderedFrames = null,
    Func<bool>? IsNativeSeeking = null);
