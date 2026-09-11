using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer;

public partial class MainWindow : Window, IDisposable, IPlaybackController
{
    // ── mpv ──────────────────────────────────────────────────────
    private IntPtr            _mpv             = IntPtr.Zero;
    private DispatcherTimer?  _timer;
    private readonly PlaybackControlState _playbackControl = new();
    private readonly SeekBarInteractionController _seekBarInteraction = new();
    private readonly ISeekBarUpdateState _seekState;
    private readonly MainViewModel _vm;
    private double            _duration        = 0;
    private double            _fps             = 0;
    private bool              _metadataFetched = false;
    private string            _metaLine        = "";
    private readonly OsdUpdateState _osdUpdateState;
    private DateTime          _lastSeekTickLogAt = DateTime.MinValue;

    // ── SW レンダー ────────────────────────────────────────────────
    private readonly RenderSession _renderSession;
    private readonly IDisplayCatalog _displayCatalog = new NativeDisplayCatalog();
    private FullscreenOutputWindow? _fullscreenWindow;
    private bool _isRefreshingDisplays;
    private readonly PlaybackPerformanceStats _playbackPerformanceStats;

    private readonly IMediaDurationReader _mediaDurationReader;
    private readonly PlaylistDurationBackfillService _playlistDurationBackfillService;
    private readonly PlaylistDurationBackfillCoordinator _playlistDurationBackfillCoordinator;
    private readonly PlaylistLoadCoordinator _playlistLoadCoordinator;
    private readonly MpvStartupPropertyApplier _mpvStartupPropertyApplier;
    private readonly MpvSessionInitializer _mpvSessionInitializer;
    private readonly ProjectLoadApplicator _projectLoadApplicator;
    private readonly ProjectSaveExecutor _projectSaveExecutor;
    private readonly ProjectFileCoordinator _projectFileCoordinator;
    private readonly IMpvApi _mpvApi;
    private readonly AudioControlCoordinator _audioControlCoordinator;

    // ── Spout ─────────────────────────────────────────────────────
    private readonly ISpoutOutput _spoutOutput;

    // ── GPU 出力（OutputBackend=Gpu 時のみ） ───────────────────────
    private readonly OutputEngine? _outputEngine;
    private WriteableBitmap? _outputPreviewBitmap;

    // ── LTC ───────────────────────────────────────────────────────
    private readonly LtcSyncController _ltcSyncController;
    private readonly ILtcMonitor _ltcMonitor;
    private readonly TimecodeSyncService _syncService;
    private readonly FileLoadStabilityLogState _fileLoadStabilityLogState = new(TimeSpan.FromSeconds(1));
    private readonly GapPlaybackCommandExecutor _gapPlaybackCommandExecutor;
    private volatile bool _disposed;
    private readonly GapFreezeHandler _gapFreezeHandler;
    private bool _isRefreshingLtcDevices;

    // ── Constants ──────────────────────────────────────────────────

    // Seek debounce timing
    private const double SeekDebounceMs = 250.0;
    private const double LoadfileReloadDebounceMs = 1000.0;

    // MPV command strings
    private const string MpvSeekModeAbsolute = "absolute+exact";
    private const string MpvSeekModeRelative = "relative+exact";
    // MPV property values
    private const string MpvValueYes = "yes";
    private const string MpvValueNo = "no";

    // OSD settings
    private const string MpvPropertyOsdBar = "osd-bar";
    private const string MpvPropertyOsdLevel = "osd-level";
    private const string MpvPropertyOsdFontSize = "osd-font-size";

    // Playback icons
    private const string IconPause = "⏸";

    // Timer interval (ms)
    private const int TimerIntervalMs = 100;

    // Additional repeated strings
    private const string MpvCommandNoOsd = "no-osd";
    private const string TimelineOnLabel = "Timeline ON";
    private const string TimelineOffLabel = "Timeline OFF";
    private const string SyncOnLabel = "Sync ON";
    private const string SyncOffLabel = "Sync OFF";
    private const string SpoutOnLabel = "Spout ON";
    private const string SpoutOffLabel = "Spout OFF";
    private const string FullscreenOpenLabel = "FULLSCREEN";
    private const string FullscreenCloseLabel = "EXIT FULLSCREEN";

    // CLI --save-project の起動シーケンス待機時間
    private const int SaveProjectDelayMs = 3000; // playlist ロード完了の暫定待機時間

    // ── Playlist ──────────────────────────────────────────────────
    private readonly PlaylistState _playlist;
    private readonly ProjectRestorePauseState _projectRestorePauseState = new();
    private Guid?                  _loadedTrackId;
    private bool                   _endAdvanceTriggered;
    private readonly PlaylistDragDropCoordinator _playlistDragDropCoordinator;

    // ── 同期コーディネータ（遅延生成キャッシュ。ラムダは this のフィールドのみを参照するため
    //    呼び出しごとの再生成は不要。RenderFrameWorker 等と同様、初回呼び出し時に確定する） ──
    private SingleModeSyncCoordinator?  _singleModeSyncCoordinator;
    private ContinueOnTrackCoordinator? _continueOnTrackCoordinator;
    private GapEnterCoordinator?        _gapEnterCoordinator;
    private PlaybackOperationsCoordinator? _playbackOperationsCoordinator;
    private WindowLoadedCoordinator? _windowLoadedCoordinator;

    // ── Timeline ──────────────────────────────────────────────────
    private TimelinePanel? _timelinePanel;

    // ── 起動 ─────────────────────────────────────────────────────

    public MainWindow(
        ILtcMonitor ltcMonitor,
        PlaylistState playlist,
        TimecodeSyncService syncService,
        LtcFrameProcessor ltcFrameProcessor,
        GapPlaybackCommandExecutor gapPlaybackCommandExecutor,
        GapFreezeHandler gapFreezeHandler,
        AppSettingsManager settingsManager,
        ISpoutOutput spoutOutput,
        IMediaDurationReader mediaDurationReader,
        PlaylistDurationBackfillService playlistDurationBackfillService,
        PlaylistLoadCoordinator playlistLoadCoordinator,
        MpvStartupPropertyApplier mpvStartupPropertyApplier,
        MpvSessionInitializer mpvSessionInitializer,
        ProjectLoadApplicator projectLoadApplicator,
        ISeekBarUpdateState seekState,
        OsdUpdateState osdUpdateState,
        PlaybackPerformanceStats playbackPerformanceStats,
        OutputBackendState outputBackendState,
        IMpvApi mpvApi,
        IMpvRenderApi mpvRenderApi)
    {
        _ltcMonitor = ltcMonitor;
        _playlist = playlist;
        _syncService = syncService;
        _gapPlaybackCommandExecutor = gapPlaybackCommandExecutor;
        _gapFreezeHandler = gapFreezeHandler;
        _settingsManager = settingsManager;
        _showDebugOsd = settingsManager.Current.ShowDebugOsd;
        _spoutOutput = spoutOutput;
        _mediaDurationReader = mediaDurationReader;
        _playlistDurationBackfillService = playlistDurationBackfillService;
        _playlistLoadCoordinator = playlistLoadCoordinator;
        _mpvStartupPropertyApplier = mpvStartupPropertyApplier;
        _mpvSessionInitializer = mpvSessionInitializer;
        _projectLoadApplicator = projectLoadApplicator;
        _projectSaveExecutor = new ProjectSaveExecutor((path, syncMode, gapBehavior) => ProjectSerializer.SaveAsync(path, _playlist, syncMode, gapBehavior));
        _seekState = seekState;
        _osdUpdateState = osdUpdateState;
        _playbackPerformanceStats = playbackPerformanceStats;
        _mpvApi = mpvApi;

        _vm = new MainViewModel();
        _vm.Player   = new PlayerViewModel(this);
        _vm.Playlist = new PlaylistViewModel(_playlist, _mediaDurationReader);
        _vm.Sync     = new SyncViewModel(_ltcMonitor);
        _renderSession = new RenderSession(mpvRenderApi, _spoutOutput, _playbackPerformanceStats,
            () => _gapFreezeHandler.CurrentState,
            () => _vm.Sync.GapBehavior,
            action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            isGapFreezeConfirmed: () => _gapFreezeHandler.CachedTrackId.HasValue);
        _renderSession.FrameUpdate = ProcessRenderFrameUpdateAsync;
        if (outputBackendState.Effective == OutputBackend.Gpu)
        {
            // Gpu backend: プレビューは OutputEngine の読み戻しで更新し、CPU 経路のプレビューは接続しない。
            _outputEngine = new OutputEngine(new OutputEngineSettings
            {
                CanvasWidth = CanvasSettings.Default.Width,
                CanvasHeight = CanvasSettings.Default.Height,
                SenderName = ResolveOutputSenderName(),
                AdapterLuid = OutputDisplays.FindAdapterLuid(settingsManager.Current.FullscreenDisplayDeviceName),
                TestCardEnabled = OutputEngineSettings.TestCardRequested(),
                Trace = OutputTrace.Create(Environment.GetEnvironmentVariable(OutputTrace.EnvironmentVariable)),
                PreviewFrameReady = OnOutputPreviewFrame,
            });
            _renderSession.GpuFrameSink = (frame, generation, position) => _outputEngine?.SubmitFrame(frame, generation, position);
            _renderSession.PositionSecondsProvider = ReadMpvTimePos;
            Log.Information("OutputEngine: Gpu backend を開始（OutputBackend={Backend}）", outputBackendState.Decision.Requested);
            _outputEngine.Start();
        }
        else
        {
            _renderSession.PreviewBitmapChanged += bitmap => VideoImage.Source = bitmap;
        }
        _ltcSyncController = new LtcSyncController(
            _playlist, _gapFreezeHandler, _syncService, ltcFrameProcessor,
            settingsManager.Current.LtcSignalLossTimeoutMs, settingsManager.Current.LtcSignalResumeFrames,
            new LtcSyncEffects(
                GetContext: () => new LtcSyncContext(
                    _mpv != IntPtr.Zero, _vm.Sync.SyncEnabled, _vm.Sync.SyncMode,
                    _seekBarInteraction.IsSeeking, _vm.Sync.IsLtcRunning, _playbackControl.IsPaused,
                    _vm.Sync.LtcSignalLossMode, _vm.Sync.LtcFpsMode, _vm.Sync.GapBehavior,
                    _loadedTrackId, _fps, _duration,
                    _settingsManager.Current.LtcSignalLossTimeoutMs, _settingsManager.Current.LtcSignalResumeFrames),
                ApplyFrameText: (timecode, realTime) =>
                {
                    _vm.Sync.LtcTimecodeText = timecode;
                    _vm.Sync.LtcRealTimeText = realTime;
                },
                ApplyDisplay: (display, pauseReason) =>
                {
                    _vm.Sync.LtcFormatText = display.FormatText;
                    _vm.Sync.LtcTimecodeForeground = display.TimecodeForeground;
                    _vm.Sync.LtcSignalLossPauseReason = pauseReason;
                },
                SetMonitoring: running => _vm.Sync.IsLtcRunning = running,
                SetSignalLossPaused: paused =>
                {
                    _mpvApi.SetPropertyString(_mpv, "pause", paused ? MpvValueYes : MpvValueNo);
                    ApplyPauseState(paused);
                },
                ResumeProjectRestorePause: ResumeProjectRestorePauseForSyncIfNeeded,
                ClearGapFreezeFrame: () =>
                {
                    _renderSession.Invalidate();
                    _renderSession.ClearGapFreezeFrame();
                },
                RefreshCurrentVideoFrame: RefreshCurrentVideoFrame,
                UpdateTimelinePosition: seconds => _timelinePanel?.UpdatePlaybackPosition(seconds),
                UpdateCurrentTrackLabel: UpdateCurrentTrackLabel,
                RenderGapFreeze: () => _renderSession.QueueGapFrame(GapRenderFrameDecision.GapFreeze),
                ResumeGapPause: () =>
                {
                    _mpvApi.SetPropertyString(_mpv, "pause", MpvValueNo);
                    ApplyPauseState(false);
                }),
            CreateSingleModeSyncCoordinator, CreateContinueOnTrackCoordinator, CreateGapEnterCoordinator);
        var audioState = new AudioControlState(
            settingsManager.Current.IsMuted,
            settingsManager.Current.Volume);
        _audioControlCoordinator = new AudioControlCoordinator(
            audioState,
            new AudioControlEffects(
                SetPropertyString: (name, value) => _mpvApi.SetPropertyString(_mpv, name, value),
                ApplyUi: ApplyAudioControlUi,
                Persist: snapshot => _ = _settingsManager.UpdateAsync(settings => settings with
                {
                    IsMuted = snapshot.IsMuted,
                    Volume = snapshot.Volume,
                })));
        ApplyAudioControlUi(audioState.Snapshot);
        _vm.Sync.SyncModeIndex = ProjectSyncSelectionMapper.GetSyncModeIndex(settingsManager.Current.SyncMode);
        _vm.Sync.GapBehaviorIndex = ProjectSyncSelectionMapper.GetGapBehaviorIndex(settingsManager.Current.GapBehavior);
        _vm.Sync.LtcSignalLossModeIndex =
            settingsManager.Current.LtcSignalLossMode == LtcSignalLossMode.Stop ? 1 : 0;
        _playlistDragDropCoordinator = new PlaylistDragDropCoordinator(new PlaylistDragDropEffects(
            BeginDrag: track => DragDrop.DoDragDrop(PlaylistList, track, DragDropEffects.Move),
            IndexOf: track => _playlist.Tracks.IndexOf(track),
            GetTrackCount: () => _playlist.Tracks.Count,
            MoveTrack: _playlist.MoveTrack,
            SetSelectedIndex: ApplyPlaylistSelectionIndex,
            UpdateCurrentTrackLabel: UpdateCurrentTrackLabel,
            UpdatePlaylistTimelineDisplay: UpdatePlaylistTimelineDisplay));
        _playlistDurationBackfillCoordinator = new PlaylistDurationBackfillCoordinator(
            _playlistDurationBackfillService,
            new PlaylistDurationBackfillEffects(
                GetTracks: () => _playlist.Tracks,
                ApplyDurationOnUiAsync: async (trackId, duration, recalculateTimeline) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _playlist.UpdateMediaDuration(trackId, duration, recalculate: recalculateTimeline);
                        UpdatePlaylistTimelineDisplay();
                    });
                },
                HandleFailure: ex =>
                {
                    Log.Error(ex, "ReadDurationsInBackground failed");
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        MessageBox.Show("メディアのduration読み込みに失敗しました。", "エラー",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                }));
        _projectFileCoordinator = new ProjectFileCoordinator(
            new ProjectFileActionRunner(),
            new ProjectFileEffects(
                SaveAsync: path => _projectSaveExecutor.SaveAsync(path, _vm.Sync.SyncMode, _vm.Sync.GapBehavior),
                LogSaved: path =>
                {
                    RememberProjectPath(path);
                    Log.Information("プロジェクトを保存しました: {Path}", path);
                },
                HandleSaveFailure: ex =>
                {
                    Log.Error(ex, "プロジェクトの保存に失敗しました");
                    MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                },
                LoadAsync: ProjectSerializer.LoadAsync,
                ApplyProject: project => _ = Dispatcher.BeginInvoke(() =>
                {
                    RestoreLoadedProject(project);
                }),
                LogLoaded: path =>
                {
                    RememberProjectPath(path);
                    Log.Information("プロジェクトを読み込みました: {Path}", path);
                },
                HandleInvalidProject: () => MessageBox.Show("プロジェクトファイルの形式が不正です。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error),
                HandleLoadFailure: ex =>
                {
                    Log.Error(ex, "プロジェクトの読み込みに失敗しました");
                    MessageBox.Show("プロジェクトの読み込みに失敗しました。ファイルが破損している可能性があります。", "エラー",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }));
        DataContext  = _vm;

        _vm.Sync.StartLtcFailed += (_, ex) =>
        {
            Log.Warning(ex, "LTC monitor start failed");
            MessageBox.Show("LTC入力の開始に失敗しました。デバイス接続を確認してください。",
                "LTC Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _vm.Sync.SyncEnabledChanged += (_, enabled) =>
        {
            _ltcSyncController.SyncEnabledChanged();
            Log.Information("Timecode sync {State}", enabled ? "enabled" : "disabled");
        };

        _vm.Sync.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SyncViewModel.SyncMode):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        SyncMode = _vm.Sync.SyncMode,
                    });
                    _ltcSyncController.SyncModeChanged();
                    Log.Information("Sync mode changed to {Mode}", _vm.Sync.SyncMode);
                    break;
                case nameof(SyncViewModel.GapBehavior):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        GapBehavior = _vm.Sync.GapBehavior,
                    });
                    Log.Information("Gap behavior changed to {Behavior}", _vm.Sync.GapBehavior);
                    _ltcSyncController.GapBehaviorChanged();
                    break;
                case nameof(SyncViewModel.LtcFpsMode):
                    _ltcSyncController.FpsModeChanged();
                    Log.Information("LTC fps mode changed mode={Mode}", _vm.Sync.LtcFpsMode);
                    break;
                case nameof(SyncViewModel.LtcSignalLossMode):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        LtcSignalLossMode = _vm.Sync.LtcSignalLossMode,
                    });
                    Log.Information("LTC signal loss mode changed mode={Mode}", _vm.Sync.LtcSignalLossMode);
                    break;
                case nameof(SyncViewModel.IsLtcRunning):
                    _ltcSyncController.MonitoringChanged();
                    break;
            }
        };

        _vm.Playlist.TrackRemoved += () =>
        {
            SyncPlaylistSelection();
            UpdatePlaylistTimelineDisplay();
            if (_playlist.Current != null)
                LoadCurrentPlaylistTrack();
            else
            {
                StopPlayback();
                UpdateCurrentTrackLabel();
            }
        };
        _vm.Playlist.TrackMoved += () =>
        {
            ApplyPlaylistSelectionIndex(_vm.Playlist.SelectedIndex);
            UpdatePlaylistTimelineDisplay();
            UpdateCurrentTrackLabel();
        };
        _vm.Playlist.TracksCleared += () =>
        {
            _loadedTrackId = null;
            StopPlayback();
            SyncPlaylistSelection();
            UpdatePlaylistTimelineDisplay();
            UpdateCurrentTrackLabel();
        };

        InitializeComponent();
        Title = ApplicationVersion.WindowTitle;
    }

    internal MainViewModel ViewModel => _vm;

    private readonly AppSettingsManager _settingsManager;
    private readonly bool _showDebugOsd;

    private void Window_Loaded(object sender, RoutedEventArgs e)
        => CreateWindowLoadedCoordinator().Initialize();

    private WindowLoadedCoordinator CreateWindowLoadedCoordinator() =>
        _windowLoadedCoordinator ??= new(new WindowLoadedEffects(
            InitializeUi: InitializeWindowLoadedUi,
            // CLI 引数: --open と --playlist に対応（--vo は SW レンダーに切り替えたため不要）
            ParseLaunchArguments: () => AppLaunchArguments.Parse(Environment.GetCommandLineArgs()),
            InitializeSession: InitializeWindowLoadedSession,
            ScheduleLaunchAction: plan => ScheduleProjectLaunchAction(plan)));

    private void InitializeWindowLoadedUi()
    {
        var uiInitializer = new WindowLoadedUiInitializer(
            bindPlaylist: () => PlaylistList.ItemsSource = _playlist.Tracks,
            subscribeLtc: () =>
            {
                _ltcMonitor.FrameReceived += LtcMonitor_FrameReceived;
                _ltcMonitor.Stopped += LtcMonitor_Stopped;
            },
            refreshLtcDevices: RefreshLtcDevices,
            applyAutoOffset: () => AutoOffsetCheckBox.IsChecked = _settingsManager.Current.AutoOffsetOnAdd);
        uiInitializer.Initialize();
    }

    // mpv 専用スレッド（RenderSession の snapshot 生成側）からも呼ばれる。
    private double? ReadMpvTimePos()
    {
        IntPtr mpv = Volatile.Read(ref _mpv);
        if (mpv == IntPtr.Zero) return null;
        return _mpvApi.GetProperty(mpv, "time-pos", _mpvApi.FormatDouble, out double pos) == 0 && double.IsFinite(pos)
            ? pos
            : null;
    }

    // Gpu backend: ギャップ・カード・世代・位置を GPU worker の mailbox へ渡す（最新1件）。
    private void SubmitOutputState()
    {
        if (_outputEngine == null || _disposed) return;
        OutputGapMode gap = _renderSession.GetGapRenderDecision() switch
        {
            GapRenderFrameDecision.Black => OutputGapMode.Black,
            GapRenderFrameDecision.GapFreeze => OutputGapMode.GapFreeze,
            GapRenderFrameDecision.Hold => OutputGapMode.Hold,
            _ => OutputGapMode.None,
        };
        _outputEngine.SubmitTimelineState(new TimelineOutputState(
            _renderSession.CaptureGeneration(),
            gap,
            OutputEngineSettings.TestCardRequested(),
            CanvasSettings.Default,
            new ClipPlacement(null),
            ReadMpvTimePos() ?? 0));
    }

    private static string ResolveOutputSenderName()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(SpoutSender.SenderNameEnvironmentVariable);
        return string.IsNullOrWhiteSpace(fromEnv) ? SpoutOutput.DefaultSenderName : fromEnv;
    }

    // OutputEngine の GPU worker から呼ばれる。UI は Dispatcher に投げるだけで待たない。
    private void OnOutputPreviewFrame(PreviewFrame frame)
    {
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
            {
                try
                {
                    if (_disposed) return;
                    if (_outputPreviewBitmap == null
                        || _outputPreviewBitmap.PixelWidth != frame.Width
                        || _outputPreviewBitmap.PixelHeight != frame.Height)
                    {
                        _outputPreviewBitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
                    }
                    _outputPreviewBitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Pixels, frame.Width * 4, 0);
                    VideoImage.Source = _outputPreviewBitmap;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "OutputEngine: プレビュー更新に失敗");
                }
                finally { frame.Release(); }
            });
        }
        catch (Exception)
        {
            frame.Release();
        }
    }

    private bool InitializeWindowLoadedSession()
    {
        var spoutUiApplicator = new SpoutStartupUiApplicator(
            setButtonEnabled: enabled => BtnSpout.IsEnabled = enabled,
            setToggleLabel: label => _vm.Sync.SpoutToggleLabel = label);
        var sessionInitializer = new WindowLoadedSessionInitializer(
            initializeMpvSession: () => _mpvSessionInitializer.Initialize(_showDebugOsd),
            assignMpv: mpv => _mpv = mpv,
            applyAudioSettings: _audioControlCoordinator.ApplyStartup,
            createRenderContext: () => _renderSession.Create(_mpv),
            allocateRenderParameters: _renderSession.AllocateParameters,
            // Gpu backend では OutputEngine の SendTexture 経路が送信者を持つため、CPU 側 spoutDX は初期化しない。
            initializeSpout: () => SpoutStartupState.FromInitializationResult(
                _outputEngine != null ? true : _spoutOutput.TryInitialize()),
            applySpoutStartupState: spoutUiApplicator.Apply,
            initializeFrameRenderer: _renderSession.InitializeFrameRenderer,
            startTimer: () => _timer = StartupTimerFactory.CreateStartedTimer(TimeSpan.FromMilliseconds(TimerIntervalMs), OnTick),
            initializeStartupBuffer: _renderSession.InitializeStartupBuffer,
            initializeTimeline: InitializeTimeline,
            showError: ShowWindowLoadedSessionInitializationError);
        bool initialized = sessionInitializer.Initialize();
        if (initialized)
            RefreshDisplaySelection(_settingsManager.Current.FullscreenDisplayDeviceName);
        return initialized;
    }

    private void ScheduleProjectLaunchAction(ProjectLaunchActionPlan launchActionPlan)
    {
        var launchActionExecutor = new ProjectLaunchActionExecutor(
            LoadProjectFromLaunchAsync,
            paths => ReplacePlaylistAndLoadAsync(paths),
            path => _projectSaveExecutor.SaveAsync(path, _vm.Sync.SyncMode, _vm.Sync.GapBehavior));
        var launchActionScheduler = new ProjectLaunchActionScheduler(
            scheduleStartup: action => _ = Dispatcher.InvokeAsync(async () => await action()),
            scheduleSave: action => _ = Dispatcher.BeginInvoke(async () => await action(), DispatcherPriority.Normal),
            delayAsync: Task.Delay,
            logStartupFailure: (ex, plan) => Log.Error(ex, "launch startup action failed action={Action} path={Path}",
                plan.StartupAction, plan.LoadProjectPath),
            logSaveCompleted: path =>
            {
                RememberProjectPath(path);
                Log.Information("--save-project completed: {Path}", path);
            },
            logSaveFailure: (ex, path) => Log.Error(ex, "--save-project failed: {Path}", path));
        launchActionScheduler.Schedule(launchActionPlan, launchActionExecutor, TimeSpan.FromMilliseconds(SaveProjectDelayMs));
    }

    private static void ShowWindowLoadedSessionInitializationError(WindowLoadedSessionInitializationError error)
    {
        string message = error switch
        {
            WindowLoadedSessionInitializationError.MpvCreateFailed =>
                "mpv_create 失敗。mpv-2.dll を確認してください。",
            WindowLoadedSessionInitializationError.MpvInitializeFailed =>
                "mpvの初期化に失敗しました。",
            WindowLoadedSessionInitializationError.RenderContextCreateFailed =>
                "mpv レンダーコンテキストの作成に失敗しました。",
            _ => "初期化に失敗しました。"
        };

        MessageBox.Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private async Task LoadProjectFromLaunchAsync(string path)
    {
        var project = await ProjectSerializer.LoadAsync(path);
        if (project == null)
            return;

        RestoreLoadedProject(project);
        RememberProjectPath(path);
    }

    private void RestoreLoadedProject(ProjectData project)
    {
        _projectRestorePauseState.Clear();
        StopPlayback();
        ApplyLoadedProject(project);

        SyncPlaylistSelection();
        UpdatePlaylistTimelineDisplay();
        UpdateCurrentTrackLabel();
        LoadCurrentPlaylistTrack(paused: true);
        _ = ReadDurationsInBackground(
            _playlist.Tracks.Select(t => t.FilePath).ToList(),
            recalculateTimeline: false);
    }

    private void ApplyLoadedProject(ProjectData project)
    {
        ProjectLoadApplyResult result = _projectLoadApplicator.Apply(project);
        _vm.Sync.SyncModeIndex = result.SyncModeIndex;
        _vm.Sync.GapBehaviorIndex = result.GapBehaviorIndex;
    }

    private void InitializeTimeline()
    {
        bool isVisible = _settingsManager.Current.IsTimelineVisible;
        TimelineStartupState startupState = TimelineStartupInitializer.CreateState(isVisible);
        _timelinePanel = new TimelinePanel(_playlist, isVisible);
        _timelinePanel.TimelineSeekRequested += TimelinePanel_TimelineSeekRequested;
        TimelineContainer.Child = _timelinePanel;
        TimelineContainer.Visibility = startupState.ContainerVisibility;
        _vm.Sync.TimelineToggleLabel = startupState.ToggleLabel;
    }



    // ── ファイルを開く ─────────────────────────────────────────────

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv == IntPtr.Zero) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "動画ファイルを選択",
            Filter = "動画|*.mp4;*.mov;*.avi;*.mkv;*.mxf;*.ts;*.m2ts|すべて|*.*"
        };
        if (dlg.ShowDialog() != true) return;
        _ = ReplacePlaylistAndLoadAsyncSingle(dlg.FileName);
    }

    private async void BtnAddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv == IntPtr.Zero) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title       = "Playlist に追加する動画ファイルを選択",
            Filter      = "動画|*.mp4;*.mov;*.avi;*.mkv;*.mxf;*.ts;*.m2ts|すべて|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        _vm.Playlist.AutoOffsetOnAdd = _settingsManager.Current.AutoOffsetOnAdd;
        bool wasEmpty = _playlist.Tracks.Count == 0;
        var runner = new PlaylistAddFilesActionRunner();
        await runner.RunAsync(
            wasEmpty,
            () => _vm.Playlist.AddFilesAsync(dlg.FileNames, CancellationToken.None),
            ex => Log.Error(ex, "AddFilesAsync failed"),
            () => MessageBox.Show("ファイルの追加に失敗しました。", "エラー", MessageBoxButton.OK, MessageBoxImage.Error),
            SyncPlaylistSelection,
            UpdatePlaylistTimelineDisplay,
            () => _playlist.Current != null,
            () => LoadCurrentPlaylistTrack());
    }

    private void BtnRefreshLtcDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshLtcDevices();
    }

    private void LtcDeviceCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingLtcDevices || LtcDeviceCombo.SelectedItem is not string deviceName)
            return;

        _ = _settingsManager.UpdateAsync(settings => settings with { LtcDeviceName = deviceName });
        Log.Information("LTC capture device selected device={Device}", deviceName);
    }

    private void AutoOffsetCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool autoOffset = AutoOffsetCheckBox.IsChecked == true;
        _ = _settingsManager.UpdateAsync(s => s with { AutoOffsetOnAdd = autoOffset });
    }

    private void RefreshLtcDevices()
    {
        string? requestedSelection = LtcDeviceCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(requestedSelection))
            requestedSelection = _settingsManager.Current.LtcDeviceName;

        _isRefreshingLtcDevices = true;

        try
        {
            LtcDeviceCombo.Items.Clear();
            IReadOnlyList<string> deviceNames = _ltcMonitor.GetCaptureDeviceNames();
            foreach (string name in deviceNames)
                LtcDeviceCombo.Items.Add(name);

            int selectedIndex = LtcDeviceListRefreshPlanner.ResolveSelectedIndex(requestedSelection, deviceNames);
            LtcDeviceCombo.SelectedIndex = selectedIndex;
            if (!string.IsNullOrEmpty(requestedSelection) &&
                (selectedIndex < 0 || deviceNames[selectedIndex] != requestedSelection))
            {
                Log.Warning(
                    "Saved LTC capture device was not found; using first available device requested={Device}",
                    requestedSelection);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LTC capture device enumeration failed");
            _ltcSyncController.DeviceEnumerationFailed();
        }
        finally
        {
            _isRefreshingLtcDevices = false;
        }
    }

    private void RememberProjectPath(string path) =>
        _ = _settingsManager.UpdateAsync(settings => settings with { LastOpenedProjectPath = path });

    private void LtcMonitor_FrameReceived(object? sender, LtcFrameReceivedEventArgs e)
    {
        if (_disposed) return;
        long receivedAtMilliseconds = Environment.TickCount64;
        SyncAccuracyTrace.Current.RecordLtc(e);
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) _ltcSyncController.ReceiveFrame(e, receivedAtMilliseconds);
        });
    }

    private SingleModeSyncCoordinator CreateSingleModeSyncCoordinator() =>
        _singleModeSyncCoordinator ??= new SingleModeSyncCoordinator(
            _syncService,
            new SingleModeSyncEffects(
                GetTimePos: () =>
                {
                    int rc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double playbackSeconds);
                    return (rc, playbackSeconds);
                },
                BuildPlaybackState: playbackSeconds => new SyncPlaybackState(
                    SyncEnabled: _vm.Sync.SyncEnabled,
                    HasCurrentTrack: _playlist.Current != null,
                    IsSeeking: _seekBarInteraction.IsSeeking,
                    PlaybackSeconds: playbackSeconds,
                    DurationSeconds: _duration,
                    VideoFps: _fps,
                    TimecodeFps: _ltcSyncController.LastTimecodeFps),
                SeekTo: target => SeekTo(target),
                GetTotalRenderedFrames: () => _playbackPerformanceStats.TotalRenderedFrames,
                IsNativeSeeking: IsNativeSeeking));

    private ContinueOnTrackCoordinator CreateContinueOnTrackCoordinator() =>
        _continueOnTrackCoordinator ??= new ContinueOnTrackCoordinator(
            _syncService,
            _fileLoadStabilityLogState,
            new ContinueOnTrackEffects(
                PeekGapExit: () => _gapFreezeHandler.PeekGapExit(),
                DecideGapExit: () => _gapFreezeHandler.DecideGapExit(),
                IsPlaybackPaused: () => _playbackControl.IsPaused,
                ClearGapFreezeFrame: () =>
                {
                    _gapFreezeHandler.ClearCachedFrameInfo();
                    _renderSession.Invalidate();
                    _renderSession.ClearGapFreezeFrame();
                },
                SeekTo: target => SeekTo(target),
                ResumeMpvPause: () => _mpvApi.SetPropertyString(_mpv, "pause", MpvValueNo),
                ApplyPauseState: paused => ApplyPauseState(paused),
                ShowOsdBar: () => _mpvApi.SetPropertyString(_mpv, MpvPropertyOsdBar, MpvValueYes),
                UpdateCurrentTrackLabel: () => UpdateCurrentTrackLabel(),
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => SetLoadedTrack(id),
                LoadFile: (path, start) => LoadFile(path, startPosition: start),
                GetTotalRenderedFrames: () => _playbackPerformanceStats.TotalRenderedFrames,
                GetTimePos: () =>
                {
                    int rc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double playbackSeconds);
                    return (rc, playbackSeconds);
                },
                BuildPlaybackState: playbackSeconds => new SyncPlaybackState(
                    SyncEnabled: true,
                    HasCurrentTrack: true,
                    IsSeeking: _seekBarInteraction.IsSeeking,
                    PlaybackSeconds: playbackSeconds,
                    DurationSeconds: _duration,
                    VideoFps: _fps,
                    TimecodeFps: _ltcSyncController.LastTimecodeFps),
                IsNativeSeeking: IsNativeSeeking));

    private GapEnterCoordinator CreateGapEnterCoordinator() =>
        _gapEnterCoordinator ??= new(_gapFreezeHandler, new GapEnterEffects(
            ResetEndAdvanceTriggered: () => _endAdvanceTriggered = false,
            IsPlaybackPaused: () => _playbackControl.IsPaused,
            PauseForGap: () => _gapPlaybackCommandExecutor.PauseForGap(_mpv),
            ApplyPauseState: paused => ApplyPauseState(paused),
            RenderBlack: () => _renderSession.QueueGapFrame(GapRenderFrameDecision.Black),
            RenderGapFreeze: () => _renderSession.QueueGapFrame(GapRenderFrameDecision.GapFreeze),
            ClearGapFreezeFrame: () => _renderSession.ClearGapFreezeFrame(),
            SeekTo: target => SeekTo(target),
            GetMpvDuration: () =>
            {
                int rc = _mpvApi.GetProperty(_mpv, "duration", _mpvApi.FormatDouble, out double duration);
                return (rc, duration);
            },
            IsMpvReady: () => _mpv != IntPtr.Zero,
            LoadPausedAt: (path, target) => _gapPlaybackCommandExecutor.LoadPausedAt(_mpv, path, target),
            ResetPlayerStateForNewTrack: () => ResetPlayerStateForNewTrack(),
            GetLoadedTrackId: () => _loadedTrackId,
            SetLoadedTrackId: id => SetLoadedTrack(id),
            GetDuration: () => _duration,
            SetDuration: d => _duration = d,
            GetFps: () => _fps,
            SetFps: f => _fps = f,
            GetGapBehavior: () => _vm.Sync.GapBehavior,
            UpdateCurrentTrackLabel: () => UpdateCurrentTrackLabel()));

    private void RefreshCurrentVideoFrame()
    {
        // Re-seek the current position to redraw immediately after leaving a black/frozen gap.
        if (_mpv != IntPtr.Zero &&
            _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double currentPos) == 0)
            SeekTo(currentPos);
    }

    private void LtcMonitor_Stopped(object? sender, Exception? exception)
    {
        if (_disposed) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_disposed) _ltcSyncController.MonitorStopped(exception);
        });
        if (exception != null)
            Log.Error(exception, "LTC monitor stopped with error");
    }

    private void BtnPreviousTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.MovePrevious())
        {
            SyncPlaylistSelection();
            LoadCurrentPlaylistTrack();
        }
    }

    private void BtnNextTrack_Click(object sender, RoutedEventArgs e)
    {
        if (_playlist.MoveNext())
        {
            SyncPlaylistSelection();
            LoadCurrentPlaylistTrack();
        }
    }

    private void PlaylistList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _vm.Playlist.SelectedIndex = PlaylistList.SelectedIndex;
        UpdateCurrentTrackLabel();
    }

    private void PlaylistList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // MouseDoubleClick は ListBox 全域（スクロールバー・空白領域含む）で発火するため、
        // 実際に項目上でのダブルクリックのときだけロードする
        if (ListBoxItemHitTester.GetItemIndexAt(PlaylistList, e.GetPosition(PlaylistList)) < 0)
            return;

        if (_playlist.Select(PlaylistList.SelectedIndex))
        {
            SyncPlaylistSelection();
            LoadCurrentPlaylistTrack();
        }
    }

    private void PlaylistList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        System.Windows.Point point = e.GetPosition(PlaylistList);
        _playlistDragDropCoordinator.SetDragStart(point.X, point.Y);
    }

    private void PlaylistList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PlaylistTrack? track = PlaylistList.SelectedItem as PlaylistTrack;
        System.Windows.Point currentPoint = e.GetPosition(PlaylistList);
        _playlistDragDropCoordinator.HandleMouseMove(
            track,
            currentPoint.X,
            currentPoint.Y,
            e.LeftButton == System.Windows.Input.MouseButtonState.Pressed,
            SystemParameters.MinimumHorizontalDragDistance,
            SystemParameters.MinimumVerticalDragDistance);
    }

    private void PlaylistList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = PlaylistDragDropCoordinator.CanAcceptDrop(e.Data.GetDataPresent(typeof(PlaylistTrack)))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PlaylistList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PlaylistTrack)) is not PlaylistTrack draggedTrack)
            return;

        int hitIndex = GetPlaylistIndexFromPoint(e.GetPosition(PlaylistList));
        e.Handled = _playlistDragDropCoordinator.HandleDrop(
            draggedTrack,
            hitIndex);
    }

    private int GetPlaylistIndexFromPoint(System.Windows.Point point)
        => ListBoxItemHitTester.GetItemIndexAt(PlaylistList, point);

    private async Task ReplacePlaylistAndLoadAsyncSingle(string path)
        => await ReplacePlaylistAndLoadAsync([path]);

    private async Task ReplacePlaylistAndLoadAsync(IEnumerable<string> paths)
    {
        _loadedTrackId = null;
        StopPlayback();
        bool autoOffset = _settingsManager.Current.AutoOffsetOnAdd;
        PlaylistLoadResult loadResult = _playlistLoadCoordinator.ReplaceWithFiles(paths, autoOffset);
        SyncPlaylistSelection();
        UpdatePlaylistTimelineDisplay();
        if (loadResult.ShouldLoadCurrentTrack)
            LoadCurrentPlaylistTrack();
        await ReadDurationsInBackground(loadResult.Paths.ToList(), recalculateTimeline: autoOffset);
    }

    private Task ReadDurationsInBackground(
        List<string> paths,
        int startIndex = 0,
        bool recalculateTimeline = true) =>
        _playlistDurationBackfillCoordinator.BackfillAsync(paths, startIndex, recalculateTimeline);

    private async void BtnSaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "TimecodeSyncPlayer Project (*.tsp)|*.tsp",
            DefaultExt = "tsp",
            Title = "プロジェクトを保存"
        };

        string? selectedPath = dialog.ShowDialog() == true ? dialog.FileName : null;
        await _projectFileCoordinator.SaveAsync(selectedPath);
    }

    private async void BtnLoadProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "TimecodeSyncPlayer Project (*.tsp)|*.tsp",
            DefaultExt = "tsp",
            Title = "プロジェクトを読み込み"
        };

        string? selectedPath = dialog.ShowDialog() == true ? dialog.FileName : null;
        await _projectFileCoordinator.LoadAsync(selectedPath);
    }

    /// <summary>
    /// _loadedTrackId とタイムラインパネルの表示用トラックIDを同時に更新する。
    /// </summary>
    private void SetLoadedTrack(Guid? id)
    {
        _loadedTrackId = id;
        if (_timelinePanel != null)
            _timelinePanel.LoadedTrackId = id;
    }

    private void LoadCurrentPlaylistTrack(bool paused = false)
    {
        PlaylistTrack? track = _playlist.Current;
        if (track == null) return;

        SetLoadedTrack(track.Id);
        if (paused)
        {
            if (LoadFilePaused(track.FilePath))
                _projectRestorePauseState.MarkPending();
        }
        else
        {
            _projectRestorePauseState.Clear();
            LoadFile(track.FilePath);
        }
        UpdateCurrentTrackLabel();
        UpdatePlaylistTimelineDisplay();
        Log.Information("Playlist track loaded index={Index} name={Name} path={Path}",
            _playlist.CurrentIndex, track.Name, track.FilePath);
    }

    private void SyncPlaylistSelection()
    {
        ApplyPlaylistSelectionIndex(_playlist.CurrentIndex);
        UpdateCurrentTrackLabel();
    }

    private void ApplyPlaylistSelectionIndex(int index)
    {
        if (index < -1 || index >= PlaylistList.Items.Count)
            return;

        PlaylistList.SelectedIndex = index;
    }

    private void UpdateCurrentTrackLabel()
    {
        _vm.Playlist.CurrentTrackLabel = PlaylistCurrentTrackLabelFormatter.Format(
            _vm.Sync.SyncMode,
            _vm.Sync.GapBehavior,
            _gapFreezeHandler.IsInactive,
            _playlist.Tracks,
            _playlist.CurrentIndex,
            _loadedTrackId,
            _ltcSyncController.LastLtcSeconds);
    }

    private void UpdatePlaylistTimelineDisplay()
    {
        foreach (var item in PlaylistList.Items)
        {
            var container = PlaylistList.ItemContainerGenerator.ContainerFromItem(item) as DependencyObject;
            if (container == null) continue;

            if (item is not PlaylistTrack track) continue;

            var timelineTextBlock = FindVisualChildByName<System.Windows.Controls.TextBlock>(container, "TimelineRangeTextBlock");
            if (timelineTextBlock != null)
            {
                timelineTextBlock.Text = track.GetTimelineRangeText();
            }

            var offsetTextBox = FindVisualChildByName<System.Windows.Controls.TextBox>(container, "TimelineOffsetTextBox");
            if (offsetTextBox != null && !offsetTextBox.IsFocused)
            {
                offsetTextBox.Text = track.TimelineOffsetText;
            }
        }
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name && child is T result)
                return result;
            var nested = FindVisualChildByName<T>(child, name);
            if (nested != null)
                return nested;
        }
        return null;
    }

    private void TimelineOffsetTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.TextBox textBox || textBox.Tag is not Guid trackId)
            return;

        var result = PlaylistTimelineOffsetEditor.Apply(
            _playlist,
            trackId,
            textBox.Text,
            _settingsManager.Current.AutoOffsetOnAdd,
            GapFreezeHandler.DefaultFallbackFps);
        if (result.Status == PlaylistTimelineOffsetEditStatus.TrackNotFound)
            return;

        Log.Debug("TimelineOffset edit: track={Track} input='{Input}' fps={Fps} currentOffset={CurrentOffset:F3} autoOffset={AutoOffset}",
            result.OriginalTrack!.Name,
            textBox.Text,
            result.Fps,
            result.OriginalTrack.TimelineOffset.TotalSeconds,
            _settingsManager.Current.AutoOffsetOnAdd);

        if (result.Status == PlaylistTimelineOffsetEditStatus.Applied)
        {
            if (_settingsManager.Current.AutoOffsetOnAdd)
            {
                Log.Debug("TimelineOffset edit: autoOffset enabled, recalculating from index {Index}", result.Index + 1);
            }
            else
            {
                Log.Debug("TimelineOffset edit: autoOffset disabled, skipping recalculate");
            }

            UpdatePlaylistTimelineDisplay();
            Log.Information("TimelineOffset updated: track={Track} offset={Offset:F3} actualIn={ActualIn:F3} actualOut={ActualOut:F3} autoOffset={AutoOffset}",
                result.OriginalTrack.Name,
                result.UpdatedTrack!.TimelineOffset.TotalSeconds,
                result.UpdatedTrack.GetActualTimelineIn().TotalSeconds,
                result.UpdatedTrack.GetActualTimelineOut().TotalSeconds,
                _settingsManager.Current.AutoOffsetOnAdd);
        }
        else
        {
            Log.Warning("TimelineOffset parse failed: track={Track} input='{Input}' fps={Fps}", result.OriginalTrack!.Name, textBox.Text, result.Fps);
            textBox.Text = result.OriginalTrack.TimelineOffsetText;
        }
    }

    private void TimelineOffsetTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            TimelineOffsetTextBox_LostFocus(sender, e);
            (sender as System.Windows.Controls.TextBox)?.GetBindingExpression(System.Windows.Controls.TextBox.TextProperty)?.UpdateSource();
        }
    }

    private void StopPlayback()
        => CreatePlaybackOperationsCoordinator().StopPlayback();

    private bool LoadFile(string path, double? startPosition = null)
        => CreatePlaybackOperationsCoordinator().LoadFile(path, startPosition);

    private bool LoadFilePaused(string path)
        => CreatePlaybackOperationsCoordinator().LoadFilePaused(path);

    // ── Playback helpers ───────────────────────────────────────────

    private bool SeekTo(double seconds, bool suppressOsd = true)
        => CreatePlaybackOperationsCoordinator().SeekTo(seconds, suppressOsd);

    // ── IPlaybackController ────────────────────────────────────────────────
    void IPlaybackController.TogglePlayPause()
    {
        if (_mpv == IntPtr.Zero) return;
        _projectRestorePauseState.Clear();
        PlaybackPauseChange change = _playbackControl.TogglePlayPause();
        _mpvApi.SetPropertyString(_mpv, "pause", change.MpvPauseValue);
        ResetPlaybackPerformanceStats();
        _vm.Player.PlayPauseIcon = change.PlayPauseIcon;
    }

    private void ResumeProjectRestorePauseForSyncIfNeeded()
    {
        if (!_projectRestorePauseState.TryConsume())
            return;

        _mpvApi.SetPropertyString(_mpv, "pause", MpvValueNo);
        ApplyPauseState(false);
        Log.Information("Project restore pause released by on-track sync");
    }

    void IPlaybackController.SeekRelative(double seconds)
    {
        if (_mpv == IntPtr.Zero) return;
        _ltcSyncController.CancelPendingSync();
        if (_mpvApi.CommandString(_mpv, $"seek {seconds} {MpvSeekModeRelative}") == 0)
            _mpvApi.SetPropertyString(_mpv, "pause", _playbackControl.IsPaused ? MpvValueYes : MpvValueNo);
    }

    void IPlaybackController.CycleSpeed()
    {
        if (_mpv == IntPtr.Zero) return;
        PlaybackSpeedChange change = _playbackControl.CycleSpeed();
        _mpvApi.SetPropertyString(_mpv, "speed", change.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _vm.Player.SpeedLabel = change.Label;
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        if (_mpv == IntPtr.Zero) return;
        _audioControlCoordinator.ToggleMute();
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (_mpv == IntPtr.Zero) return;
        _audioControlCoordinator.SetVolume(e.NewValue);
    }

    private void ApplyAudioControlUi(AudioControlSnapshot snapshot)
    {
        _vm.Player.MuteToggleLabel = snapshot.MuteToggleLabel;
        _vm.Player.Volume = snapshot.Volume;
    }

    private void ApplyPauseState(bool paused)
        => CreatePlaybackOperationsCoordinator().ApplyPauseState(paused);

    private PlaybackOperationsCoordinator CreatePlaybackOperationsCoordinator() =>
        _playbackOperationsCoordinator ??= new(_playbackControl, new PlaybackOperationsEffects(
            IsMpvReady: () => _mpv != IntPtr.Zero,
            CommandString: command => _mpvApi.CommandString(_mpv, command),
            SetPropertyString: (name, value) => _mpvApi.SetPropertyString(_mpv, name, value),
            ResetPlayerStateForNewTrack: () => ResetPlayerStateForNewTrack(),
            ResetVideoWidth: () => _renderSession.Width = 0,
            ResetVideoHeight: () => _renderSession.Height = 0,
            ClearLoadedTrackId: () => _loadedTrackId = null,
            HasTimelinePanel: () => _timelinePanel != null,
            ClearTimelineLoadedTrackId: () => _timelinePanel!.LoadedTrackId = null,
            SetSeekBarValueFromPlayer: value => SetSeekBarValueFromPlayer(value),
            SetTimeLabel: value => _vm.Player.TimeLabel = value,
            SetPlayPauseIcon: value => _vm.Player.PlayPauseIcon = value,
            ResetGapFreezeAll: () => _gapFreezeHandler.ResetAll(),
            ResetGapFreeze: () => _gapFreezeHandler.Reset(),
            ClearGapFreezeFrame: () => _renderSession.ClearGapFreezeFrame()));

    // ── Spout ─────────────────────────────────────────────────────

    private void BtnSpout_Click(object sender, RoutedEventArgs e)
    {
        _spoutOutput.IsEnabled = !_spoutOutput.IsEnabled;
        _outputEngine?.SetSpoutEnabled(_spoutOutput.IsEnabled);
        _vm.Sync.SpoutToggleLabel = ToggleLabelFormatter.Format(_spoutOutput.IsEnabled, SpoutOnLabel, SpoutOffLabel);
        Log.Information("Spout 出力: {State}", _spoutOutput.IsEnabled ? "ON" : "OFF");
    }

    private void DisplayCombo_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isRefreshingDisplays || DisplayCombo.SelectedItem is not DisplayTarget selected)
            return;

        _ = _settingsManager.UpdateAsync(settings => settings with
        {
            FullscreenDisplayDeviceName = selected.DeviceName,
        });
    }

    private void BtnFullscreen_Click(object sender, RoutedEventArgs e)
    {
        if (_fullscreenWindow != null)
        {
            CloseFullscreenOutput();
            return;
        }

        string? preferredDevice = (DisplayCombo.SelectedItem as DisplayTarget)?.DeviceName
            ?? _settingsManager.Current.FullscreenDisplayDeviceName;
        RefreshDisplaySelection(preferredDevice);
        if (DisplayCombo.SelectedItem is not DisplayTarget target)
            return;

        var window = CreateFullscreenOutputWindow(target);
        window.Closed += FullscreenWindow_Closed;
        if (_outputEngine == null)
            _renderSession.BitmapChanged += FullscreenFrameRenderer_BitmapChanged;
        _fullscreenWindow = window;

        try
        {
            window.Show();
            _renderSession.SetFullscreenActive(true);
            DisplayCombo.IsEnabled = false;
            BtnFullscreen.Content = FullscreenCloseLabel;
            var externalBitmap = _renderSession.CurrentExternalBitmap;
            var previewBitmap = VideoImage.Source as BitmapSource;
            Log.Information("Fullscreen output opened on {Display} externalBitmap={ExternalWidth}x{ExternalHeight} previewBitmap={PreviewWidth}x{PreviewHeight}",
                target.DeviceName, externalBitmap?.PixelWidth ?? 0, externalBitmap?.PixelHeight ?? 0,
                previewBitmap?.PixelWidth ?? 0, previewBitmap?.PixelHeight ?? 0);
        }
        catch
        {
            if (_outputEngine == null)
                _renderSession.BitmapChanged -= FullscreenFrameRenderer_BitmapChanged;
            window.Closed -= FullscreenWindow_Closed;
            _fullscreenWindow = null;
            _renderSession.SetFullscreenActive(false);
            throw;
        }
    }

    // Kept independent of VideoImage.Source: that image is a reduced, delayed preview.
    private FullscreenOutputWindow CreateFullscreenOutputWindow(DisplayTarget target) =>
        _outputEngine != null
            ? new FullscreenOutputWindow(target, _displayCatalog, null, _outputEngine)
            : new FullscreenOutputWindow(target, _displayCatalog, _renderSession.CurrentExternalBitmap);

    private void FullscreenFrameRenderer_BitmapChanged(WriteableBitmap bitmap) =>
        _fullscreenWindow?.UpdateBitmap(bitmap);

    private void FullscreenWindow_Closed(object? sender, EventArgs e)
    {
        if (_outputEngine == null)
            _renderSession.BitmapChanged -= FullscreenFrameRenderer_BitmapChanged;
        else
            _outputEngine.DetachFullscreen();
        if (sender is FullscreenOutputWindow window)
            window.Closed -= FullscreenWindow_Closed;
        _fullscreenWindow = null;
        _renderSession.SetFullscreenActive(false);
        BtnFullscreen.Content = FullscreenOpenLabel;
        DisplayCombo.IsEnabled = true;
        string? selectedDeviceName = (DisplayCombo.SelectedItem as DisplayTarget)?.DeviceName
            ?? _settingsManager.Current.FullscreenDisplayDeviceName;
        RefreshDisplaySelection(selectedDeviceName);
        Log.Information("Fullscreen output closed");
    }

    private void CloseFullscreenOutput()
    {
        FullscreenOutputWindow? window = _fullscreenWindow;
        if (window == null)
            return;

        window.Close();
    }

    private void RefreshDisplaySelection(string? preferredDeviceName)
    {
        _isRefreshingDisplays = true;
        try
        {
            IReadOnlyList<DisplayTarget> displays;
            try
            {
                displays = _displayCatalog.GetDisplays();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to enumerate displays");
                displays = [];
            }

            DisplayTarget? selected = DisplaySelectionPolicy.Select(displays, preferredDeviceName);
            DisplayCombo.ItemsSource = displays;
            DisplayCombo.SelectedItem = selected;
            BtnFullscreen.IsEnabled = _fullscreenWindow != null || selected != null;

        }
        finally
        {
            _isRefreshingDisplays = false;
        }
    }

    private void BtnTimeline_Click(object sender, RoutedEventArgs e)
    {
        bool isVisible = TimelineContainer.Visibility != Visibility.Visible;
        TimelineContainer.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        _vm.Sync.TimelineToggleLabel = ToggleLabelFormatter.Format(isVisible, TimelineOnLabel, TimelineOffLabel);

        if (_timelinePanel != null)
            _timelinePanel.IsTimelineVisible = isVisible;

        _ = _settingsManager.UpdateAsync(s => s with { IsTimelineVisible = isVisible });
        Log.Information("Timeline visibility changed to {Visible}", isVisible);
    }

    private void TimelinePanel_TimelineSeekRequested(object? sender, TimelineSeekEventArgs e)
    {
        if (_mpv == IntPtr.Zero) return;

        _ltcSyncController.CancelPendingSync();
        _syncService.ClearSeekState();
        bool success = SeekTo(e.TargetSeconds);
        Log.Information("Timeline seek target={Target:F3} trackIndex={TrackIndex} success={Success}",
            e.TargetSeconds, e.TrackIndex, success);
    }

    // ── シークバー操作 ────────────────────────────────────────────

    private void Seek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ltcSyncController.CancelPendingSync();
        _seekBarInteraction.BeginSeek();
        TrySetSeekBarFromPointer(e, "MouseDown");
    }

    private void Seek_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        TrySetSeekBarFromPointer(e, "MouseUp");
        _seekBarInteraction.EndSeek();
        CommitSeekBarSeek(SeekBar.Value, "MouseUp");
    }

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SeekBarPreview preview = _seekBarInteraction.CreatePreview(e.NewValue, _duration);
        if (preview.HasValue)
        {
            _vm.Player.TimeLabel = $"{PlaybackTimeFormatter.FormatFrames(preview.PositionSeconds, _fps)} / {PlaybackTimeFormatter.FormatFrames(_duration, _fps)}";
            UpdateOsd(preview.PositionSeconds);
            return;
        }

        CommitSeekBarSeek(e.NewValue, "Automation");
    }

    // ── 定期 UI 更新（タイマー） ──────────────────────────────────
    // 周期性タスクのみ（duration取得・メタデータ取得）。
    // フレームごとのUI更新（シークバー・時刻表示・OSD等）は OnRenderUpdate で行う。

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed || _mpv == IntPtr.Zero) return;

        SubmitOutputState();
        _ltcSyncController.Tick(Environment.TickCount64);

        int durationRc = _mpvApi.GetProperty(_mpv, "duration", _mpvApi.FormatDouble, out double dur);
        if (durationRc == 0 && SeekBarUpdateState.IsUsableDuration(dur))
            _duration = dur;

        if (!_metadataFetched && _duration > 0)
            FetchMetadata();

        // Gap 状態では mpv のレンダーコールバックが止まるため、
        // タイマーでタイムライン位置を更新する
        if (!_gapFreezeHandler.IsInactive
            && _vm.Sync.SyncMode == SyncMode.Continue
            && _ltcSyncController.LastLtcSeconds > 0)
        {
            _timelinePanel?.UpdatePlaybackPosition(_ltcSyncController.LastLtcSeconds);
        }

        // A paused seek may finish after its final FRAME callback. Explicitly redraw
        // once native completion is observable; the capture operation excludes duplicates.
        if (_gapFreezeHandler.CurrentState == GapState.EnteringFreeze)
            _ = AsyncOperationExceptionBoundary.RunAsync(
                () => TryCompleteGapFreezeAsync(_renderSession.CaptureGeneration(), false, allowRedraw: true),
                ex => Log.Error(ex, "Gap freeze completion retry failed"));

        if (_gapFreezeHandler.HasTimedOut())
        {
            Log.Warning("Continue mode: gap freeze final-frame capture timed out, holding current frame");
            _gapFreezeHandler.ForceFreezeComplete();
        }
    }

    private void TryAdvancePlaylistAtEnd(double positionSeconds)
    {
        if (_playbackControl.IsPaused || _seekBarInteraction.IsSeeking || _endAdvanceTriggered) return;
        if (!SeekBarUpdateState.IsUsableDuration(_duration)) return;
        if (!ContinueModePlaybackPolicy.ShouldAutoAdvanceAtMediaEnd(_vm.Sync.SyncMode, _vm.Sync.SyncEnabled))
            return;

        if (_vm.Sync.SyncMode == SyncMode.Continue)
        {
            TryAdvanceContinueMode(positionSeconds);
        }
        else
        {
            if (_playlist.Current == null || _loadedTrackId != _playlist.Current.Id) return;
            if (positionSeconds < _duration - GapFreezeHandler.EndAdvanceThresholdSec) return;

            _endAdvanceTriggered = true;
            if (_playlist.MoveNext())
            {
                SyncPlaylistSelection();
                LoadCurrentPlaylistTrack();
            }
        }
    }

    private void TryAdvanceContinueMode(double positionSeconds)
    {
        ContinueModeEndAdvanceDecision decision = PlaylistEndAdvancePlanner.Decide(
            _playlist.Tracks,
            _loadedTrackId,
            positionSeconds,
            alreadyTriggered: _endAdvanceTriggered,
            isPaused: _playbackControl.IsPaused,
            isSeeking: _seekBarInteraction.IsSeeking,
            thresholdSeconds: GapFreezeHandler.EndAdvanceThresholdSec);

        switch (decision.Action)
        {
            case ContinueModeEndAdvanceAction.None:
                return;

            case ContinueModeEndAdvanceAction.LoadNextTrack:
            {
                var nextTrack = decision.NextTrack!;
                _endAdvanceTriggered = true;
                Log.Information("Continue mode: auto-advancing to track {TrackName}", nextTrack.Name);
                double startPos = nextTrack.MediaIn > TimeSpan.Zero ? nextTrack.MediaIn.TotalSeconds : 0;
                bool success = LoadFile(nextTrack.FilePath, startPosition: startPos > 0 ? startPos : null);
                if (success)
                {
                    SetLoadedTrack(nextTrack.Id);
                }
                return;
            }

            case ContinueModeEndAdvanceAction.EnterNoTracks:
                _endAdvanceTriggered = true;
                Log.Information("Continue mode: reached final track end, entering no-tracks gap state");
                CreateGapEnterCoordinator().HandleNoTracks();
                return;
        }
    }

    // ── SW レンダーコールバック（フレーム描画） ───────────────────

    /// <summary>
    /// mpv のレンダー更新コールバックから Dispatcher 経由で呼ばれる（UI スレッド）。
    /// Gapのキャプチャ状態を接続し、RenderSessionで描画後にフレームごとのUI更新を行う。
    /// </summary>
    private async Task ProcessRenderFrameUpdateAsync(int renderGeneration, bool hasFrame)
    {
        await TryCompleteGapFreezeAsync(renderGeneration, hasFrame);
        if (_disposed || !_renderSession.IsCurrent(renderGeneration)) return;
        SubmitOutputState();
        if (hasFrame && _gapFreezeHandler.IsInactive)
        {
            await _renderSession.RenderFrameAsync(renderGeneration);
            if (_disposed || !_renderSession.IsCurrent(renderGeneration)) return;
        }

        GapRenderFrameDecision gapRenderDecision = _renderSession.GetGapRenderDecision();
        if (gapRenderDecision == GapRenderFrameDecision.Black)
        {
            await _renderSession.RenderGapAsync(gapRenderDecision);
        }
        else if (gapRenderDecision == GapRenderFrameDecision.GapFreeze)
        {
            await _renderSession.RenderGapAsync(gapRenderDecision);
        }

        if (_disposed || !_renderSession.IsCurrent(renderGeneration)) return;
        UpdatePerFrameUI();
    }

    private async Task TryCompleteGapFreezeAsync(int renderGeneration, bool hasFrame, bool allowRedraw = false)
    {
        if (_gapFreezeHandler.CurrentState == GapState.EnteringFreeze && !IsNativeSeeking() &&
            _mpvApi.GetPropertyString(_mpv, "pause") == MpvValueYes)
        {
            int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double actualPos);
            GapFrameCaptureDecision decision = GapFrameCaptureCoordinator.Decide(
                _gapFreezeHandler.CurrentState,
                hasFrame,
                IsCurrentMpvPathExpectedForGapFreeze(),
                timePosRc == 0,
                actualPos,
                _gapFreezeHandler.PendingTargetSeconds,
                _fps > 0 ? _fps : GapFreezeHandler.DefaultFallbackFps,
                allowRedraw: allowRedraw);

            if (decision == GapFrameCaptureDecision.RenderAndCapture)
            {
                bool captured = await GapFreezeCaptureOperation.RunAsync(
                    _gapFreezeHandler, _loadedTrackId,
                    () => !_disposed && _renderSession.IsCurrent(renderGeneration),
                    stillCurrent => _renderSession.TryCaptureGapFreezeFrameAsync(renderGeneration,
                        () => stillCurrent() && IsNativeGapFreezeTargetReady()));
                if (captured)
                {
                    Log.Information("Continue mode: gap freeze activated, final frame captured");
                    // Timer retries must publish too: a paused decoder may issue no
                    // further callback after native seek completion becomes visible.
                    if (!_disposed && _renderSession.IsCurrent(renderGeneration))
                        await _renderSession.RenderGapAsync(GapRenderFrameDecision.GapFreeze);
                }
            }
        }
    }

    private bool IsNativeSeeking() =>
        _mpv == IntPtr.Zero || _mpvApi.GetPropertyString(_mpv, "seeking") != MpvValueNo;

    // Recheck on the UI thread after the native render await. A manual operation can
    // start and finish during that await without changing the gap capture attempt.
    private bool IsNativeGapFreezeTargetReady()
    {
        if (IsNativeSeeking() || _mpvApi.GetPropertyString(_mpv, "pause") != MpvValueYes)
            return false;
        if (!string.IsNullOrWhiteSpace(_gapFreezeHandler.PendingPath) &&
            !ContinueModePlaybackPolicy.IsExpectedMediaPath(
                _mpvApi.GetPropertyString(_mpv, "path"), _gapFreezeHandler.PendingPath))
            return false;
        int rc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double position);
        return GapFrameCaptureCoordinator.Decide(_gapFreezeHandler.CurrentState, true, true,
            rc == 0, position, _gapFreezeHandler.PendingTargetSeconds, _fps) ==
            GapFrameCaptureDecision.RenderAndCapture;
    }

    private bool IsCurrentMpvPathExpectedForGapFreeze()
    {
        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            _mpvApi,
            _mpv,
            _gapFreezeHandler.PendingPath,
            _gapFreezeHandler.PendingTargetSeconds,
            _gapFreezeHandler.LastReloadAt,
            DateTime.UtcNow,
            TimeSpan.FromMilliseconds(LoadfileReloadDebounceMs));

        if (result.IsExpected)
            return true;

        if (result.ReloadIssued)
        {
            _gapFreezeHandler.LastReloadAt = result.LastReloadAt;
            Log.Warning(
                "Continue mode: ignored stale gap freeze frame currentPath={CurrentPath} expectedPath={ExpectedPath}; reissued load target={Target:F3} loadRc={LoadRc} pauseRc={PauseRc}",
                result.CurrentPath, _gapFreezeHandler.PendingPath, _gapFreezeHandler.PendingTargetSeconds, result.LoadRc, result.PauseRc);
        }
        else
        {
            Log.Debug(
                "Continue mode: ignored stale gap freeze frame currentPath={CurrentPath} expectedPath={ExpectedPath}",
                result.CurrentPath, _gapFreezeHandler.PendingPath);
        }

        return false;
    }

    /// <summary>
    /// フレームごとのUI更新（シークバー・時刻表示・OSD・プレイリスト自動送り・パフォーマンス統計）。
    /// ProcessRenderFrameUpdateAsync から呼ばれる。
    /// </summary>
    private void UpdatePerFrameUI()
    {
        if (_mpv == IntPtr.Zero) return;

        int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double pos);

        double? gapTimelinePosition = PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            _gapFreezeHandler.IsInactive,
            _vm.Sync.SyncMode,
            _ltcSyncController.LastLtcSeconds);
        if (gapTimelinePosition.HasValue)
            _timelinePanel?.UpdatePlaybackPosition(gapTimelinePosition.Value);

        if (timePosRc != 0) return;

        if (!_playbackControl.IsPaused)
        {
            PlaybackPerformanceSnapshot? performance = _playbackPerformanceStats.RecordTick(pos, DateTime.UtcNow);
            if (performance != null)
                LogPlaybackPerformance(performance);
        }

        if (!_seekBarInteraction.IsSeeking)
        {
            double displayPos = pos;
            SetSeekBarValueFromPlayer(SeekBarUpdateState.ToSliderValue(displayPos, _duration, SeekBar.Value));
            _vm.Player.TimeLabel = $"{PlaybackTimeFormatter.FormatFrames(displayPos, _fps)} / {PlaybackTimeFormatter.FormatFrames(_duration, _fps)}";
            UpdateOsd(displayPos);
            TryAdvancePlaylistAtEnd(pos);

            if (_gapFreezeHandler.IsInactive)
            {
                double timelinePosition = PlaybackTimelinePositionPolicy.GetNormalTimelinePosition(
                    _vm.Sync.SyncMode,
                    _vm.Sync.SyncEnabled,
                    _ltcSyncController.LastLtcSeconds,
                    pos);
                _timelinePanel?.UpdatePlaybackPosition(timelinePosition);
            }
        }
    }

    // ── 終了 ─────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var disposer = new MainWindowResourceDisposer(
            disposeTimer: () =>
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTick;
                }
            },
            disposeRenderContext: _renderSession.FreeContext,
            disposeMpv: () =>
            {
                if (_mpv != IntPtr.Zero)
                {
                    _mpvApi.TerminateDestroy(_mpv);
                    _mpv = IntPtr.Zero;
                }
            },
            disposeLtc: () =>
            {
                _ltcMonitor.FrameReceived -= LtcMonitor_FrameReceived;
                _ltcMonitor.Stopped -= LtcMonitor_Stopped;
                _ltcMonitor.Dispose();
            },
            disposeSpout: () => _spoutOutput.Dispose(),
            disposeTimeline: () =>
            {
                if (_timelinePanel != null)
                    _timelinePanel.TimelineSeekRequested -= TimelinePanel_TimelineSeekRequested;
                _timelinePanel?.Dispose();
            },
            disposeBuffer: _renderSession.Dispose,
            stopRender: _renderSession.Stop,
            closeFullscreen: CloseFullscreenOutput,
            stopOutput: () => _outputEngine?.Stop(),
            disposeOutput: () => _outputEngine?.Dispose());
        disposer.DisposeAll();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during MainWindow cleanup");
        }
    }

    // ── メタデータ取得 ────────────────────────────────────────────

    private void FetchMetadata()
    {
        if (_mpvApi.GetProperty(_mpv, "container-fps", _mpvApi.FormatDouble, out double fps) == 0 && fps > 0)
            _fps = fps;

        string widthStr  = _mpvApi.GetPropertyString(_mpv, "width");
        string heightStr = _mpvApi.GetPropertyString(_mpv, "height");
        string vcodec    = _mpvApi.GetPropertyString(_mpv, "video-codec");
        string acodec    = _mpvApi.GetPropertyString(_mpv, "audio-codec");

        // レンダー解像度を設定（SW レンダーはこのサイズで描画する）
        if (int.TryParse(widthStr,  out int w) && w > 0) _renderSession.Width  = w;
        if (int.TryParse(heightStr, out int h) && h > 0) _renderSession.Height = h;

        if (_renderSession.Width <= 0 || _renderSession.Height <= 0)
            return;

        _metadataFetched = true;
        // The native pump consumed any early FRAME while dimensions were unknown.
        // Paused loads need one explicit redraw after metadata becomes available.
        if (_gapFreezeHandler.IsInactive)
            _ = AsyncOperationExceptionBoundary.RunAsync(
                () => _renderSession.RenderFrameAsync(_renderSession.CaptureGeneration()),
                ex => Log.Error(ex, "Metadata frame redraw failed"));
        Log.Information("FetchMetadata: {W}x{H} {Fps:F3}fps V:{VCodec} A:{ACodec}",
            _renderSession.Width, _renderSession.Height, _fps, vcodec, acodec);

        _metaLine = MetadataDisplayFormatter.FormatMetadataLine(
            _renderSession.Width,
            _renderSession.Height,
            _fps,
            vcodec,
            acodec);
        _vm.Player.MetaLine = _metaLine;
    }

    // ── OSD ───────────────────────────────────────────────────────

    private void UpdateOsd(double pos)
    {
        if (_mpv == IntPtr.Zero) return;
        if (!DebugOsdPolicy.ShouldWrite(_showDebugOsd)) return;
        int frame = _fps > 0 ? (int)(pos * _fps) : (int)pos;
        if (!_osdUpdateState.ShouldUpdate(frame, DateTime.UtcNow)) return;
        string timePart = PlaybackTimeFormatter.FormatFrames(pos, _fps);
        string text = DebugOsdPolicy.FormatText(timePart, _metaLine);
        _mpvApi.SetPropertyString(_mpv, "osd-msg3", text);
    }

    // ── シークバー ────────────────────────────────────────────────

    private bool TrySetSeekBarFromPointer(System.Windows.Input.MouseButtonEventArgs e, string phase)
    {
        double oldValue  = SeekBar.Value;
        double pointerX  = e.GetPosition(SeekBar).X;
        SeekBarPointerUpdate update = _seekBarInteraction.TrySetFromPointer(pointerX, SeekBar.ActualWidth, oldValue, _duration);

        if (!update.Applied)
        {
            Log.Debug("Seek {Phase} ignored pointerX={PointerX:F1} width={Width:F1}", phase, pointerX, SeekBar.ActualWidth);
            return false;
        }

        SeekBar.Value = update.SliderValue;
        _vm.Player.SeekBarValue = update.SliderValue;
        Log.Information("Seek {Phase} pointerX={PointerX:F1} oldValue={OldValue:F6} newValue={NewValue:F6}",
            phase, pointerX, oldValue, update.SliderValue);
        return true;
    }

    private void SetSeekBarValueFromPlayer(double value)
    {
        _seekBarInteraction.BeginPlayerUpdate();
        try
        {
            SeekBar.Value = value;
            _vm.Player.SeekBarValue = value;
        }
        finally
        {
            _seekBarInteraction.EndPlayerUpdate();
        }
    }

    private void CommitSeekBarSeek(double sliderValue, string source)
    {
        if (_mpv == IntPtr.Zero) return;

        SeekBarCommit commit = _seekBarInteraction.CreateCommit(sliderValue, SeekBar.Minimum, SeekBar.Maximum, _duration);
        if (!commit.ShouldCommit) return;

        _ltcSyncController.CancelPendingSync();
        _vm.Player.SeekBarValue = commit.SliderValue;
        _seekState.MarkSeekSent(commit.TargetSeconds, DateTime.UtcNow);
        bool success = SeekTo(commit.TargetSeconds);
        int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double timePos);
        Log.Information(
            "Seek command sent source={Source} value={SliderValue:F6} duration={Duration:F3} target={Target:F3} success={Success} immediateTimePos={TimePos:F3}",
            source, commit.SliderValue, _duration, commit.TargetSeconds, success, timePos);
    }

    private void LogSeekTickIfNeeded(
        int durationRc, double reportedDuration, int timePosRc,
        double playerPosition, double displayPosition,
        double beforeValue, double afterValue)
    {
        if (!_seekState.HasPendingSeek) return;

        DateTime now = DateTime.UtcNow;
        if (now - _lastSeekTickLogAt < TimeSpan.FromMilliseconds(SeekDebounceMs)) return;

        _lastSeekTickLogAt = now;
        Log.Information(
            "Seek pending tick durationRc={DurationRc} dur={Dur:F3} timePosRc={TimePosRc} playerPos={PlayerPos:F3} displayPos={DisplayPos:F3} target={Target:F3} sliderBefore={SliderBefore:F6} sliderAfter={SliderAfter:F6}",
            durationRc, reportedDuration, timePosRc, playerPosition, displayPosition,
            _seekState.TargetSeconds, beforeValue, afterValue);
    }

    private void LogPlaybackPerformance(PlaybackPerformanceSnapshot snapshot)
    {
        RenderUpdateSchedulerStats renderStats = _renderSession.ConsumeUpdateStats();

        Log.Information(
            "Playback perf elapsed={Elapsed:F2}s expectedFps={ExpectedFps:F3} playbackRate={PlaybackRate:F3} displayedFps={DisplayedFps:F2} ticks={Ticks} renderCallbacks={RenderCallbacks} coalescedRenderCallbacks={CoalescedRenderCallbacks} renderUpdates={RenderUpdates} frameUpdates={FrameUpdates} renderedFrames={RenderedFrames} avgRenderMs={AvgRenderMs:F2} maxRenderMs={MaxRenderMs:F2} avgBitmapMs={AvgBitmapMs:F2} maxBitmapMs={MaxBitmapMs:F2} avgSpoutMs={AvgSpoutMs:F2} maxSpoutMs={MaxSpoutMs:F2} size={Width}x{Height} spoutEnabled={SpoutEnabled} frameBoundary=full-resolution-bitmap",
            snapshot.Elapsed.TotalSeconds, _fps, snapshot.PlaybackRate,
            snapshot.DisplayedFps, snapshot.TickCount, renderStats.Requests,
            renderStats.CoalescedRequests, snapshot.RenderUpdates,
            snapshot.FrameUpdates, snapshot.RenderedFrames, snapshot.AvgRenderMs,
            snapshot.MaxRenderMs, snapshot.AvgBitmapMs, snapshot.MaxBitmapMs,
            snapshot.AvgSpoutMs, snapshot.MaxSpoutMs, snapshot.Width,
            snapshot.Height, snapshot.SpoutEnabled);

        if (PlaybackPerformanceWarningPolicy.ShouldWarnDisplayedFps(snapshot, _fps))
        {
            Log.Warning(
                "Playback perf warning: full-resolution bitmap publication FPS is below source FPS expectedFps={ExpectedFps:F3} displayedFps={DisplayedFps:F2} playbackRate={PlaybackRate:F3}",
                _fps, snapshot.DisplayedFps, snapshot.PlaybackRate);
        }

        if (PlaybackPerformanceWarningPolicy.ShouldWarnPlaybackRate(snapshot))
        {
            Log.Warning(
                "Playback perf warning: playback clock is slower than realtime playbackRate={PlaybackRate:F3} displayedFps={DisplayedFps:F2} expectedFps={ExpectedFps:F3}",
                snapshot.PlaybackRate, snapshot.DisplayedFps, _fps);
        }
    }

    private void ResetPlaybackPerformanceStats()
    {
        _playbackPerformanceStats.Reset();
        _renderSession.ResetUpdateStats();
    }

    private void ResetPlayerStateForNewTrack()
    {
        _renderSession.Invalidate();
        _metadataFetched = false;
        _duration = 0;
        _fps = 0;
        _metaLine = "";
        _osdUpdateState.Reset();
        _renderSession.ResetDisplay();
        _renderSession.ResetUpdateStats();
        ResetPlaybackPerformanceStats();
        _seekState.Clear();
        _endAdvanceTriggered = false;
    }

    // ── ユーティリティ ────────────────────────────────────────────

    private static (double ScaleX, double ScaleY) GetDpiScale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
    }

}
