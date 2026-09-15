using Serilog;

namespace TimecodeSyncPlayer;

internal readonly record struct LtcSyncContext(
    bool IsMpvReady,
    bool SyncEnabled,
    SyncMode Mode,
    bool IsSeeking,
    bool IsMonitoring,
    bool IsPlaybackPaused,
    LtcSignalLossMode SignalLossMode,
    TimecodeFpsMode FpsMode,
    GapBehavior GapBehavior,
    Guid? LoadedTrackId,
    double VideoFps,
    double DurationSeconds,
    int SignalLossTimeoutMilliseconds,
    int SignalResumeFrames);

/// <summary>UI/native I/O boundaries. All LTC routing and state transitions belong to the controller.</summary>
internal sealed record LtcSyncEffects(
    Func<LtcSyncContext> GetContext,
    Action<string, string> ApplyFrameText,
    Action<LtcDisplayState, string> ApplyDisplay,
    Action<bool> SetMonitoring,
    Action<bool> SetSignalLossPaused,
    Action ResumeProjectRestorePause,
    Action ClearGapFreezeFrame,
    Action RefreshCurrentVideoFrame,
    Action<double> UpdateTimelinePosition,
    Action UpdateCurrentTrackLabel,
    Action RenderGapFreeze,
    Action ResumeGapPause,
    Func<SyncCorrectionMode>? GetCorrectionMode = null,
    Func<double?>? GetPlaybackSeconds = null,
    Func<double, bool>? ApplyRateInstant = null,
    Func<double, bool>? SeekTo = null,
    Action<string>? SetCorrectionStatus = null,
    Func<double>? GetSyncOffsetMilliseconds = null);

/// <summary>
/// UI-thread LTC session orchestration shared by the window and integration scenarios.
/// Receive timestamps are captured by the audio callback before dispatching to this class.
/// Construction only stores dependencies; it does not access UI controls.
/// </summary>
internal sealed class LtcSyncController
{
    private readonly PlaylistState _playlist;
    private readonly GapFreezeHandler _gap;
    private readonly TimecodeSyncService _syncService;
    private readonly LtcFrameProcessor _frames;
    private readonly LtcSignalLossPolicy _signalLoss;
    private readonly LtcSignalLossMonitoringState _monitoring = new();
    private readonly LtcSyncEffects _effects;
    private readonly Func<SingleModeSyncCoordinator> _single;
    private readonly Func<ContinueOnTrackCoordinator> _continue;
    private readonly Func<GapEnterCoordinator> _gapCoordinator;
    private readonly ContinueModeQueryLogState _queryLog = new(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
    private readonly SyncCorrectionController _correction = new();
    private readonly Func<DateTime> _getUtcNow;
    private ContinueFrameContext? _lastContinueFrame;
    private bool _smoothAvailable = true;
    private double _lastAppliedRate = 1.0;
    private bool _rateRestorePending;
    private double? _lastAcceptedLtcSeconds;
    private double? _pendingSyncSeconds;
    private string _formatText = "LTC 停止中";

    public LtcSyncController(
        PlaylistState playlist, GapFreezeHandler gap, TimecodeSyncService syncService,
        LtcFrameProcessor frames, int timeoutMilliseconds, int resumeFrames,
        LtcSyncEffects effects, Func<SingleModeSyncCoordinator> single,
        Func<ContinueOnTrackCoordinator> continueOnTrack, Func<GapEnterCoordinator> gapCoordinator,
        Func<DateTime>? getUtcNow = null)
    {
        _playlist = playlist;
        _gap = gap;
        _syncService = syncService;
        _frames = frames;
        _signalLoss = new(TimeSpan.FromMilliseconds(timeoutMilliseconds), resumeFrames);
        _effects = effects;
        _single = single;
        _continue = continueOnTrack;
        _gapCoordinator = gapCoordinator;
        _getUtcNow = getUtcNow ?? (() => DateTime.UtcNow);
    }

    public double LastLtcSeconds { get; private set; }
    public double LastTimecodeFps => _frames.LastTimecodeFps;

    /// <summary>テスト・診断用: 直近フレームの Continue 補正文脈（フレーム先頭で捨てる）。</summary>
    internal ContinueFrameContext? LastContinueFrame => _lastContinueFrame;

    public void SyncEnabledChanged()
    {
        ResetCorrection();
        _smoothAvailable = true;
        if (!_effects.GetContext().SyncEnabled)
            _syncService.ClearSeekState();
        ExitGapForManualControl();
        ReapplyLastAcceptedFrame();
    }

    public void SyncModeChanged()
    {
        ResetCorrection();
        _smoothAvailable = true;
        _frames.ResetDiagnostics();
        _syncService.ClearSeekState();
        ExitGapForManualControl();
        _effects.UpdateCurrentTrackLabel();
        ReapplyLastAcceptedFrame();
    }

    public void GapBehaviorChanged() => ReapplyLastAcceptedFrame();

    private void ReapplyLastAcceptedFrame()
    {
        _pendingSyncSeconds = null;
        LtcSyncContext state = _effects.GetContext();
        if (_lastAcceptedLtcSeconds is double seconds && state.IsMonitoring &&
            state.SyncEnabled && !state.IsSeeking && !_signalLoss.ShouldSuppressSync)
            RequestSync(seconds);
    }

    public void CancelPendingSync()
    {
        _pendingSyncSeconds = null;
        // T7: 手動シークは補正状態（Smooth の無効化を含む）も捨てる。
        ResetCorrection();
    }

    /// <summary>T7: 操作者の再生・一時停止、プロジェクト差し替えで補正状態を捨てる。</summary>
    public void CorrectionReset() => ResetCorrection();

    /// <summary>
    /// T7: 補正状態を捨て、プレイヤーに掛けた倍率が残っていれば 1.0 に戻す。
    /// 一時停止中などで戻せないときは次の評価可能フレームの評価前に戻す。
    /// </summary>
    private void ResetCorrection()
    {
        _correction.Reset();
        if (_rateRestorePending || Math.Abs(_lastAppliedRate - 1.0) < 0.0005)
            return;
        if (_effects.ApplyRateInstant?.Invoke(1.0) == true)
            _lastAppliedRate = 1.0;
        else
            _rateRestorePending = true;
    }

    private void RequestSync(double seconds)
    {
        _pendingSyncSeconds = ApplySync(seconds) == SyncRequestResult.Deferred ? seconds : null;
    }

    public void FpsModeChanged() => _frames.ResetForFpsMode(_effects.GetContext().FpsMode);

    public void MonitoringChanged()
    {
        _lastAcceptedLtcSeconds = null;
        _pendingSyncSeconds = null;
        if (_effects.GetContext().IsMonitoring)
        {
            _monitoring.MarkStarted();
            _signalLoss.Reset();
            _formatText = "fps: 検出中...";
        }
        else if (!_monitoring.IsDetectionActive(isReportedRunning: false))
        {
            _signalLoss.Reset();
            _formatText = "LTC 停止中";
        }
        RefreshDisplay();
    }

    public void DeviceEnumerationFailed()
    {
        _formatText = "LTC デバイス列挙失敗";
        RefreshDisplay();
    }

    public void MonitorStopped(Exception? exception)
    {
        _lastAcceptedLtcSeconds = null;
        _pendingSyncSeconds = null;
        if (_monitoring.MarkStopped(exception))
        {
            _signalLoss.Reset();
            _effects.ApplyFrameText("--:--:--:--", "-.--- s");
        }
        _formatText = exception == null ? "LTC 停止中" : "LTC 停止エラー";
        // MarkStopped must precede this effect: the VM synchronously re-enters MonitoringChanged.
        _effects.SetMonitoring(false);
        RefreshDisplay();
    }

    public void ReceiveFrame(LtcFrameReceivedEventArgs frame, long receivedAtMilliseconds)
    {
        TimecodeFpsMode mode = _effects.GetContext().FpsMode;
        ReceiveProcessedFrame(_frames.Process(frame, mode), receivedAtMilliseconds, frame, mode);
    }

    /// <summary>Accept an already processed frame, retaining its diagnostic gate and display state.</summary>
    public void ReceiveProcessedFrame(LtcFrameProcessingResult processed, long receivedAtMilliseconds) =>
        ReceiveProcessedFrame(processed, receivedAtMilliseconds, sourceFrame: null, _effects.GetContext().FpsMode);

    private void ReceiveProcessedFrame(
        LtcFrameProcessingResult processed, long receivedAtMilliseconds,
        LtcFrameReceivedEventArgs? sourceFrame, TimecodeFpsMode mode)
    {
        ApplyFrame(processed);
        if (sourceFrame != null)
            LogFrameDiagnostics(sourceFrame, processed, mode);
        if (!processed.ShouldApplySync)
            return;
        // T7: フレーム文脈は必ずこのフレームの処理の先頭で捨てる。抑止などで
        // ApplySync が走らないフレームに前のフレームの素材位置・再生位置を持ち越さない。
        _lastContinueFrame = null;
        // T3: 同期に使う値だけを入口で 1 回オフセットする。表示用の LastLtcSeconds は
        // 受信した LTC の生値を保つ。ここで作った effective 値を共有することで、
        // 同期判断・シーク・クリップ切替・ギャップ出入りが同じ量だけずれる。
        double effectiveSeconds = SyncOffsetPolicy.Apply(
            processed.ResolvedSeconds,
            _effects.GetSyncOffsetMilliseconds?.Invoke() ?? SyncOffsetPolicy.DefaultMilliseconds);
        _lastAcceptedLtcSeconds = effectiveSeconds;
        ObserveValidFrame(effectiveSeconds, receivedAtMilliseconds);
        ApplyCorrection(effectiveSeconds);
    }

    /// <summary>
    /// T5: 粗いデッドゾーンの内側で残差を詰める。Smooth はシークを発行しない。
    /// shim がレート変更を拒否したら Smooth 使用不可として表示し、自動では Jump へ落とさない。
    /// </summary>
    private void ApplyCorrection(double ltcSeconds)
    {
        if (_effects.GetCorrectionMode == null ||
            _effects.ApplyRateInstant == null || _effects.SeekTo == null)
            return;

        LtcSyncContext state = _effects.GetContext();
        if (!state.SyncEnabled || !state.IsMonitoring || state.IsPlaybackPaused || state.IsSeeking)
            return;
        if (_syncService.SeekState.HasPendingSeek)
            return;

        if (_rateRestorePending)
        {
            // T7: 一時停止中などで戻せなかった倍率を、評価の前に 1.0 へ戻す。
            if (!_effects.ApplyRateInstant(1.0))
                return;
            _lastAppliedRate = 1.0;
            _rateRestorePending = false;
        }

        double residualSeconds;
        double targetSeconds;
        if (state.Mode == SyncMode.Continue)
        {
            // T7: Continue は粗い同期判定と同じ素材位置と再生位置を使う。素材位置は
            // コーディネーターが 1 か所で出した値なので、残差は sync.evaluate の delta と一致する。
            if (_lastContinueFrame is not { CorrectionAllowed: true } frame)
                return;
            residualSeconds = frame.MediaPositionSeconds - frame.PlaybackSeconds;
            targetSeconds = frame.MediaPositionSeconds;
        }
        else
        {
            if (_effects.GetPlaybackSeconds == null) return;
            if (_effects.GetPlaybackSeconds() is not double playback || !double.IsFinite(playback))
                return;
            residualSeconds = ltcSeconds - playback;
            targetSeconds = ltcSeconds;
        }

        SyncCorrectionDecision decision = _correction.Evaluate(
            residualSeconds, targetSeconds, _effects.GetCorrectionMode(), _smoothAvailable, _getUtcNow());

        switch (decision.Action)
        {
            case SyncCorrectionActionType.SetRate:
                if (!_effects.ApplyRateInstant(decision.Rate))
                {
                    _smoothAvailable = false;
                    Log.Warning("Smooth 補正を使用できません（レート変更が拒否されました）。Jump への切替を検討してください");
                }
                else if (Math.Abs(decision.Rate - _lastAppliedRate) >= 0.0005)
                {
                    _lastAppliedRate = decision.Rate;
                    Log.Information(
                        "Smooth correction rate={Rate:F5} residualMs={ResidualMs:F1}",
                        decision.Rate, residualSeconds * 1000.0);
                }
                break;
            case SyncCorrectionActionType.Seek:
                // Smooth の倍率を Jump へ持ち込まない（shim 側では強制しない）。
                _effects.ApplyRateInstant(1.0);
                _lastAppliedRate = 1.0;
                if (_effects.SeekTo(decision.TargetSeconds))
                {
                    Log.Information(
                        "Jump correction seek target={Target:F3} residualMs={ResidualMs:F1}",
                        decision.TargetSeconds, residualSeconds * 1000.0);
                    _syncService.ReportSeekSent(decision.TargetSeconds);
                }
                break;
        }

        string status =
            !_smoothAvailable || _correction.SmoothUnavailable ? "Smooth 使用不可: Jump に切替"
            : _correction.SmoothDisabled ? "Smooth 補正なし（効かない）"
            : "";
        _effects.SetCorrectionStatus?.Invoke(status);
    }

    private static void LogFrameDiagnostics(
        LtcFrameReceivedEventArgs frame, LtcFrameProcessingResult processed, TimecodeFpsMode mode)
    {
        if (processed.ShouldLogFps)
            Log.Information(
                "LTC fps resolved mode={Mode} detectedFps={DetectedFps:F3} dropFrame={DropFrame} resolvedFps={ResolvedFps:F3}",
                mode, frame.Fps, frame.Timecode.DropFrame, processed.ResolvedFps);
        if (processed.Diagnostic.Status is not (TimecodeFrameDiagnosticStatus.Initial or TimecodeFrameDiagnosticStatus.Normal))
            Log.Warning(
                "LTC frame diagnostic status={Status} tc={Timecode} rawSeconds={RawSeconds:F3} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2} detectedFps={DetectedFps:F3} resolvedFps={ResolvedFps:F3} mode={Mode}",
                processed.Diagnostic.Status, frame.Timecode, frame.RealTimeSeconds, processed.ResolvedSeconds,
                processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames, frame.Fps, processed.ResolvedFps, mode);
        if (!processed.ShouldApplySync)
            Log.Information(
                "Timecode sync skipped due to LTC frame diagnostic status={Status} tc={Timecode} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2}",
                processed.Diagnostic.Status, frame.Timecode, processed.ResolvedSeconds,
                processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames);
    }

    private void ApplyFrame(LtcFrameProcessingResult processed)
    {
        _effects.ApplyFrameText(processed.TimecodeText, processed.RealTimeText);
        LastLtcSeconds = processed.ResolvedSeconds;
        _formatText = processed.FormatText;
        RefreshDisplay();
    }

    private void ObserveValidFrame(double seconds, long receivedAtMilliseconds)
    {
        ApplySignalLossAction(_signalLoss.ObserveValidFrame(receivedAtMilliseconds, SignalContext()));
        RefreshDisplay();
        _pendingSyncSeconds = null;
        if (!_signalLoss.ShouldSuppressSync)
            RequestSync(seconds);
    }

    public void Tick(long nowMilliseconds)
    {
        ApplySignalLossAction(_signalLoss.Evaluate(nowMilliseconds, SignalContext()));
        RefreshDisplay();
        if (_pendingSyncSeconds is double seconds)
            RequestSync(seconds);
    }

    private LtcSignalLossContext SignalContext()
    {
        LtcSyncContext state = _effects.GetContext();
        return new(state.SignalLossMode, state.SyncEnabled,
            _monitoring.IsDetectionActive(state.IsMonitoring), !_gap.IsInactive, state.IsPlaybackPaused);
    }

    private void RefreshDisplay()
    {
        LtcDisplayState display = LtcDisplayStateFormatter.Format(
            _monitoring.IsDetectionActive(_effects.GetContext().IsMonitoring), _signalLoss.IsLost, _formatText);
        _effects.ApplyDisplay(display, LtcSignalLossPauseReasonFormatter.Format(_signalLoss.IsPauseOwned));
    }

    private void ApplySignalLossAction(LtcSignalLossAction action)
    {
        if (action == LtcSignalLossAction.None || !_effects.GetContext().IsMpvReady)
            return;
        bool pause = action == LtcSignalLossAction.Pause;
        _effects.SetSignalLossPaused(pause);
        LtcSyncContext state = _effects.GetContext();
        if (pause)
            Log.Information("LTC signal lost: playback paused timeoutMs={TimeoutMs}", state.SignalLossTimeoutMilliseconds);
        else
            Log.Information("LTC signal restored: playback resumed resumeFrames={ResumeFrames}", state.SignalResumeFrames);
    }

    private SyncRequestResult ApplySync(double seconds)
    {
        _lastContinueFrame = null;
        LtcSyncContext state = _effects.GetContext();
        if (!state.IsMpvReady || !state.IsMonitoring || !state.SyncEnabled ||
            state.IsSeeking || _signalLoss.ShouldSuppressSync)
            return SyncRequestResult.Complete;
        if (state.Mode != SyncMode.Continue)
        {
            if (state.SyncEnabled && !state.IsSeeking && _playlist.Current != null)
                _effects.ResumeProjectRestorePause();
            return _single().Apply(seconds);
        }
        TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(seconds);
        string? trackName = result.Track?.Name;
        if (_queryLog.ShouldLog(result.Status, trackName, result.MediaPositionSeconds, DateTime.UtcNow))
            Log.Debug("Continue mode query result: status={Status} track={Track} mediaPos={MediaPos:F3}",
                result.Status, trackName ?? "null", result.MediaPositionSeconds);
        switch (result.Status)
        {
            case TimelineQueryStatus.OnTrack:
                _effects.ResumeProjectRestorePause();
                ContinueFrameContext frame = _continue().HandleFrame(result, seconds);
                _lastContinueFrame = frame;
                if (frame.SwitchedTrack)
                {
                    // T7: トラック切替（ロード成功）で補正状態を捨て、Smooth を再試行できるようにする。
                    ResetCorrection();
                    _smoothAvailable = true;
                }
                if (frame.ExitedGap)
                    ResetCorrection();
                return frame.Request;
            case TimelineQueryStatus.Gap:
                // T7: ギャップ中は補正を評価しない（出入りのたびに状態を捨てる）。
                ResetCorrection();
                if (_gap.ShouldTransitionFromFreezeToBlack(state.GapBehavior))
                    _effects.ClearGapFreezeFrame();
                _effects.UpdateTimelinePosition(seconds);
                GapEnterAction action = _gap.DecideGapEnter(result, state.GapBehavior,
                    state.LoadedTrackId, state.VideoFps, state.DurationSeconds);
                GapEnterCoordinator coordinator = _gapCoordinator();
                new GapEnterActionDispatcher(new GapEnterActionHandlers(
                    coordinator.EnterBlackGap, coordinator.EnterForceBlack, _effects.RenderGapFreeze,
                    coordinator.StartGapFreezeCaptureForCurrentTrack,
                    coordinator.LoadPreviousTrackFinalFrameForGapFreeze)).Execute(action, result);
                _effects.UpdateCurrentTrackLabel();
                break;
            case TimelineQueryStatus.NoTracks:
                ResetCorrection();
                if (_gap.ShouldTransitionFromFreezeToBlack(state.GapBehavior))
                    _effects.ClearGapFreezeFrame();
                _gapCoordinator().HandleNoTracks();
                break;
        }
        return SyncRequestResult.Complete;
    }

    private void ExitGapForManualControl()
    {
        LtcSyncContext state = _effects.GetContext();
        if (!GapStateExitPolicy.ShouldExit(state.SyncEnabled, state.Mode, !_gap.IsInactive))
            return;
        GapExitAction exit = _gap.DecideGapExit();
        _gap.ResetAll();
        if (exit.ShouldResumePlayback && !_signalLoss.IsPauseOwned && state.IsMpvReady)
            _effects.ResumeGapPause();
        _effects.ClearGapFreezeFrame();
        _effects.RefreshCurrentVideoFrame();
        Log.Information("Gap state cleared for manual control syncEnabled={SyncEnabled} mode={Mode}",
            state.SyncEnabled, state.Mode);
        _effects.UpdateCurrentTrackLabel();
    }
}
