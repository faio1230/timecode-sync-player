using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer;

/// <summary>
/// Continue モードの OnTrack パス（Gap 終了 / トラック切替 / 同一トラック同期）の
/// 判定・シーク発行・トラックロード制御を担う。MainWindow.HandleOnTrackSync から抽出。
/// Gap 終了時もトラックを先に判定し、新しい動画のロード後に停止状態を復元する。
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

    public SyncRequestResult Handle(TimelineQueryResult result, double ltcSeconds) =>
        HandleFrame(result, ltcSeconds).Request;

    /// <summary>
    /// T7: 補正（Smooth / Jump）が使う素材位置と、このフレームで評価してよいかも返す。
    /// MediaPositionSeconds / PlaybackSeconds は粗い同期判定（EvaluateDecision）に渡した値
    /// そのもので、補正の残差は MediaPositionSeconds − PlaybackSeconds（= sync.evaluate の
    /// delta）になる。素材位置の計算はここ 1 か所だけに置く。
    /// </summary>
    public ContinueFrameContext HandleFrame(TimelineQueryResult result, double ltcSeconds)
    {
        ContinueOnTrackDecision onTrackDecision = ContinueOnTrackPlanner.Decide(result, _effects.GetLoadedTrackId());
        PlaylistTrack track = onTrackDecision.Track;
        double mediaPos = onTrackDecision.MediaPositionSeconds;
        GapExitAction exitAction = _effects.PeekGapExit();
        bool wasPaused = _effects.IsPlaybackPaused();
        bool exitingGap = exitAction.Type == GapExitActionType.ResumePlayback;

        // A different clip must be loaded before releasing the gap-owned pause.
        if (exitingGap && onTrackDecision.Action != ContinueOnTrackAction.SwitchTrack)
        {
            if (!_effects.SeekTo(mediaPos))
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "gap-exit-seek");
            // D37-b2: ギャップ（黒・フリーズ）明けの着地。直後の不足は速度補正ではなくシークで詰める。
            _syncService.NotifyLanding();
            CompleteGapExit(exitAction);
            // ギャップ出口のシークを発行したフレームでは補正を評価しない。
            return new ContinueFrameContext(SyncRequestResult.Complete, false, mediaPos, 0.0, "gap-exit", ExitedGap: true);
        }

        if (onTrackDecision.Action == ContinueOnTrackAction.SwitchTrack)
        {
            // トラック切替はロードで着地位置が決まるため、ここでも先行補償を通す（学習はトラック単位）。
            SeekLatencyCompensator compensator = _syncService.LatencyCompensator;
            compensator.SelectTrack(track.Id);
            double compensationSeconds = compensator.CompensationForTrack(track.Id);
            double loadPosition = mediaPos + compensationSeconds;
            Log.Information(
                "Continue mode: switching to track {TrackName} at media position {Pos:F3}s compensation={CompensationMs:F1}ms",
                track.Name, mediaPos, compensationSeconds * 1000.0);
            long loadIssuedQpc = Stopwatch.GetTimestamp();
            bool success = _effects.LoadFile(track.FilePath, loadPosition);
            if (success)
            {
                _effects.SetLoadedTrackId(track.Id);

                _syncService.BeginFileLoad(loadPosition, _effects.GetTotalRenderedFrames(), loadIssuedQpc, "track-switch");
                _fileLoadStabilityLogState.Reset();
                _effects.UpdateCurrentTrackLabel();
                if (exitingGap)
                {
                    if (!exitAction.ShouldResumePlayback)
                        _effects.ApplyPauseState(wasPaused);
                    CompleteGapExit(exitAction);
                }
            }
            return success
                ? new ContinueFrameContext(SyncRequestResult.Complete, false, mediaPos, 0.0, "switch", SwitchedTrack: true)
                : ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "switch-failed");
        }
        else
        {
            // 0.4.5-A フェーズ 1: shadow は trace 有効時だけ読む（無効時は従来どおり位置を読まない）。
            bool traceEnabled = OutputTrace.Current.IsEnabled;

            // Track switches and gap exits above may replace the pending operation.
            // For this clip, native time-pos is not stable until seeking has finished.
            if (_effects.IsNativeSeeking?.Invoke() == true)
            {
                if (traceEnabled)
                {
                    SyncPositionRead shadowRead = _effects.ReadPosition();
                    if (shadowRead.Succeeded)
                    {
                        SyncPlaybackState shadowState = _effects.BuildPlaybackState(shadowRead.PlaybackSeconds);
                        _syncService.RecordPositionShadow(ltcSeconds, shadowState, shadowRead.Sample, "native-seeking");
                    }
                }
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "native-seeking");
            }

            SyncPositionRead read = _effects.ReadPosition();
            if (!read.Succeeded)
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "time-pos");
            double playbackSeconds = read.PlaybackSeconds;

            // 位置サンプルは秒と同じ照会の結果。shadow は trace 有効時だけ渡す。
            PlaybackPositionSample? positionSample = traceEnabled ? read.Sample : null;

            if (!_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            {
                if (_fileLoadStabilityLogState.ShouldLog(DateTime.UtcNow))
                {
                    Log.Debug(
                        "Continue mode: waiting for file load stability playback={Playback:F3} mediaPos={MediaPos:F3} renderedFrames={RenderedFrames}",
                        playbackSeconds, mediaPos, _effects.GetTotalRenderedFrames());
                }

                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "load-stability");
            }

            _fileLoadStabilityLogState.Reset();

            SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);
            SyncDecision decision = _syncService.EvaluateDecision(mediaPos, state, positionSample);
            // None の decision は TargetSeconds=0 のため、シーク要求として渡さない（D20-b (ii)）。
            double requestedTarget = decision.Action == SyncActionType.Seek ? decision.TargetSeconds : double.NaN;
            bool suppressSeek = _syncService.ShouldSuppressSeek(playbackSeconds, decision.ToleranceSeconds,
                requestedTarget);
            // D37-b: シーク中・着地未確認のフレームでは、粗い判定も補正も評価しない。
            if (decision.PositionUntrusted)
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "position-untrusted");
            ContinueSyncSeekPlan seekPlan = ContinueSyncSeekPlanner.Decide(decision, suppressSeek, _syncService.IsDebounced());

            if (!seekPlan.ShouldSeek)
            {
                // D37-a: ゲートが Seek を保留している間は要求を維持し、次の評価で再試行する。
                if (seekPlan.SkipReason == ContinueSyncSeekSkipReason.GateDeferred)
                    return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "seek-gated");
                if (seekPlan.SkipReason == ContinueSyncSeekSkipReason.NoSeekDecision &&
                    !_syncService.SeekState.HasPendingSeek)
                    return new ContinueFrameContext(SyncRequestResult.Complete, true, mediaPos, playbackSeconds);
                // 保留中・抑止・デバウンスのシークがあるフレームでは補正を評価しない。
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "pending-seek");
            }

            bool success = _effects.SeekTo(seekPlan.TargetSeconds);
            if (success)
                _syncService.ReportSeekSent(seekPlan.TargetSeconds);
            Log.Information(
                "Continue mode: sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} success={Success}",
                ltcSeconds, playbackSeconds, seekPlan.TargetSeconds,
                decision.DeltaSeconds, decision.ToleranceSeconds, success);
            return success
                ? new ContinueFrameContext(SyncRequestResult.Complete, false, mediaPos, playbackSeconds, "seek-issued")
                : ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "seek-failed");
        }
    }

    private void CompleteGapExit(GapExitAction exitAction)
    {
        _effects.DecideGapExit();
        _effects.ClearGapFreezeFrame();
        if (exitAction.ShouldResumePlayback)
        {
            _effects.ResumePlayback();
            _effects.ApplyPauseState(false);
        }
        _effects.UpdateCurrentTrackLabel();
    }

}

/// <summary>
/// T7: 1 フレーム分の Continue 判定結果。補正は CorrectionAllowed のときだけ評価し、
/// 残差 = MediaPositionSeconds − PlaybackSeconds、Jump のシーク先 = MediaPositionSeconds を使う。
/// </summary>
internal readonly record struct ContinueFrameContext(
    SyncRequestResult Request,
    bool CorrectionAllowed,
    double MediaPositionSeconds,
    double PlaybackSeconds,
    string CorrectionBlockedReason = "",
    bool SwitchedTrack = false,
    bool ExitedGap = false)
{
    public static ContinueFrameContext Blocked(SyncRequestResult request, string reason) =>
        new(request, false, 0.0, 0.0, reason);
}

/// <summary>
/// <see cref="ContinueOnTrackCoordinator"/> が使用する副作用デリゲート群。
/// MainWindow のフィールド・メソッドをフェイク可能な形で注入する。
/// loadedTrackId は <see cref="GetLoadedTrackId"/>/<see cref="SetLoadedTrackId"/> 経由で
/// アクセスし、更新タイミング（LoadFile 成功直後）を現行と同一に保つ。
/// </summary>
internal sealed record ContinueOnTrackEffects(
    Func<GapExitAction> PeekGapExit,
    Func<GapExitAction> DecideGapExit,
    Func<bool> IsPlaybackPaused,
    Action ClearGapFreezeFrame,
    Func<double, bool> SeekTo,
    Action ResumePlayback,
    Action<bool> ApplyPauseState,
    Action UpdateCurrentTrackLabel,
    Func<Guid?> GetLoadedTrackId,
    Action<Guid> SetLoadedTrackId,
    Func<string, double, bool> LoadFile,
    Func<long> GetTotalRenderedFrames,
    // v0.5.1: 再生位置（秒）と位置サンプルを同じ 1 回の照会で返す。
    Func<SyncPositionRead> ReadPosition,
    Func<double, SyncPlaybackState> BuildPlaybackState,
    Func<bool>? IsNativeSeeking = null);
