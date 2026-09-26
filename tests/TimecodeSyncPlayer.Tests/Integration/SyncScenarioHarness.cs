using System.Globalization;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Tests.Integration;

internal sealed record ScenarioPlaybackOperation(string Name, double? Value = null, string? Text = null);

/// <summary>
/// v0.5.4 C4: 仮想時刻つきの観測イベント（設計: docs/design/v0.5.4-scenario-layer.md §2-4）。
/// tick・LTC フレーム・シーク・一時停止・ロードを時刻で並べ、指標の計算に使う。
/// </summary>
internal sealed record ScenarioEvent(long AtMilliseconds, string Kind, string Detail, double? Value = null);

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
    private readonly GapFreezeHandler _gap;
    private readonly ScenarioClock? _scenarioClock;
    private readonly ScenarioPlayback _playback = new(positionSeconds: 1, durationSeconds: 5, fps: 25);
    private readonly ProjectRestorePauseState _projectRestorePauseState = new();
    private readonly ContinueOnTrackCoordinator _continueCoordinator;
    private readonly GapEnterCoordinator _gapCoordinator;
    private readonly AudioControlCoordinator _audioControlCoordinator;

    private long _monotonicMilliseconds = 10_000;

    /// <summary>
    /// v0.5.4 C1: ScenarioClock があるときは同じ時計の単調ミリ秒を返す。旧 ctor では従来どおり
    /// Tick100Milliseconds が進める内部値（10_000 起点）を使う。
    /// </summary>
    private long MonotonicMilliseconds => _scenarioClock?.MonotonicMilliseconds ?? _monotonicMilliseconds;

    private long _renderedFrames;
    private Guid? _loadedTrackId;

    public SyncScenarioHarness(TimeProvider? timeProvider = null, bool enableCorrection = false,
        bool? sampleClockEnabled = null, Func<long>? getQpc = null, ScenarioClock? scenarioClock = null)
    {
        if (scenarioClock is not null && timeProvider is not null)
            throw new ArgumentException("timeProvider と scenarioClock は同時に指定しない");
        if (scenarioClock is not null && getQpc is not null)
            throw new ArgumentException("getQpc と scenarioClock は同時に指定しない");

        // v0.5.4 C1: ScenarioClock は UTC・単調ミリ秒・QPC を 1 つにまとめる。旧 ctor
        // （ManualTimeProvider + getQpc）はそのまま使える。
        _scenarioClock = scenarioClock;
        // C3: LTC の台本。開始時刻は harness の単調ミリ秒に揃える（Controller は構築後なので遅延参照）。
        // Controller はコンストラクタの後半で代入される（この経路はフレーム発行時＝代入後にしか
        // 呼ばれないため null 免除で参照する）。
        Ltc = new LtcScript(
            (frame, at) =>
            {
                Controller!.ReceiveProcessedFrame(frame, at);
                RecordEvent("ltc-frame", frame.ResolvedSeconds, frame.Diagnostic.Status.ToString(), at);
            },
            startMilliseconds: scenarioClock?.MonotonicMilliseconds ?? _monotonicMilliseconds);
        // C2: 仮想時計が進むと偽プレイヤーの位置・着地・ロード・尺の到着も進む。
        if (scenarioClock is not null)
            scenarioClock.Advanced += delta => _playback.AdvanceTime(delta);
        // C3: 台本は時計の進みに合わせてフレームを発行する（偽プレイヤーの後、Tick の前）。
        if (scenarioClock is not null)
            scenarioClock.Advanced += delta => Ltc.AdvanceTime(delta);
        TimeProvider? effectiveTimeProvider = scenarioClock ?? timeProvider;
        Func<long>? effectiveGetQpc = scenarioClock is null ? getQpc : () => scenarioClock.Qpc;
        _gap = scenarioClock is null ? new GapFreezeHandler() : new GapFreezeHandler(scenarioClock);

        // D37-a: ゲートの窓・変化量の判定に使う時計。ManualTimeProvider があれば同じ時計に
        // 揃えて、テスト内の時間（clock.Advance / Tick100Milliseconds）で決定的にする。
        _syncService = new(
            effectiveTimeProvider is null
                ? new SyncDecisionEngine()
                : new SyncDecisionEngine(new SyncDecisionOptions(), null,
                    () => effectiveTimeProvider.GetUtcNow().ToUnixTimeMilliseconds() / 1000.0),
            new TimecodeSyncSeekState(), effectiveTimeProvider);
        // C4: サービスが同期シークを発行した時点（全経路。着地シークも含む）。
        _syncService.SeekIssued += () => RecordEvent("service-seek");
        _audioControlCoordinator = new AudioControlCoordinator(
            new AudioControlState(isMuted: false, volume: 100),
            new AudioControlEffects(
                SetVolume: volume => RecordPlaybackProperty("volume", volume.ToString("0.###", CultureInfo.InvariantCulture)),
                SetMute: mute => RecordPlaybackProperty("mute", mute ? "yes" : "no"),
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
                    return action;
                },
                // C4: Continue の同期シーク（段 0 の「同期シーク」に対応）。
                SeekTo: target =>
                {
                    RecordEvent("sync-seek", target);
                    return Seek(target);
                },
                ResumePlayback: () =>
                {
                    RecordPlaybackProperty("pause", "no");
                    Operations.Add(new("playback-resume", Text: "no"));
                },
                ApplyPauseState: SetPaused,
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel,
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => _loadedTrackId = id,
                LoadFile: LoadFile,
                GetTotalRenderedFrames: () => _renderedFrames,
                ReadPosition: () => new SyncPositionRead(true, _playback.PositionSeconds),
                BuildPlaybackState: playback => new SyncPlaybackState(
                    SyncEnabled,
                    Playlist.Current != null,
                    IsSeeking,
                    playback,
                    _playback.DurationSeconds,
                    _playback.Fps,
                    TimecodeFps: 25),
                IsNativeSeeking: () => NativeSeeking));

        _gapCoordinator = new GapEnterCoordinator(
            _gap,
            new GapEnterEffects(
                ResetEndAdvanceTriggered: () => { },
                IsPlaybackPaused: () => IsPaused,
                PauseForGap: () =>
                {
                    RecordPlaybackProperty("pause", "yes");
                    Operations.Add(new("pause-for-gap"));
                },
                ApplyPauseState: SetPaused,
                ClearGapFreezeFrame: () => Operations.Add(new("clear-freeze")),
                SeekTo: Seek,
                GetPlayerDuration: () => (0, _playback.DurationSeconds),
                IsPlayerReady: () => true,
                LoadPausedAt: (path, target) =>
                {
                    Operations.Add(new("load-paused", target, path));
                    return new GapLoadCommandResult(PlaybackResult.Ok, PlaybackResult.Ok);
                },
                ResetPlayerStateForNewTrack: () => { },
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => _loadedTrackId = id,
                GetDuration: () => _playback.DurationSeconds,
                SetDuration: duration => _playback.SetDuration(duration),
                GetFps: () => _playback.Fps,
                SetFps: fps => _playback.SetFps(fps),
                GetGapBehavior: () => GapBehavior,
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel));

        var single = new SingleModeSyncCoordinator(
            _syncService,
            new SingleModeSyncEffects(
                ReadPosition: () => new SyncPositionRead(true, _playback.PositionSeconds),
                BuildPlaybackState: playback => new SyncPlaybackState(
                    SyncEnabled, Playlist.Current != null, IsSeeking, playback,
                    _playback.DurationSeconds, _playback.Fps, 25,
                    MediaInSeconds: MediaInSeconds, MediaOutSeconds: MediaOutSeconds),
                // C4: Single の同期シーク（段 0 の「同期シーク」に対応）。
                SeekTo: target =>
                {
                    RecordEvent("sync-seek", target);
                    return Seek(target);
                },
                GetTotalRenderedFrames: () => _renderedFrames,
                IsNativeSeeking: () => NativeSeeking,
                // D33: 終端ホールドの pause/resume を記録する。解除は MainWindow と同じ条件
                // （v0.5.2 段 2g-2: ほかの持ち主が止めていれば再開しない）。
                SetEndHold: held =>
                {
                    Operations.Add(new(held ? "clip-end-hold" : "clip-end-release"));
                    if (held)
                    {
                        SetPaused(true);
                        return;
                    }

                    PauseOwners otherOwners = SyncRules.CollectOtherPauseOwners(
                        Controller!.IsSignalLossPauseOwned,
                        _gap.IsPauseOwnedByGap,
                        _projectRestorePauseState.IsPending);
                    if (SyncRules.ShouldResumeOnBoundaryHoldRelease(otherOwners))
                        SetPaused(false);
                    else
                        Operations.Add(new("clip-end-stays-paused"));
                },
                // D35-b: 境界ホールド解除時に保留シークと保持着地のラッチを解除する。
                // Controller はコンストラクタの後半で代入され、この経路はフレーム処理時
                // （代入後）にしか呼ばれないため null 免除で参照する。
                OnBoundaryHoldReleased: () => Controller!.NotifyClipBoundaryHoldReleased()));
        Controller = new LtcSyncController(
            Playlist, _gap, _syncService,
            new LtcFrameProcessor(new TimecodeFpsSelector(), new TimecodeFrameDiagnostics()),
            250, 3,
            new LtcSyncEffects(
                GetContext: () => new LtcSyncContext(
                    true, SyncEnabled, Mode, IsSeeking, IsMonitoring, IsPaused,
                    SignalLossMode, TimecodeFpsMode.Fixed25, GapBehavior,
                    _loadedTrackId, _playback.Fps, _playback.DurationSeconds, 250, 3,
                    MediaInSeconds, MediaOutSeconds),
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
                    RecordPlaybackProperty("pause", paused ? "yes" : "no");
                    SetPaused(paused);
                },
                ResumeProjectRestorePause: ResumeProjectRestorePauseForSyncIfNeeded,
                ClearGapFreezeFrame: () => Operations.Add(new("clear-freeze")),
                RefreshCurrentVideoFrame: () =>
                {
                    Seek(_playback.PositionSeconds);
                },
                UpdateTimelinePosition: _ => { },
                UpdateCurrentTrackLabel: RecordCurrentTrackLabel,
                ResumeGapPause: () =>
                {
                    RecordPlaybackProperty("pause", "no");
                    SetPaused(false);
                },
                GetSyncOffsetMilliseconds: () => SyncOffsetMilliseconds,
                GetCorrectionMode: enableCorrection ? () => CorrectionMode : null,
                GetPlaybackSeconds: enableCorrection ? () => _playback.PositionSeconds : null,
                GetTotalRenderedFrames: () => _renderedFrames,
                ApplyRateInstant: enableCorrection
                    ? rate =>
                    {
                        RateAttempts.Add(rate);
                        if (!_playback.SetRateInstant(rate).Success) return false;
                        AppliedRates.Add(rate);
                        Operations.Add(new("rate", rate));
                        return true;
                    }
                    : null,
                // C4: controller からのシーク（保持着地・Jump 補正。段 0 の「着地シーク」を含む）。
                SeekTo: enableCorrection
                    ? target =>
                    {
                        RecordEvent("landing-seek", target);
                        return Seek(target);
                    }
                    : null,
                SetCorrectionStatus: enableCorrection ? text => CorrectionStatus = text : null,
                IsPlaybackPositionUnstable: () => PlaybackPositionUnstable,
                // v0.5.3 段 3i: 信号断の一時停止を解いたときの再開判定（§6 の 9）。
                // MainWindow と同じ組み立て関数を使う。
                GetOtherPauseOwners: () => SyncRules.CollectPauseOwnersExceptSignalLoss(
                    single.IsBoundaryHeld,
                    _gap.IsPauseOwnedByGap,
                    _projectRestorePauseState.IsPending)),
            () => single, () => _continueCoordinator, () => _gapCoordinator,
            getUtcNow: effectiveTimeProvider is null ? null : () => effectiveTimeProvider.GetUtcNow().UtcDateTime,
            sampleClockEnabled: sampleClockEnabled,
            getQpc: effectiveGetQpc);
        Single = single;
    }

    public LtcSyncController Controller { get; }

    /// <summary>C3: LTC 入力の台本（Normal／Duplicate／Jump／無音／Raw）。</summary>
    public LtcScript Ltc { get; }

    /// <summary>v0.5.2 段 0: Single の同期コーディネーター（ラッチの写しを読むため）。</summary>
    public SingleModeSyncCoordinator Single { get; }
    public string TimecodeText { get; private set; } = "--:--:--:--";
    public string RealTimeText { get; private set; } = "-.--- s";

    /// <summary>D37-c: 学習済みシーク所要を用意するテスト用（保留の状態を直接操作する）。</summary>
    public ITimecodeSyncSeekState SeekState => _syncService.SeekState;

    /// <summary>D37-f: スキャンから渡すシーク所要の見積もりを、テストから直接設定する。</summary>
    public TimecodeSyncService SyncService => _syncService;

    public PlaylistState Playlist { get; } = new();
    public List<ScenarioPlaybackOperation> Operations { get; } = [];

    /// <summary>C4: 時刻つきの観測イベント。指標はここから計算する（記録のみ）。</summary>
    public List<ScenarioEvent> Events { get; } = [];
    public List<ScenarioLtcDisplayState> DisplayStates { get; } = [];
    public List<string> CurrentTrackLabels { get; } = [];
    public List<(string Name, string Value)> PlaybackPropertyWrites { get; } = [];
    public IReadOnlyList<(string Name, string Value)> AudioPropertyWrites =>
        PlaybackPropertyWrites.Where(write => write.Name is "mute" or "volume").ToArray();
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

    /// <summary>D33: Single の LTC → 素材位置の範囲（D29 と同じ）。既定は MediaIn=0 / MediaOut=尺。</summary>
    public double MediaInSeconds { get; set; }
    public double? MediaOutSeconds { get; set; }

    /// <summary>T7: 補正を有効にしたハーネスだけが使う補正モード。</summary>
    public SyncCorrectionMode CorrectionMode { get; set; } = SyncCorrectionMode.Smooth;
    public bool RateApplySucceeds
    {
        get => _playback.RateApplySucceeds;
        set => _playback.RateApplySucceeds = value;
    }
    public List<double> AppliedRates { get; } = [];

    /// <summary>0.4.8: 再生位置が直近に後退した（位置を補正の入力として信用しない）状態を与える。</summary>
    public bool PlaybackPositionUnstable { get; set; }
    public List<double> RateAttempts { get; } = [];
    public string CorrectionStatus { get; private set; } = "";

    public bool IsPaused => _playback.Paused;
    public bool IsGapActive => !_gap.IsInactive;
    public GapState GapState => _gap.CurrentState;
    public Guid? LoadedTrackId => _loadedTrackId;
    public bool LoadSucceeds
    {
        get => _playback.LoadSucceeds;
        set => _playback.LoadSucceeds = value;
    }
    public bool SeekSucceeds
    {
        get => _playback.SeekSucceeds;
        set => _playback.SeekSucceeds = value;
    }
    public bool NativeSeeking { get; set; }
    public double PlaybackSeconds => _playback.PositionSeconds;

    /// <summary>C2: 偽の再生 API（着地の遅れ・ロード・尺の到着・レートの設定に使う）。</summary>
    public ScenarioPlayback Playback => _playback;
    /// <summary>
    /// 合成層が描く面。段 3 以降は CPU 描画の副作用ではなく Gap 状態から決まる
    /// （GPU 合成層が出力モードとして描く）。
    /// </summary>
    public ScenarioRenderSurface RenderSurface => _gap.CurrentState switch
    {
        GapState.BlackFrameActive or GapState.ForceBlack => ScenarioRenderSurface.Black,
        GapState.FreezeComplete => ScenarioRenderSurface.Freeze,
        _ => ScenarioRenderSurface.Video,
    };

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
        SupplyLtc(seconds, TimecodeFrameDiagnosticStatus.Normal, shouldApplySync: true);

    /// <summary>
    /// D31-b: 解読は続いているが値が進まない保持（Duplicate）として供給する。進行の時計を
    /// 進めないため、タイムアウト後は信号停止（保持）の判定へ伝わる。
    /// </summary>
    public void SupplyHeldLtc(double seconds) =>
        SupplyLtc(seconds, TimecodeFrameDiagnosticStatus.Duplicate, shouldApplySync: false);

    private void SupplyLtc(double seconds, TimecodeFrameDiagnosticStatus status, bool shouldApplySync)
    {
        Controller.ReceiveProcessedFrame(new LtcFrameProcessingResult(
            "scenario", $"{seconds:F3} s", seconds, 25, "fps: 25",
            new TimecodeFrameDiagnosticResult(status, 0, 0),
            ShouldApplySync: shouldApplySync, ShouldLogFps: false), MonotonicMilliseconds);
        RecordEvent("ltc-frame", seconds, status.ToString());
    }

    /// <summary>
    /// T2: サンプル時計の検証用。フレーム終端 QPC を持つフレームとして渡す（秒は 25fps の
    /// フレーム境界に合わせる）。
    /// </summary>
    public void SupplyLtcFrame(double seconds, long frameEndTimestamp, long callbackTimestamp = 0)
    {
        int frame = (int)Math.Round(seconds * 25.0);
        var timecode = new LtcTimecode(
            frame / (25 * 3600), (frame / (25 * 60)) % 60, (frame / 25) % 60, frame % 25, false);
        Controller.ReceiveFrame(
            new LtcFrameReceivedEventArgs(timecode, 25, seconds, frameEndTimestamp, callbackTimestamp),
            MonotonicMilliseconds);
        RecordEvent("ltc-frame", seconds, "Frame");
    }

    /// <summary>
    /// C4: 任意のミリ秒だけ仮想時間を進める（40ms の LTC グリッドに合わせたいとき用）。
    /// 進めた後は Tick100Milliseconds と同じ順で controller の Tick を呼ぶ。
    /// </summary>
    public void AdvanceMilliseconds(int milliseconds)
    {
        if (_scenarioClock is null)
        {
            _monotonicMilliseconds += milliseconds;
            Ltc.AdvanceTime(TimeSpan.FromMilliseconds(milliseconds));
        }
        else
        {
            _scenarioClock.AdvanceMilliseconds(milliseconds);
            // C4: tick は合成の拍でもある。ロード解除の成立（描画が進んだか）に必要。
            // 旧経路は従来どおり AdvancePlayback でしか描画フレームを足さない。
            _renderedFrames++;
        }

        Controller.Tick(MonotonicMilliseconds);
        RecordEvent("tick", _playback.PositionSeconds, RenderSurface.ToString());
    }

    public void Tick100Milliseconds() => AdvanceMilliseconds(100);

    public void Tick100Milliseconds(int count)
    {
        for (int i = 0; i < count; i++)
            Tick100Milliseconds();
    }

    public void ManualPlay()
    {
        _projectRestorePauseState.Clear();
        RecordPlaybackProperty("pause", "no");
        SetPaused(false);
    }

    public void ManualPause()
    {
        _projectRestorePauseState.Clear();
        RecordPlaybackProperty("pause", "yes");
        SetPaused(true);
    }
    public void ToggleMute() => _audioControlCoordinator.ToggleMute();
    public void SetVolume(double volume) => _audioControlCoordinator.SetVolume(volume);

    public void ManualNextTrack() => SelectAndLoadTrack(Playlist.CurrentIndex + 1);
    public void ManualPreviousTrack() => SelectAndLoadTrack(Playlist.CurrentIndex - 1);

    public void StopPlayback()
    {
        Operations.Add(new("stop-playback"));
        RecordPlaybackProperty("pause", "yes");
        SetPaused(true);
    }

    public void LoadCurrentFile()
    {
        // v0.5.3 段 3g: ギャップの読み込み（LoadPausedAt / GapFreezePathGuard）を再現し、
        // 読み込みの後にロード中の印を立てない口を通す。
        if (Playlist.Current is { } current && LoadFile(current.FilePath, current.MediaIn.TotalSeconds))
            _syncService.BeginGapFreezeLoad("load-paused-at");
    }

    /// <summary>D27-b: 手動ロード（次/前/プレイリスト）でアプリ側が立てるロードゲートを再現する。</summary>
    public void BeginManualFileLoad() => _syncService.BeginFileLoad(0, _renderedFrames);

    /// <summary>テスト用: 尺（clamp の着地先）を差し替える。</summary>
    public void SetDurationSeconds(double seconds) => _playback.SetDuration(seconds);

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
        _playback.SetPosition(seconds);
        _renderedFrames += renderedFrames;
    }

    public void CompleteFreezeCapture()
    {
        _gap.OnFreezeComplete(_loadedTrackId);
    }

    public void ArrangeGapStateForModel(GapState state)
    {
        _gap.CurrentState = state;
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
        RecordEvent("load", start, path);
        if (!_playback.Load(path, start, paused: false).Success) return false;
        SetPaused(false);
        var track = Playlist.Tracks.FirstOrDefault(t => t.FilePath == path);
        if (track != null)
            _playback.SetMedia(track.MediaDuration.TotalSeconds, track.FrameRate ?? 25);
        return true;
    }

    private void LoadCurrentFilePaused()
    {
        if (Playlist.Current is not { } current)
            return;

        Operations.Add(new("loadfile-paused", current.MediaIn.TotalSeconds, current.FilePath));
        RecordEvent("load", current.MediaIn.TotalSeconds, current.FilePath);
        _loadedTrackId = current.Id;
        RecordPlaybackProperty("pause", "yes");
        SetPaused(true);
        _projectRestorePauseState.MarkPending();
        _playback.Load(current.FilePath, current.MediaIn.TotalSeconds, paused: true);
    }

    private void ResumeProjectRestorePauseForSyncIfNeeded()
    {
        if (!_projectRestorePauseState.TryConsume())
            return;

        RecordPlaybackProperty("pause", "no");
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
        RecordEvent("seek", target);
        return _playback.Seek(target).Success;
    }

    private void SetPaused(bool paused)
    {
        _playback.SetPaused(paused);
        Operations.Add(new("pause", Text: paused ? "yes" : "no"));
        RecordEvent(paused ? "pause" : "resume");
    }

    private void RecordPlaybackProperty(string name, string value) =>
        PlaybackPropertyWrites.Add((name, value));

    /// <summary>C4: 時刻つきの観測イベントを足す（省略時は現在の仮想時刻）。</summary>
    private void RecordEvent(string kind, double? value = null, string detail = "", long? atMilliseconds = null) =>
        Events.Add(new ScenarioEvent(atMilliseconds ?? MonotonicMilliseconds, kind, detail, value));

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
