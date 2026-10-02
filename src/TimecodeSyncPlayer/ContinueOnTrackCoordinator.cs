using System.Diagnostics;
using Serilog;
using TimecodeSyncPlayer.Contracts;

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
            // v0.5.4 B6b（規則 3）: ギャップの出口も relocate。目標は M(now) + c（マスターが動いている間）で、
            // クリップの範囲に収める。発行したら着地の状態に通す（着地するまで次のシークを出さない）。
            double exitTarget = RelocateTarget(track, mediaPos);
            if (!_effects.SeekTo(exitTarget))
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "gap-exit-seek");
            _syncService.ReportSeekSent(exitTarget, "gap-exit");
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
            // v0.5.4 B6b（規則 3 の予測ロケート）: 先行量 c があればそれを使う（D7-a の補償より優先。
            // 経路で分けない 1 つの c）。無ければ従来どおり D7-a の補償（既定 0）。
            double lookaheadSeconds = _syncService.RelocateLookaheadSeconds;
            double loadPosition = lookaheadSeconds > 0.0
                ? RelocateTarget(track, mediaPos)
                : mediaPos + compensationSeconds;
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
            SyncPositionRead read = _effects.ReadPosition();
            if (!read.Succeeded)
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "time-pos");
            double playbackSeconds = read.PlaybackSeconds;

            // 位置サンプルは秒と同じ照会の結果（追加の照会は無い）。v0.5.4 #7: 出力トレースの有無と関係なく
            // 常に渡し、relocate の粗い判定の誤差を配信 PTS（評価位置）で測る（試験の道具で判断を変えない）。
            PlaybackPositionSample? positionSample = read.Sample;

            SyncPlaybackState state = _effects.BuildPlaybackState(playbackSeconds);
            // v0.5.4 段 B1: 着地の状態（新しい判定）は、位置を照会したすべての場所で観測する
            // （LTC のフレームの経路に依らない観測は UI タイマー・保持の Duplicate が担う。§9-7 の 1）。
            // v0.5.4 段 B3: ロードの成立（旧 門 18）も着地の事象で決まるので、観測の後に解除だけ試す。
            // ロード中の抑止は着地待ち（EvaluateDecision の未信頼）が担う。
            _syncService.ObserveLandingState(read, state.VideoFps, state.TimecodeFps);
            if (_syncService.TryMarkFileLoaded(playbackSeconds, _effects.GetTotalRenderedFrames()))
            {
                _fileLoadStabilityLogState.Reset();
            }
            else if (_fileLoadStabilityLogState.ShouldLog(DateTime.UtcNow))
            {
                Log.Debug(
                    "Continue mode: waiting for file load stability playback={Playback:F3} mediaPos={MediaPos:F3} renderedFrames={RenderedFrames}",
                    playbackSeconds, mediaPos, _effects.GetTotalRenderedFrames());
            }

            SyncDecision decision = _syncService.EvaluateDecision(mediaPos, state, positionSample);
            // None の decision は TargetSeconds=0 のため、シーク要求として渡さない（D20-b (ii)）。
            // D38 (b): 未信頼の要求の目標は、EvaluateDecision が pending の破棄（門 8）に使う
            // （ここで渡すと pending の置き換え（re-pend）が先に走り、着地の観測を失う）。
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
                // v0.5.4 段 0: Continue 側のシーク見送りの理由（門 5・14 の Suppressed / Debounced）を数える。
                Log.Debug(
                    "sync.gate seek-skip reason={Reason} ltc={Ltc:F3} playback={Playback:F3} target={Target:F3}",
                    seekPlan.SkipReason, ltcSeconds, playbackSeconds, decision.TargetSeconds);
                // 保留中・抑止・デバウンスのシークがあるフレームでは補正を評価しない。
                return ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "pending-seek");
            }

            bool success = _effects.SeekTo(seekPlan.TargetSeconds);
            if (success)
            {
                _syncService.ReportSeekSent(seekPlan.TargetSeconds);
                // v0.5.4 段 B2 の計測: 着地から 500ms 以内の同期シーク（旧 門 9 が隠していた量）。
                _syncService.NotePostLandingSeekIssued(seekPlan.TargetSeconds);
            }
            // v0.6.3 段 1（観測）: 先行量（lookaheadMs）とその出所（cSource）を配布ビルドでも数えられるように出す。
            Log.Information(
                "Continue mode: sync seek ltc={Ltc:F3} playback={Playback:F3} target={Target:F3} delta={Delta:F3} tolerance={Tolerance:F4} success={Success} lookaheadMs={LookaheadMs:F1} cSource={CSource}",
                ltcSeconds, playbackSeconds, seekPlan.TargetSeconds,
                decision.DeltaSeconds, decision.ToleranceSeconds, success,
                _syncService.RelocateLookaheadSeconds * 1000.0, _syncService.RelocateLookaheadSource);
            return success
                ? new ContinueFrameContext(SyncRequestResult.Complete, false, mediaPos, playbackSeconds, "seek-issued")
                : ContinueFrameContext.Blocked(SyncRequestResult.Deferred, "seek-failed");
        }
    }

    /// <summary>
    /// v0.5.4 B6b（規則 3 の予測ロケート）: relocate の目標 = 素材位置 + 先行量（マスターが動いている間の c）を
    /// トラックの範囲に収める（D29 と同じ切り詰め。尺が分からなければ上は切らない）。先行量が 0 なら素材位置のまま。
    /// </summary>
    private double RelocateTarget(PlaylistTrack track, double mediaPos)
    {
        double lookaheadSeconds = _syncService.RelocateLookaheadSeconds;
        if (lookaheadSeconds <= 0.0)
            return mediaPos;
        double target = mediaPos + lookaheadSeconds;
        double durationSeconds = track.MediaDuration.TotalSeconds;
        if (track.MediaOut is null && durationSeconds <= 0.0)
            return target;
        return SyncDecisionEngine.ClampToClip(
            target, track.MediaIn.TotalSeconds, track.MediaOut?.TotalSeconds, durationSeconds,
            track.FrameRate ?? 0.0);
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
    Func<double, SyncPlaybackState> BuildPlaybackState);
