namespace TimecodeSyncPlayer.Tests.Integration;

internal sealed record ScenarioMpvOperation(string Name, double? Value = null, string? Text = null);
internal sealed record ScenarioLtcDisplayState(
    string FormatText,
    string TimecodeForeground,
    string PauseReason = "");

internal enum ScenarioRenderSurface
{
    Video,
    Black,
    Freeze
}

/// <summary>
/// 本番の LTC 制御を UI なしで実行し、mpv・表示境界だけを記録するシナリオ基盤。
/// </summary>
internal sealed class SyncScenarioHarness
{
    private readonly TimecodeSyncService _syncService;
    private readonly GapFreezeHandler _gap = new();
    private readonly PlaybackControlState _playback = new();
    private readonly ProjectRestorePauseState _projectRestorePauseState = new();
    private readonly ContinueOnTrackCoordinator _continueCoordinator;
    private readonly GapEnterCoordinator _gapCoordinator;
    private readonly AudioControlCoordinator _audioControlCoordinator;

    private long _monotonicMilliseconds = 10_000;
    private long _renderedFrames;
    private Guid? _loadedTrackId;
    private double _playbackSeconds = 1;
    private double _durationSeconds = 5;
    private double _videoFps = 25;
    private bool _renderVideoOnNextSeek;

    public SyncScenarioHarness(TimeProvider? timeProvider = null, bool enableCorrection = false)
    {
        _syncService = new(new SyncDecisionEngine(), new TimecodeSyncSeekState(), timeProvider);
        _audioControlCoordinator = new AudioControlCoordinator(
            new AudioControlState(isMuted: false, volume: 100),
            new AudioControlEffects(
                SetPropertyString: (name, value) =>
                {
                    RecordMpvProperty(name, value);
                    return 0;
                },
                ApplyUi: _ => { },
                Persist: _ => { }));
        _continueCoordinator = new ContinueOnTrackCoordinator(
            _syncService,
            new FileLoadStabilityLogState(TimeSpan.FromSeconds(1)),
            new ContinueOnTrackEffects(
                PeekGapExit: () => _gap.PeekGapExit(),
                IsPlaybackPaused: () => IsPaused,
                ClearGapFreezeFrame: () => { _gap.ClearCachedFrameInfo(); Operations.Add(new("clear-freeze")); },
                DecideGapExit: () =>
                {
                    GapExitAction action = _gap.DecideGapExit();
                    if (action.Type == GapExitActionType.ResumePlayback)
                        RenderSurface = ScenarioRenderSurface.Video;
                    return action;
                },
                SeekTo: Seek,
                ResumeMpvPause: () =>
                {
                    RecordMpvProperty("pause", "no");
                    Operations.Add(new("mpv-resume", Text: "no"));
                },
                ApplyPauseState: SetPaused,
                ShowOsdBar: () =>
                {
                    RecordMpvProperty("osd-bar", "yes");
                    Operations.Add(new("osd-bar", Text: "yes"));
                },
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel,
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
                PauseForGap: () =>
                {
                    RecordMpvProperty("pause", "yes");
                    Operations.Add(new("pause-for-gap"));
                },
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
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel));

        var single = new SingleModeSyncCoordinator(
            _syncService,
            new SingleModeSyncEffects(
                GetTimePos: () => (0, _playbackSeconds),
                BuildPlaybackState: playback => new SyncPlaybackState(
                    SyncEnabled, Playlist.Current != null, IsSeeking, playback,
                    _durationSeconds, _videoFps, 25),
                SeekTo: Seek,
                GetTotalRenderedFrames: () => _renderedFrames));
        Controller = new LtcSyncController(
            Playlist, _gap, _syncService,
            new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics()),
            250, 3,
            new LtcSyncEffects(
                GetContext: () => new LtcSyncContext(
                    true, SyncEnabled, Mode, IsSeeking, IsMonitoring, IsPaused,
                    SignalLossMode, TimecodeFpsMode.Fixed25, GapBehavior,
                    _loadedTrackId, _videoFps, _durationSeconds, 250, 3),
                ApplyFrameText: (timecode, realTime) =>
                {
                    TimecodeText = timecode;
                    RealTimeText = realTime;
                },
                ApplyDisplay: RecordLtcDisplayState,
                SetMonitoring: running => IsMonitoring = running,
                SetSignalLossPaused: paused =>
                {
                    Operations.Add(new(paused ? "signal-loss-pause" : "signal-loss-resume"));
                    RecordMpvProperty("pause", paused ? "yes" : "no");
                    SetPaused(paused);
                },
                ResumeProjectRestorePause: ResumeProjectRestorePauseForSyncIfNeeded,
                ClearGapFreezeFrame: () => Operations.Add(new("clear-freeze")),
                RefreshCurrentVideoFrame: () =>
                {
                    RenderSurface = ScenarioRenderSurface.Video;
                    Seek(_playbackSeconds);
                },
                UpdateTimelinePosition: _ => { },
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel,
                RenderGapFreeze: RenderFreeze,
                ResumeGapPause: () =>
                {
                    RecordMpvProperty("pause", "no");
                    SetPaused(false);
                },
                GetSyncOffsetMilliseconds: () => SyncOffsetMilliseconds,
                GetCorrectionMode: enableCorrection ? () => CorrectionMode : null,
                GetPlaybackSeconds: enableCorrection ? () => _playbackSeconds : null,
                ApplyRateInstant: enableCorrection
                    ? rate =>
                    {
                        RateAttempts.Add(rate);
                        if (!RateApplySucceeds) return false;
                        AppliedRates.Add(rate);
                        Operations.Add(new("rate", rate));
                        return true;
                    }
                    : null,
                SeekTo: enableCorrection ? Seek : null,
                SetCorrectionStatus: enableCorrection ? text => CorrectionStatus = text : null),
            () => single, () => _continueCoordinator, () => _gapCoordinator,
            getUtcNow: timeProvider is null ? null : () => timeProvider.GetUtcNow().UtcDateTime);
    }

    public LtcSyncController Controller { get; }
    public string TimecodeText { get; private set; } = "--:--:--:--";
    public string RealTimeText { get; private set; } = "-.--- s";

    public PlaylistState Playlist { get; } = new();
    public List<ScenarioMpvOperation> Operations { get; } = [];
    public List<ScenarioLtcDisplayState> DisplayStates { get; } = [];
    public List<string> CurrentTrackLabels { get; } = [];
    public List<(string Name, string Value)> MpvPropertyWrites { get; } = [];
    public IReadOnlyList<(string Name, string Value)> AudioPropertyWrites =>
        MpvPropertyWrites.Where(write => write.Name is "mute" or "volume").ToArray();
    public AudioControlSnapshot AudioState => _audioControlCoordinator.State;
    public SyncMode Mode { get; private set; } = SyncMode.Continue;
    public bool SyncEnabled { get; private set; } = true;
    public bool IsSeeking { get; private set; }
    private bool _isMonitoring = true;
    public bool IsMonitoring
    {
        get => _isMonitoring;
        set
        {
            _isMonitoring = value;
            Controller.MonitoringChanged();
        }
    }
    private GapBehavior _gapBehavior = GapBehavior.Freeze;
    public GapBehavior GapBehavior
    {
        get => _gapBehavior;
        set
        {
            _gapBehavior = value;
            Controller.GapBehaviorChanged();
        }
    }
    public LtcSignalLossMode SignalLossMode { get; set; } = LtcSignalLossMode.Stop;

    /// <summary>T3: 全体に効く同期オフセット（ms）。プラスで映像が先行する。</summary>
    public double SyncOffsetMilliseconds { get; set; }

    /// <summary>T7: 補正を有効にしたハーネスだけが使う補正モード。</summary>
    public SyncCorrectionMode CorrectionMode { get; set; } = SyncCorrectionMode.Smooth;
    public bool RateApplySucceeds { get; set; } = true;
    public List<double> AppliedRates { get; } = [];
    public List<double> RateAttempts { get; } = [];
    public string CorrectionStatus { get; private set; } = "";

    public bool IsPaused => _playback.IsPaused;
    public bool IsGapActive => !_gap.IsInactive;
    public GapState GapState => _gap.CurrentState;
    public Guid? LoadedTrackId => _loadedTrackId;
    public bool LoadSucceeds { get; set; } = true;
    public bool SeekSucceeds { get; set; } = true;
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

    public void SupplyLtc(double seconds) =>
        Controller.ReceiveProcessedFrame(new LtcFrameProcessingResult(
            "scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
            new TimecodeFrameDiagnosticResult(TimecodeFrameDiagnosticStatus.Normal, 0, 0),
            ShouldApplySync: true, ShouldLogFps: false), _monotonicMilliseconds);

    public void Tick100Milliseconds()
    {
        _monotonicMilliseconds += 100;
        Controller.Tick(_monotonicMilliseconds);
    }

    public void Tick100Milliseconds(int count)
    {
        for (int i = 0; i < count; i++)
            Tick100Milliseconds();
    }

    public void ManualPlay()
    {
        _projectRestorePauseState.Clear();
        RecordMpvProperty("pause", "no");
        SetPaused(false);
    }

    public void ManualPause()
    {
        _projectRestorePauseState.Clear();
        RecordMpvProperty("pause", "yes");
        SetPaused(true);
    }
    public void ToggleMute() => _audioControlCoordinator.ToggleMute();
    public void SetVolume(double volume) => _audioControlCoordinator.SetVolume(volume);

    public void ManualNextTrack() => SelectAndLoadTrack(Playlist.CurrentIndex + 1);
    public void ManualPreviousTrack() => SelectAndLoadTrack(Playlist.CurrentIndex - 1);

    public void StopPlayback()
    {
        Operations.Add(new("stop-playback"));
        RecordMpvProperty("pause", "yes");
        SetPaused(true);
    }

    public void LoadCurrentFile()
    {
        if (Playlist.Current is { } current)
            LoadFile(current.FilePath, current.MediaIn.TotalSeconds);
    }

    public void ReloadProject()
    {
        Operations.Add(new("project-load"));
        StopPlayback();
        LoadCurrentFilePaused();
    }

    public void ChangeMode(SyncMode mode)
    {
        Mode = mode;
        Controller.SyncModeChanged();
    }

    public void SetSyncEnabled(bool enabled)
    {
        SyncEnabled = enabled;
        Controller.SyncEnabledChanged();
    }

    public void SelectPlaylistRow(int index)
    {
        if (Playlist.Select(index))
            Operations.Add(new("select-row", index));
    }
    public void BeginSeekBarInteraction()
    {
        Controller.CancelPendingSync();
        IsSeeking = true;
    }
    public void EndSeekBarInteraction(double target)
    {
        Controller.CancelPendingSync();
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

    private bool LoadFile(string path, double start)
    {
        Operations.Add(new("loadfile", start, path));
        if (!LoadSucceeds) return false;
        SetPaused(false);
        RenderSurface = ScenarioRenderSurface.Video;
        _renderVideoOnNextSeek = false;
        _playbackSeconds = start;
        var track = Playlist.Tracks.FirstOrDefault(t => t.FilePath == path);
        if (track != null)
        {
            _durationSeconds = track.MediaDuration.TotalSeconds;
            _videoFps = track.FrameRate ?? 25;
        }
        return true;
    }

    private void LoadCurrentFilePaused()
    {
        if (Playlist.Current is not { } current)
            return;

        Operations.Add(new("loadfile-paused", current.MediaIn.TotalSeconds, current.FilePath));
        _loadedTrackId = current.Id;
        RecordMpvProperty("pause", "yes");
        SetPaused(true);
        _projectRestorePauseState.MarkPending();
        _playbackSeconds = current.MediaIn.TotalSeconds;
    }

    private void ResumeProjectRestorePauseForSyncIfNeeded()
    {
        if (!_projectRestorePauseState.TryConsume())
            return;

        RecordMpvProperty("pause", "no");
        SetPaused(false);
        Operations.Add(new("project-restore-resume"));
    }

    private void SelectAndLoadTrack(int index)
    {
        if (!Playlist.Select(index) || Playlist.Current is not { } current)
            return;

        _loadedTrackId = current.Id;
        LoadFile(current.FilePath, current.MediaIn.TotalSeconds);
    }

    private bool Seek(double target)
    {
        Operations.Add(new("seek", target));
        if (!SeekSucceeds) return false;
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

    private void RecordMpvProperty(string name, string value) =>
        MpvPropertyWrites.Add((name, value));

    private void RecordLtcDisplayState(LtcDisplayState display, string pauseReason)
    {
        var state = new ScenarioLtcDisplayState(display.FormatText, display.TimecodeForeground, pauseReason);
        if (DisplayStates.Count == 0 || DisplayStates[^1] != state)
            DisplayStates.Add(state);
    }

    private void RecordCurrentTrackLabel()
    {
        Operations.Add(new("update-label"));
        string label = PlaylistCurrentTrackLabelFormatter.Format(
            Mode,
            GapBehavior,
            _gap.IsInactive,
            Playlist.Tracks,
            Playlist.CurrentIndex,
            _loadedTrackId,
            Controller.LastLtcSeconds);
        if (CurrentTrackLabels.Count == 0 || CurrentTrackLabels[^1] != label)
            CurrentTrackLabels.Add(label);
    }
}
