namespace TimecodeSyncPlayer.Tests.Integration;

internal sealed record ScenarioMpvOperation(string Name, double? Value = null, string? Text = null);

/// <summary>
/// MainWindow の同期配線を UI なしで再現し、mpv 境界だけを記録するシナリオ基盤。
/// </summary>
internal sealed class SyncScenarioHarness
{
    private readonly TimecodeSyncService _syncService =
        new(new SyncDecisionEngine(), new TimecodeSyncSeekState());
    private readonly GapFreezeHandler _gap = new();
    private readonly LtcSignalLossPolicy _signalLoss =
        new(TimeSpan.FromMilliseconds(250), resumeFrameCount: 3);
    private readonly PlaybackControlState _playback = new();
    private readonly ContinueOnTrackCoordinator _continueCoordinator;
    private readonly GapEnterCoordinator _gapCoordinator;
    private readonly GapEnterActionDispatcher _gapDispatcher;

    private long _monotonicMilliseconds = 10_000;
    private long _renderedFrames;
    private Guid? _loadedTrackId;
    private double _playbackSeconds = 1;
    private double _durationSeconds = 5;
    private double _videoFps = 25;

    public SyncScenarioHarness()
    {
        _continueCoordinator = new ContinueOnTrackCoordinator(
            _syncService,
            new FileLoadStabilityLogState(TimeSpan.FromSeconds(1)),
            new ContinueOnTrackEffects(
                DecideGapExit: _gap.DecideGapExit,
                SeekTo: Seek,
                ResumeMpvPause: () => Operations.Add(new("pause", Text: "no")),
                ApplyPauseState: SetPaused,
                ShowOsdBar: () => Operations.Add(new("osd-bar", Text: "yes")),
                UpdateCurrentTrackLabel: () => Operations.Add(new("update-label")),
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => _loadedTrackId = id,
                LoadFile: LoadFile,
                GetTotalRenderedFrames: () => _renderedFrames,
                GetTimePos: () => (0, _playbackSeconds),
                BuildPlaybackState: playback => new SyncPlaybackState(
                    SyncEnabled,
                    Playlist.Current != null,
                    IsSeeking,
                    playback,
                    _durationSeconds,
                    _videoFps,
                    TimecodeFps: 25)));

        _gapCoordinator = new GapEnterCoordinator(
            _gap,
            new GapEnterEffects(
                ResetEndAdvanceTriggered: () => { },
                PauseForGap: () => Operations.Add(new("pause-for-gap")),
                ApplyPauseState: SetPaused,
                RenderBlack: () => Operations.Add(new("render-black")),
                RenderGapFreeze: () => Operations.Add(new("render-freeze")),
                ClearGapFreezeFrame: () => Operations.Add(new("clear-freeze")),
                SeekTo: Seek,
                GetMpvDuration: () => (0, _durationSeconds),
                IsMpvReady: () => true,
                LoadPausedAt: (path, target) =>
                {
                    Operations.Add(new("load-paused", target, path));
                    return new GapLoadCommandResult(0, 0);
                },
                ResetPlayerStateForNewTrack: () => { },
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => _loadedTrackId = id,
                GetDuration: () => _durationSeconds,
                SetDuration: duration => _durationSeconds = duration,
                GetFps: () => _videoFps,
                SetFps: fps => _videoFps = fps,
                GetGapBehavior: () => GapBehavior,
                UpdateCurrentTrackLabel: () => Operations.Add(new("update-label"))));

        _gapDispatcher = new GapEnterActionDispatcher(new GapEnterActionHandlers(
            _gapCoordinator.EnterBlackGap,
            _gapCoordinator.EnterForceBlack,
            () => Operations.Add(new("render-freeze")),
            _gapCoordinator.StartGapFreezeCaptureForCurrentTrack,
            _gapCoordinator.LoadPreviousTrackFinalFrameForGapFreeze));
    }

    public PlaylistState Playlist { get; } = new();
    public List<ScenarioMpvOperation> Operations { get; } = [];
    public SyncMode Mode { get; set; } = SyncMode.Continue;
    public bool SyncEnabled { get; set; } = true;
    public bool IsSeeking { get; private set; }
    public bool IsMonitoring { get; set; } = true;
    public GapBehavior GapBehavior { get; set; } = GapBehavior.Freeze;
    public LtcSignalLossMode SignalLossMode { get; set; } = LtcSignalLossMode.Stop;
    public bool IsPaused => _playback.IsPaused;
    public bool IsGapActive => !_gap.IsInactive;
    public Guid? LoadedTrackId => _loadedTrackId;

    public PlaylistTrack AddTrack(string name, double timelineIn, double duration = 5)
    {
        var track = new PlaylistTrack(
            Guid.NewGuid(), $"C:/{name}.mp4", name,
            TimeSpan.Zero, null, TimeSpan.FromSeconds(timelineIn),
            TimeSpan.FromSeconds(duration), TimeSpan.Zero, 25, true);
        Playlist.Tracks.Add(track);
        if (Playlist.CurrentIndex < 0)
            Playlist.Select(0);
        return track;
    }

    public void SupplyLtc(double seconds)
    {
        LtcSignalLossAction signalAction = _signalLoss.ObserveValidFrame(
            _monotonicMilliseconds, SignalContext());
        ApplySignalLossAction(signalAction);
        if (_signalLoss.ShouldSuppressSync)
            return;

        ApplySync(seconds);
    }

    public void Tick100Milliseconds()
    {
        _monotonicMilliseconds += 100;
        ApplySignalLossAction(_signalLoss.Evaluate(_monotonicMilliseconds, SignalContext()));
    }

    public void ManualPlay() => SetPaused(false);
    public void ManualPause() => SetPaused(true);
    public void BeginSeekBarInteraction() => IsSeeking = true;
    public void EndSeekBarInteraction(double target)
    {
        IsSeeking = false;
        Seek(target);
    }

    public void AdvancePlayback(double seconds, long renderedFrames = 1)
    {
        _playbackSeconds = seconds;
        _renderedFrames += renderedFrames;
    }

    public void CompleteFreezeCapture()
    {
        _gap.OnFreezeComplete(_loadedTrackId);
        Operations.Add(new("render-freeze"));
    }

    private void ApplySync(double ltcSeconds)
    {
        if (!SyncEnabled || IsSeeking)
            return;

        if (Mode == SyncMode.Single)
        {
            var coordinator = new SingleModeSyncCoordinator(
                _syncService,
                new SingleModeSyncEffects(
                    GetTimePos: () => (0, _playbackSeconds),
                    BuildPlaybackState: playback => new SyncPlaybackState(
                        SyncEnabled, Playlist.Current != null, IsSeeking, playback,
                        _durationSeconds, _videoFps, 25),
                    SeekTo: Seek));
            coordinator.Apply(ltcSeconds);
            return;
        }

        if (_gap.CurrentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
            return;

        TimelineQueryResult result = Playlist.FindTrackAtTimelinePosition(ltcSeconds);
        switch (result.Status)
        {
            case TimelineQueryStatus.OnTrack:
                _continueCoordinator.Handle(result, ltcSeconds);
                break;
            case TimelineQueryStatus.Gap:
                GapEnterAction action = _gap.DecideGapEnter(
                    result, GapBehavior, _loadedTrackId, _videoFps, _durationSeconds);
                _gapDispatcher.Execute(action, result);
                break;
            case TimelineQueryStatus.NoTracks:
                _gapCoordinator.HandleNoTracks();
                break;
        }
    }

    private bool LoadFile(string path, double start)
    {
        Operations.Add(new("loadfile", start, path));
        _playbackSeconds = start;
        return true;
    }

    private bool Seek(double target)
    {
        Operations.Add(new("seek", target));
        _playbackSeconds = target;
        return true;
    }

    private void SetPaused(bool paused)
    {
        _playback.SetPaused(paused);
        Operations.Add(new("pause", Text: paused ? "yes" : "no"));
    }

    private LtcSignalLossContext SignalContext() => new(
        SignalLossMode, SyncEnabled, IsMonitoring, IsGapActive, IsPaused);

    private void ApplySignalLossAction(LtcSignalLossAction action)
    {
        if (action == LtcSignalLossAction.Pause)
            SetPaused(true);
        else if (action == LtcSignalLossAction.ResumeAndSync)
            SetPaused(false);
    }
}
