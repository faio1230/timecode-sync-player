namespace TimecodeSyncPlayer.Tests.Integration;

internal sealed record ScenarioMpvOperation(string Name, double? Value = null, string? Text = null);

internal enum ScenarioRenderSurface
{
    Video,
    Black,
    Freeze
}

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
    private bool _renderVideoOnNextSeek;

    public SyncScenarioHarness()
    {
        _continueCoordinator = new ContinueOnTrackCoordinator(
            _syncService,
            new FileLoadStabilityLogState(TimeSpan.FromSeconds(1)),
            new ContinueOnTrackEffects(
                DecideGapExit: () =>
                {
                    GapExitAction action = _gap.DecideGapExit();
                    _renderVideoOnNextSeek = action.Type == GapExitActionType.ResumePlayback;
                    return action;
                },
                SeekTo: Seek,
                ResumeMpvPause: () => Operations.Add(new("mpv-resume", Text: "no")),
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
                IsPlaybackPaused: () => IsPaused,
                PauseForGap: () => Operations.Add(new("pause-for-gap")),
                ApplyPauseState: SetPaused,
                RenderBlack: RenderBlack,
                RenderGapFreeze: RenderFreeze,
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
            RenderFreeze,
            _gapCoordinator.StartGapFreezeCaptureForCurrentTrack,
            _gapCoordinator.LoadPreviousTrackFinalFrameForGapFreeze));
    }

    public PlaylistState Playlist { get; } = new();
    public List<ScenarioMpvOperation> Operations { get; } = [];
    public SyncMode Mode { get; private set; } = SyncMode.Continue;
    public bool SyncEnabled { get; private set; } = true;
    public bool IsSeeking { get; private set; }
    public bool IsMonitoring { get; set; } = true;
    public GapBehavior GapBehavior { get; set; } = GapBehavior.Freeze;
    public LtcSignalLossMode SignalLossMode { get; set; } = LtcSignalLossMode.Stop;
    public bool IsPaused => _playback.IsPaused;
    public bool IsGapActive => !_gap.IsInactive;
    public GapState GapState => _gap.CurrentState;
    public Guid? LoadedTrackId => _loadedTrackId;
    public double PlaybackSeconds => _playbackSeconds;
    public ScenarioRenderSurface RenderSurface { get; private set; } = ScenarioRenderSurface.Video;

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

    public void Tick100Milliseconds(int count)
    {
        for (int i = 0; i < count; i++)
            Tick100Milliseconds();
    }

    public void ManualPlay() => SetPaused(false);
    public void ManualPause() => SetPaused(true);

    public void ChangeMode(SyncMode mode)
    {
        Mode = mode;
        ExitGapStateForManualControlIfNeeded();
    }

    public void SetSyncEnabled(bool enabled)
    {
        SyncEnabled = enabled;
        ExitGapStateForManualControlIfNeeded();
    }

    public void SelectPlaylistRow(int index)
    {
        if (Playlist.Select(index))
            Operations.Add(new("select-row", index));
    }
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
        RenderFreeze();
    }

    public void ArrangeGapStateForModel(GapState state)
    {
        _gap.CurrentState = state;
        RenderSurface = state switch
        {
            GapState.BlackFrameActive or GapState.ForceBlack => ScenarioRenderSurface.Black,
            GapState.FreezeComplete => ScenarioRenderSurface.Freeze,
            _ => ScenarioRenderSurface.Video,
        };
    }

    public IReadOnlyList<string> ValidateInvariants()
    {
        var violations = new List<string>();
        if (IsGapActive && (Mode != SyncMode.Continue || !SyncEnabled))
            violations.Add("active gap requires Continue + Sync ON");
        if (GapState is GapState.BlackFrameActive or GapState.ForceBlack &&
            RenderSurface != ScenarioRenderSurface.Black)
            violations.Add("black gap state requires black rendering");
        if (GapState == GapState.FreezeComplete && RenderSurface != ScenarioRenderSurface.Freeze)
            violations.Add("completed freeze requires freeze rendering");
        if (GapState == GapState.Inactive && RenderSurface != ScenarioRenderSurface.Video)
            violations.Add("inactive gap requires video rendering");
        return violations;
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
        if (_renderVideoOnNextSeek)
        {
            RenderSurface = ScenarioRenderSurface.Video;
            _renderVideoOnNextSeek = false;
        }
        return true;
    }

    private void RenderBlack()
    {
        RenderSurface = ScenarioRenderSurface.Black;
        Operations.Add(new("render-black"));
    }

    private void RenderFreeze()
    {
        RenderSurface = ScenarioRenderSurface.Freeze;
        Operations.Add(new("render-freeze"));
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
        {
            Operations.Add(new("signal-loss-pause"));
            SetPaused(true);
        }
        else if (action == LtcSignalLossAction.ResumeAndSync)
        {
            Operations.Add(new("signal-loss-resume"));
            SetPaused(false);
        }
    }

    private void ExitGapStateForManualControlIfNeeded()
    {
        if (!GapStateExitPolicy.ShouldExit(SyncEnabled, Mode, IsGapActive))
            return;

        _gap.ResetAll();
        Operations.Add(new("clear-freeze"));
        RenderSurface = ScenarioRenderSurface.Video;
        Seek(_playbackSeconds);
    }
}
