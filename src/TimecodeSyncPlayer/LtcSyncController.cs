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
    Action ResumeGapPause);

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
    private double? _lastAcceptedLtcSeconds;
    private string _formatText = "LTC 停止中";

    public LtcSyncController(
        PlaylistState playlist, GapFreezeHandler gap, TimecodeSyncService syncService,
        LtcFrameProcessor frames, int timeoutMilliseconds, int resumeFrames,
        LtcSyncEffects effects, Func<SingleModeSyncCoordinator> single,
        Func<ContinueOnTrackCoordinator> continueOnTrack, Func<GapEnterCoordinator> gapCoordinator)
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
    }

    public double LastLtcSeconds { get; private set; }
    public double LastTimecodeFps => _frames.LastTimecodeFps;

    public void SyncEnabledChanged()
    {
        if (!_effects.GetContext().SyncEnabled)
            _syncService.ClearSeekState();
        ExitGapForManualControl();
        ReapplyLastAcceptedFrame();
    }

    public void SyncModeChanged()
    {
        _frames.ResetDiagnostics();
        _syncService.ClearSeekState();
        ExitGapForManualControl();
        _effects.UpdateCurrentTrackLabel();
        ReapplyLastAcceptedFrame();
    }

    public void GapBehaviorChanged() => ReapplyLastAcceptedFrame();

    private void ReapplyLastAcceptedFrame()
    {
        LtcSyncContext state = _effects.GetContext();
        if (_lastAcceptedLtcSeconds is double seconds && state.IsMonitoring &&
            state.SyncEnabled && !state.IsSeeking && !_signalLoss.ShouldSuppressSync)
            ApplySync(seconds);
    }

    public void FpsModeChanged() => _frames.ResetForFpsMode(_effects.GetContext().FpsMode);

    public void MonitoringChanged()
    {
        _lastAcceptedLtcSeconds = null;
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
        _lastAcceptedLtcSeconds = processed.ResolvedSeconds;
        ObserveValidFrame(processed.ResolvedSeconds, receivedAtMilliseconds);
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
        if (!_signalLoss.ShouldSuppressSync)
            ApplySync(seconds);
    }

    public void Tick(long nowMilliseconds)
    {
        ApplySignalLossAction(_signalLoss.Evaluate(nowMilliseconds, SignalContext()));
        RefreshDisplay();
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

    private void ApplySync(double seconds)
    {
        LtcSyncContext state = _effects.GetContext();
        if (!state.IsMpvReady)
            return;
        if (state.Mode != SyncMode.Continue)
        {
            if (state.SyncEnabled && !state.IsSeeking && _playlist.Current != null)
                _effects.ResumeProjectRestorePause();
            _single().Apply(seconds);
            return;
        }
        if (!state.SyncEnabled || state.IsSeeking)
            return;
        TimelineQueryResult result = _playlist.FindTrackAtTimelinePosition(seconds);
        string? trackName = result.Track?.Name;
        if (_queryLog.ShouldLog(result.Status, trackName, result.MediaPositionSeconds, DateTime.UtcNow))
            Log.Debug("Continue mode query result: status={Status} track={Track} mediaPos={MediaPos:F3}",
                result.Status, trackName ?? "null", result.MediaPositionSeconds);
        switch (result.Status)
        {
            case TimelineQueryStatus.OnTrack:
                _effects.ResumeProjectRestorePause();
                _continue().Handle(result, seconds);
                break;
            case TimelineQueryStatus.Gap:
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
                if (_gap.ShouldTransitionFromFreezeToBlack(state.GapBehavior))
                    _effects.ClearGapFreezeFrame();
                _gapCoordinator().HandleNoTracks();
                break;
        }
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
