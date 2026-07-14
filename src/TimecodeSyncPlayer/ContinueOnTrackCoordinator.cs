using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// Continue モードの OnTrack パス（Gap 終了 / トラック切替 / 同一トラック同期）の
/// 判定・シーク発行・トラックロード制御を担う。MainWindow.HandleOnTrackSync から抽出。
/// 判定条件・実行順序・早期return・ログテンプレートは抽出前と完全に一致させること。
/// 副作用はすべて <see cref="ContinueOnTrackEffects"/> のデリゲート経由で注入する。
/// </summary>
internal sealed class ContinueOnTrackCoordinator
{
    private readonly TimecodeSyncService _syncService;
    private readonly FileLoadStabilityLogState _fileLoadStabilityLogState;
    private readonly ContinueOnTrackEffects _effects;

    public ContinueOnTrackCoordinator(
        TimecodeSyncService syncService,
        FileLoadStabilityLogState fileLoadStabilityLogState,
        ContinueOnTrackEffects effects)
    {
        _syncService = syncService;
        _fileLoadStabilityLogState = fileLoadStabilityLogState;
        _effects = effects;
    }

    public void Handle(TimelineQueryResult result, double ltcSeconds)
    {
        // Gap 終了チェックを先頭で実施（mediaPos への正確なシーク付き）
        GapExitAction exitAction = _effects.DecideGapExit();
        if (exitAction.Type == GapExitActionType.ResumePlayback)
        {
            double targetPos = result.MediaPositionSeconds;
            _effects.SeekTo(targetPos);
            _effects.ResumeMpvPause();
            _effects.ApplyPauseState(false);
            _effects.ShowOsdBar();
            Log.Information("Continue mode: exiting gap, resuming playback at {Pos:F3}", targetPos);
            _effects.UpdateCurrentTrackLabel();
            return;    // このフレームは Gap 終了処理のみ実行し、次フレームで通常同期へ
        }

        ContinueOnTrackDecision onTrackDecision = ContinueOnTrackPlanner.Decide(result, _effects.GetLoadedTrackId());
        PlaylistTrack track = onTrackDecision.Track;
        double mediaPos = onTrackDecision.MediaPositionSeconds;

        if (onTrackDecision.Action == ContinueOnTrackAction.SwitchTrack)
        {
            Log.Information("Continue mode: switching to track {TrackName} at media position {Pos:F3}s", track.Name, mediaPos);
            bool success = _effects.LoadFile(track.FilePath, mediaPos);
            if (success)
            {
                _effects.SetLoadedTrackId(track.Id);

                _syncService.BeginFileLoad(mediaPos, _effects.GetTotalRenderedFrames());
                _fileLoadStabilityLogState.Reset();
            }
        }
        else
        {
            (int timePosRc, double playbackSeconds) = _effects.GetTimePos();
            if (timePosRc != 0) return;

            if (!_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            {
                if (_fileLoadStabilityLogState.ShouldLog(DateTime.UtcNow))
                {
                    Log.Debug(
                        "Continue mode: waiting for file load stability playback={Playback:F3} mediaPos={MediaPos:F3} renderedFrames={RenderedFrames}",
                        playbackSeconds, mediaPos, _effects.GetTotalRenderedFrames());
                }

                return;
            }

            _fileLoadStabilityLogState.Reset();

            if (playbackSeconds < 0.5)
            {
                Log.Debug("Continue mode: skipping sync decision, playback just started playback={Playback:F3}", playbackSeconds);
                return;
            }

            SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);

            SyncDecision decision = _syncService.EvaluateDecision(mediaPos, state);
            bool suppressSeek = _syncService.ShouldSuppressSeek(playbackSeconds, decision.ToleranceSeconds);
            ContinueSyncSeekPlan seekPlan = ContinueSyncSeekPlanner.Decide(decision, suppressSeek, _syncService.IsDebounced());

            if (!seekPlan.ShouldSeek)
                return;

            bool success = _effects.SeekTo(seekPlan.TargetSeconds);
            if (success)
                _syncService.ReportSeekSent(seekPlan.TargetSeconds);
            Log.Information(
                "Continue mode: sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} success={Success}",
                ltcSeconds, playbackSeconds, seekPlan.TargetSeconds,
                decision.DeltaSeconds, decision.ToleranceSeconds, success);
        }
    }
}

/// <summary>
/// <see cref="ContinueOnTrackCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッドをフェイク可能な形で注入する。
/// loadedTrackId は <see cref="GetLoadedTrackId"/>/<see cref="SetLoadedTrackId"/> 経由で
/// アクセスし、更新タイミング（LoadFile 成功直後）を現行と同一に保つ。
/// </summary>
internal sealed record ContinueOnTrackEffects(
    Func<GapExitAction> DecideGapExit,
    Func<double, bool> SeekTo,
    Action ResumeMpvPause,
    Action<bool> ApplyPauseState,
    Action ShowOsdBar,
    Action UpdateCurrentTrackLabel,
    Func<Guid?> GetLoadedTrackId,
    Action<Guid> SetLoadedTrackId,
    Func<string, double, bool> LoadFile,
    Func<long> GetTotalRenderedFrames,
    Func<(int rc, double playbackSeconds)> GetTimePos,
    Func<double, SyncPlaybackState> BuildPlaybackState);
