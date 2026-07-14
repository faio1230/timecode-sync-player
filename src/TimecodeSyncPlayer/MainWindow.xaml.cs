using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer;

public partial class MainWindow : Window, IDisposable, IPlaybackController
{
    // ── mpv ──────────────────────────────────────────────────────
    private IntPtr            _mpv             = IntPtr.Zero;
    private IntPtr            _renderCtx       = IntPtr.Zero;
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
    private int               _videoWidth      = 0;
    private int               _videoHeight     = 0;
    private readonly PixelBufferManager _bufferManager;
    private FrameRenderer _frameRenderer = null!;
    private readonly PlaybackPerformanceStats _playbackPerformanceStats;
    private readonly RenderFramePerformanceRecorder _renderFramePerformanceRecorder;
    private readonly RenderFrameDisplayUpdater _renderFrameDisplayUpdater;
    private readonly RenderedFrameFreezeBufferCopier _renderedFrameFreezeBufferCopier;
    private readonly RenderFramePublishPipeline _renderFramePublishPipeline;
    private readonly StartupBufferInitializer _startupBufferInitializer;
    private readonly MpvRenderFrameExecutor _mpvRenderFrameExecutor;
    private readonly RenderFrameCoordinator _renderFrameCoordinator;

    // レンダーパラム用の永続バッファ（PixelBufferManager に移動）
    private MpvRenderNative.MpvRenderParam[]? _renderParams;

    // 更新コールバック（GC に回収されないようフィールドに保持する）
    private MpvRenderNative.MpvRenderUpdateFn? _updateCallback;
    private readonly IRenderUpdateScheduler _renderUpdateScheduler;
    private readonly IMediaDurationReader _mediaDurationReader;
    private readonly PlaylistDurationBackfillService _playlistDurationBackfillService;
    private readonly PlaylistLoadCoordinator _playlistLoadCoordinator;
    private readonly MpvStartupPropertyApplier _mpvStartupPropertyApplier;
    private readonly MpvSessionInitializer _mpvSessionInitializer;
    private readonly ProjectLoadApplicator _projectLoadApplicator;
    private readonly ProjectSaveExecutor _projectSaveExecutor;
    private readonly IMpvApi _mpvApi;
    private readonly IMpvRenderApi _mpvRenderApi;

    // ── Spout ─────────────────────────────────────────────────────
    private readonly ISpoutOutput _spoutOutput;
    private readonly SpoutFramePublisher _spoutFramePublisher;

    // ── LTC ───────────────────────────────────────────────────────
    private double _lastLtcSeconds;
    private readonly ILtcMonitor _ltcMonitor;
    private readonly TimecodeSyncService _syncService;
    private readonly LtcFrameProcessor _ltcFrameProcessor;
    private readonly FileLoadStabilityLogState _fileLoadStabilityLogState = new(TimeSpan.FromSeconds(1));
    private readonly ContinueModeQueryLogState _continueModeQueryLogState = new(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
    private readonly GapPlaybackCommandExecutor _gapPlaybackCommandExecutor;
    private bool _disposed;
    private readonly GapFreezeHandler _gapFreezeHandler;

    // ── Constants ──────────────────────────────────────────────────

    // Seek debounce timing
    private const double SeekDebounceMs = 250.0;
    private const double LoadfileReloadDebounceMs = 1000.0;

    // Fallback render size
    private const int FallbackRenderSize = 16;

    // Render pixel format
    private const string RenderPixelFormat = "bgr0";

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
    private const string IconPlay = "▶";
    private const string IconPause = "⏸";

    // Timer interval (ms)
    private const int TimerIntervalMs = 100;

    // Additional repeated strings
    private const string MpvCommandNoOsd = "no-osd";
    private const string MpvCommandStop = "stop";
    private const string DefaultTimeLabel = "0:00 / 0:00";
    private const string TimelineOnLabel = "Timeline ON";
    private const string TimelineOffLabel = "Timeline OFF";
    private const string SyncOnLabel = "Sync ON";
    private const string SyncOffLabel = "Sync OFF";
    private const string SpoutOnLabel = "Spout ON";
    private const string SpoutOffLabel = "Spout OFF";

    // Additional numeric constants
    private const double DefaultFallbackFps = 30.0;
    // CLI --save-project の起動シーケンス待機時間
    private const int SaveProjectDelayMs = 3000; // playlist ロード完了の暫定待機時間

    // ── Playlist ──────────────────────────────────────────────────
    private readonly PlaylistState _playlist;
    private Guid?                  _loadedTrackId;
    private bool                   _endAdvanceTriggered;
    private System.Windows.Point?  _playlistDragStartPoint;

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
        SpoutFramePublisher spoutFramePublisher,
        IMediaDurationReader mediaDurationReader,
        PlaylistDurationBackfillService playlistDurationBackfillService,
        PlaylistLoadCoordinator playlistLoadCoordinator,
        MpvStartupPropertyApplier mpvStartupPropertyApplier,
        MpvSessionInitializer mpvSessionInitializer,
        ProjectLoadApplicator projectLoadApplicator,
        ISeekBarUpdateState seekState,
        OsdUpdateState osdUpdateState,
        PlaybackPerformanceStats playbackPerformanceStats,
        RenderFramePerformanceRecorder renderFramePerformanceRecorder,
        PixelBufferManager bufferManager,
        StartupBufferInitializer startupBufferInitializer,
        RenderedFrameFreezeBufferCopier renderedFrameFreezeBufferCopier,
        IRenderUpdateScheduler renderUpdateScheduler,
        IMpvApi mpvApi,
        IMpvRenderApi mpvRenderApi)
    {
        _ltcMonitor = ltcMonitor;
        _playlist = playlist;
        _syncService = syncService;
        _ltcFrameProcessor = ltcFrameProcessor;
        _gapPlaybackCommandExecutor = gapPlaybackCommandExecutor;
        _gapFreezeHandler = gapFreezeHandler;
        _settingsManager = settingsManager;
        _spoutOutput = spoutOutput;
        _spoutFramePublisher = spoutFramePublisher;
        _mpvRenderApi = mpvRenderApi;
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
        _renderFramePerformanceRecorder = renderFramePerformanceRecorder;
        _bufferManager = bufferManager;
        _startupBufferInitializer = startupBufferInitializer;
        _renderedFrameFreezeBufferCopier = renderedFrameFreezeBufferCopier;
        _renderFrameDisplayUpdater = new RenderFrameDisplayUpdater(
            (width, height) => _frameRenderer.UpdateFromPixelBuffer(width, height),
            (width, height) => Log.Information("RenderFrame: first frame displayed {W}x{H}", width, height));
        _renderFramePublishPipeline = new RenderFramePublishPipeline(
            (width, height) => _renderFrameDisplayUpdater.Update(width, height),
            (pixels, width, height) => _spoutFramePublisher.Publish(pixels, width, height),
            measurement => _renderFramePerformanceRecorder.Record(measurement),
            (state, width, height) => _renderedFrameFreezeBufferCopier.CopyIfNeeded(state, width, height));
        _mpvRenderFrameExecutor = new MpvRenderFrameExecutor(
            () => _mpvRenderApi.RenderContextRender(_renderCtx, _renderParams!));
        _renderFrameCoordinator = new RenderFrameCoordinator(
            decideSize: () => RenderFrameSizePolicy.Decide(_videoWidth, _videoHeight, FallbackRenderSize),
            ensurePixelBuffer: (width, height) => _bufferManager.EnsurePixelBuffer(width, height),
            buildRenderParameters: (width, height) => RenderFrameParameterBuilder.Build(_bufferManager, _renderParams!, _mpvRenderApi, width, height),
            renderFrame: () => _mpvRenderFrameExecutor.Render(),
            decidePublish: RenderFramePublishPolicy.Decide,
            logRenderFailure: rc => Log.Debug("mpv_render_context_render: rc={Rc}", rc),
            publishFrame: (pixPtr, width, height, elapsedMs) => _renderFramePublishPipeline.Publish(
                pixPtr,
                width,
                height,
                elapsedMs,
                _spoutOutput.IsEnabled,
                _gapFreezeHandler.CurrentState));
        _renderUpdateScheduler = renderUpdateScheduler;
        _mpvApi = mpvApi;

        _vm = new MainViewModel();
        _vm.Player   = new PlayerViewModel(this);
        _vm.Playlist = new PlaylistViewModel(_playlist, _mediaDurationReader);
        _vm.Sync     = new SyncViewModel(_ltcMonitor);
        DataContext  = _vm;

        _vm.Sync.StartLtcFailed += (_, ex) =>
        {
            Log.Warning(ex, "LTC monitor start failed");
            MessageBox.Show("LTC入力の開始に失敗しました。デバイス接続を確認してください。",
                "LTC Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        };

        _vm.Sync.SyncEnabledChanged += (_, enabled) =>
        {
            if (!enabled) _syncService.ClearSeekState();
            Log.Information("Timecode sync {State}", enabled ? "enabled" : "disabled");
        };

        _vm.Sync.PropertyChanged += (_, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(SyncViewModel.SyncMode):
                    _ltcFrameProcessor.ResetDiagnostics();
                    _syncService.ClearSeekState();
                    Log.Information("Sync mode changed to {Mode}", _vm.Sync.SyncMode);
                    break;
                case nameof(SyncViewModel.LtcFpsMode):
                    _ltcFrameProcessor.ResetForFpsMode(_vm.Sync.LtcFpsMode);
                    Log.Information("LTC fps mode changed mode={Mode}", _vm.Sync.LtcFpsMode);
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
            PlaylistList.SelectedIndex = _vm.Playlist.SelectedIndex;
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
    }

    internal MainViewModel ViewModel => _vm;

    private readonly AppSettingsManager _settingsManager;

    private void Window_Loaded(object sender, RoutedEventArgs e)
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

        // CLI 引数: --open と --playlist に対応（--vo は SW レンダーに切り替えたため不要）
        AppLaunchArguments launchArguments = AppLaunchArguments.Parse(Environment.GetCommandLineArgs());

        var spoutUiApplicator = new SpoutStartupUiApplicator(
            setButtonEnabled: enabled => BtnSpout.IsEnabled = enabled,
            setToggleLabel: label => _vm.Sync.SpoutToggleLabel = label);
        var sessionInitializer = new WindowLoadedSessionInitializer(
            initializeMpvSession: _mpvSessionInitializer.Initialize,
            assignMpv: mpv => _mpv = mpv,
            createRenderContext: CreateRenderContext,
            allocateRenderParameters: () => _renderParams = new MpvRenderNative.MpvRenderParam[5],
            initializeSpout: () => SpoutStartupState.FromInitializationResult(_spoutOutput.TryInitialize()),
            applySpoutStartupState: spoutUiApplicator.Apply,
            initializeFrameRenderer: () =>
            {
                _frameRenderer = new FrameRenderer(_bufferManager, _spoutOutput);
                _frameRenderer.BitmapChanged += bmp => VideoImage.Source = bmp;
            },
            startTimer: () => _timer = StartupTimerFactory.CreateStartedTimer(TimeSpan.FromMilliseconds(TimerIntervalMs), OnTick),
            initializeStartupBuffer: () => _startupBufferInitializer.Initialize(RenderPixelFormat),
            initializeTimeline: InitializeTimeline,
            showError: ShowWindowLoadedSessionInitializationError);
        if (!sessionInitializer.Initialize())
            return;

        ProjectLaunchActionPlan launchActionPlan = ProjectLaunchActionPlanner.Decide(launchArguments);
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
            logSaveCompleted: path => Log.Information("--save-project completed: {Path}", path),
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

        ApplyLoadedProject(project);
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

    private bool CreateRenderContext()
    {
        // MPV_RENDER_PARAM_API_TYPE = "sw"
        IntPtr swStr = Marshal.StringToHGlobalAnsi(_mpvRenderApi.MpvRenderApiTypeSw);
        MpvRenderNative.MpvRenderParam[] initParams = RenderContextParameterBuilder.BuildSoftwareBackendParams(_mpvRenderApi, swStr);

        int rc = _mpvRenderApi.RenderContextCreate(out _renderCtx, _mpv, initParams);
        Marshal.FreeHGlobal(swStr);

        RenderContextCreateResult result = RenderContextCreateResult.FromReturnCode(_renderCtx, rc);
        if (!result.Success)
        {
            Log.Error("mpv_render_context_create 失敗: rc={Rc}", rc);
            return false;
        }

        // 更新コールバックを登録（mpv 内部スレッドから呼ばれる）。
        // タイマー（Background）と同じ優先度に下げ、UI更新が飢餓状態にならないようにする。
        _updateCallback = _ =>
        {
            if (_renderUpdateScheduler.RequestDispatch())
                Dispatcher.BeginInvoke(DispatcherPriority.Background, OnRenderUpdate);
        };
        _mpvRenderApi.RenderContextSetUpdateCallback(
            _renderCtx, _updateCallback, IntPtr.Zero);

        Log.Information("mpv SW レンダーコンテキスト作成完了");
        return true;
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
            LoadCurrentPlaylistTrack);
    }

    private void BtnRefreshLtcDevices_Click(object sender, RoutedEventArgs e)
    {
        RefreshLtcDevices();
    }

    private void AutoOffsetCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        bool autoOffset = AutoOffsetCheckBox.IsChecked == true;
        _ = _settingsManager.UpdateAsync(s => s with { AutoOffsetOnAdd = autoOffset });
    }

    private void RefreshLtcDevices()
    {
        string? previousSelection = LtcDeviceCombo.SelectedItem as string;
        LtcDeviceCombo.Items.Clear();

        try
        {
            IReadOnlyList<string> deviceNames = _ltcMonitor.GetCaptureDeviceNames();
            foreach (string name in deviceNames)
                LtcDeviceCombo.Items.Add(name);

            LtcDeviceCombo.SelectedIndex =
                LtcDeviceListRefreshPlanner.ResolveSelectedIndex(previousSelection, deviceNames);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "LTC capture device enumeration failed");
            _vm.Sync.LtcFormatText = "LTC デバイス列挙失敗";
        }
    }

    private void LtcMonitor_FrameReceived(object? sender, LtcFrameReceivedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            LtcFrameProcessingResult processed = _ltcFrameProcessor.Process(e, _vm.Sync.LtcFpsMode);

            _vm.Sync.LtcTimecodeText = processed.TimecodeText;
            _vm.Sync.LtcRealTimeText = processed.RealTimeText;
            double resolvedSeconds = processed.ResolvedSeconds;
            _lastLtcSeconds = resolvedSeconds;
            _vm.Sync.LtcFormatText = processed.FormatText;
            if (processed.ShouldLogFps)
                LogTimecodeFps(e.Fps, e.Timecode.DropFrame, processed.ResolvedFps);
            LogTimecodeFrameDiagnosticIfNeeded(e, processed);

            if (!processed.ShouldApplySync)
            {
                Log.Information(
                    "Timecode sync skipped due to LTC frame diagnostic status={Status} tc={Timecode} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2}",
                    processed.Diagnostic.Status, e.Timecode, resolvedSeconds,
                    processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames);
                return;
            }

            ApplyTimecodeSync(resolvedSeconds);
        });
    }

    private void LogTimecodeFps(double detectedFps, bool dropFrame, double resolvedFps)
    {
        Log.Information(
            "LTC fps resolved mode={Mode} detectedFps={DetectedFps:F3} dropFrame={DropFrame} resolvedFps={ResolvedFps:F3}",
            _vm.Sync.LtcFpsMode, detectedFps, dropFrame, resolvedFps);
    }

    private void LogTimecodeFrameDiagnosticIfNeeded(LtcFrameReceivedEventArgs e, LtcFrameProcessingResult processed)
    {
        if (processed.Diagnostic.Status is TimecodeFrameDiagnosticStatus.Initial or TimecodeFrameDiagnosticStatus.Normal)
            return;

        Log.Warning(
            "LTC frame diagnostic status={Status} tc={Timecode} rawSeconds={RawSeconds:F3} resolvedSeconds={ResolvedSeconds:F3} deltaSeconds={DeltaSeconds:F3} deltaFrames={DeltaFrames:F2} detectedFps={DetectedFps:F3} resolvedFps={ResolvedFps:F3} mode={Mode}",
            processed.Diagnostic.Status, e.Timecode, e.RealTimeSeconds, processed.ResolvedSeconds,
            processed.Diagnostic.DeltaSeconds, processed.Diagnostic.DeltaFrames, e.Fps, processed.ResolvedFps,
            _vm.Sync.LtcFpsMode);
    }

    private void ApplyTimecodeSync(double ltcSeconds)
    {
        if (_mpv == IntPtr.Zero)
            return;

        if (_vm.Sync.SyncMode == SyncMode.Continue)
        {
            ApplyContinueModeSync(ltcSeconds);
        }
        else
        {
            ApplySingleModeSync(ltcSeconds);
        }
    }

    private void ApplySingleModeSync(double ltcSeconds)
    {
        var coordinator = new SingleModeSyncCoordinator(
            _syncService,
            getTimePos: () =>
            {
                int rc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double playbackSeconds);
                return (rc, playbackSeconds);
            },
            buildPlaybackState: playbackSeconds => new SyncPlaybackState(
                SyncEnabled: _vm.Sync.SyncEnabled,
                HasCurrentTrack: _playlist.Current != null,
                IsSeeking: _seekBarInteraction.IsSeeking,
                PlaybackSeconds: playbackSeconds,
                DurationSeconds: _duration,
                VideoFps: _fps,
                TimecodeFps: _ltcFrameProcessor.LastTimecodeFps),
            seekTo: target => SeekTo(target));
        coordinator.Apply(ltcSeconds);
    }

    private void ApplyContinueModeSync(double ltcSeconds)
    {
        if (!_vm.Sync.SyncEnabled) return;
        if (_seekBarInteraction.IsSeeking) return;

        if (_gapFreezeHandler.CurrentState is GapState.EnteringFreeze or GapState.WaitingForFrameStep)
        {
            _timelinePanel?.UpdatePlaybackPosition(ltcSeconds);
            return;
        }

        var result = _playlist.FindTrackAtTimelinePosition(ltcSeconds);

        string? queryTrackName = result.Track?.Name;
        if (_continueModeQueryLogState.ShouldLog(result.Status, queryTrackName, result.MediaPositionSeconds, DateTime.UtcNow))
        {
            Log.Debug("Continue mode query result: status={Status} track={Track} mediaPos={MediaPos:F3}",
                result.Status, queryTrackName ?? "null", result.MediaPositionSeconds);
        }

        switch (result.Status)
        {
            case TimelineQueryStatus.OnTrack:
                HandleOnTrackSync(result, ltcSeconds);    // Gap 終了処理は内部で実施
                break;

            case TimelineQueryStatus.Gap:
                HandleGapSync(result);
                break;

            case TimelineQueryStatus.NoTracks:
                HandleNoTracksSync();
                break;
        }
    }

    private void HandleOnTrackSync(TimelineQueryResult result, double ltcSeconds)
    {
        var coordinator = new ContinueOnTrackCoordinator(
            _syncService,
            _fileLoadStabilityLogState,
            new ContinueOnTrackEffects(
                DecideGapExit: () => _gapFreezeHandler.DecideGapExit(),
                SeekTo: target => SeekTo(target),
                ResumeMpvPause: () => _mpvApi.SetPropertyString(_mpv, "pause", MpvValueNo),
                ApplyPauseState: paused => ApplyPauseState(paused),
                ShowOsdBar: () => _mpvApi.SetPropertyString(_mpv, MpvPropertyOsdBar, MpvValueYes),
                UpdateCurrentTrackLabel: () => UpdateCurrentTrackLabel(),
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id =>
                {
                    _loadedTrackId = id;
                    if (_timelinePanel != null)
                        _timelinePanel.LoadedTrackId = _loadedTrackId;
                },
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
                    TimecodeFps: _ltcFrameProcessor.LastTimecodeFps)));
        coordinator.Handle(result, ltcSeconds);
    }

    private void HandleGapSync(TimelineQueryResult result)
    {
        var action = _gapFreezeHandler.DecideGapEnter(result, _vm.Sync.GapBehavior, _loadedTrackId, _fps, _duration);
        var dispatcher = new GapEnterActionDispatcher(new GapEnterActionHandlers(
            EnterBlackGap,
            EnterForceBlack,
            () => _frameRenderer.RenderGapFreeze(_videoWidth, _videoHeight),
            StartGapFreezeCaptureForCurrentTrack,
            LoadPreviousTrackFinalFrameForGapFreeze));
        dispatcher.Execute(action, result);
        UpdateCurrentTrackLabel();
    }

    private void EnterBlackGap()
    {
        _endAdvanceTriggered = false;
        _gapPlaybackCommandExecutor.PauseForGap(_mpv);
        ApplyPauseState(true);
        _frameRenderer.RenderBlack(_videoWidth, _videoHeight);
        Log.Information("Continue mode: entered gap, rendering black frame");
    }

    private void EnterForceBlack()
    {
        _endAdvanceTriggered = false;
        _bufferManager.ClearGapFreezeFrame();
        _gapPlaybackCommandExecutor.PauseForGap(_mpv);
        ApplyPauseState(true);
        _frameRenderer.RenderBlack(_videoWidth, _videoHeight);
        Log.Information("Continue mode: gap, forcing black frame");
    }

    private void StartGapFreezeCaptureForCurrentTrack(TimelineQueryResult result, GapEnterAction action)
    {
        _endAdvanceTriggered = false;
        _gapPlaybackCommandExecutor.PauseForGap(_mpv);
        ApplyPauseState(true);

        PlaylistTrack? previousTrack = result.PreviousTrack;
        double target = action.TargetSeconds ?? 0;
        double duration = action.DurationSeconds ?? _duration;
        double fps = action.Fps ?? (_fps > 0 ? _fps : DefaultFallbackFps);
        Guid? previousTrackId = action.TrackId ?? previousTrack?.Id;

        if (target <= 0)
        {
            Log.Information("Continue mode: gap freeze activated, holding current frame because duration is unavailable");
            _gapFreezeHandler.OnFreezeComplete(_loadedTrackId);
            _frameRenderer.RenderGapFreeze(_videoWidth, _videoHeight);
            return;
        }

        bool seekSuccess = SeekTo(target);
        if (seekSuccess)
        {
            _gapFreezeHandler.EnterFreezeCapture(previousTrackId ?? _loadedTrackId, target, previousTrack?.FilePath);
            Log.Information(
                "Continue mode: entering gap freeze, waiting for final frame target={Target:F3} duration={Duration:F3} fps={Fps:F3}",
                target, duration, fps);
        }
        else
        {
            Log.Warning("Continue mode: gap freeze final-frame seek failed, holding current frame");
            _gapFreezeHandler.ForceFreezeComplete();
        }
    }

    private void EnterNoTracksFreeze()
    {
        _gapPlaybackCommandExecutor.PauseForGap(_mpv);
        ApplyPauseState(true);

        int durRc = _mpvApi.GetProperty(_mpv, "duration", _mpvApi.FormatDouble, out double duration);
        if (durRc == 0 && duration > 0)
        {
            double fps = _fps > 0 ? _fps : DefaultFallbackFps;
            double frameSeconds = 1.0 / fps;
            double target = Math.Max(0, duration - frameSeconds);
            bool seekSuccess = SeekTo(target);
            if (seekSuccess)
            {
                _gapFreezeHandler.EnterFreezeCapture(_loadedTrackId, target, null);
                Log.Information("Continue mode: no tracks, entering gap freeze target={Target:F3} duration={Duration:F3}", target, duration);
            }
            else
            {
                _gapFreezeHandler.CurrentState = GapState.ForceBlack;
                _frameRenderer.RenderBlack(_videoWidth, _videoHeight);
                Log.Warning("Continue mode: no tracks, gap freeze seek failed");
            }
        }
        else
        {
            _gapFreezeHandler.CurrentState = GapState.ForceBlack;
            _frameRenderer.RenderBlack(_videoWidth, _videoHeight);
        }
        Log.Information("Continue mode: no tracks, freezing last frame");
    }


    private void LoadPreviousTrackFinalFrameForGapFreeze(PlaylistTrack previousTrack, double target, double duration, double fps)
    {
        _endAdvanceTriggered = false;
        if (_mpv == IntPtr.Zero)
            return;

        GapLoadCommandResult commandResult = _gapPlaybackCommandExecutor.LoadPausedAt(_mpv, previousTrack.FilePath, target);

        if (commandResult.LoadRc != 0)
        {
            Log.Warning(
                "Continue mode: gap freeze previous-track load failed track={Track} target={Target:F3} loadRc={LoadRc} pauseRc={PauseRc}",
                previousTrack.Name, target, commandResult.LoadRc, commandResult.PauseRc);
            _gapFreezeHandler.ForceFreezeComplete();
            return;
        }

        _loadedTrackId = previousTrack.Id;
        if (_timelinePanel != null)
            _timelinePanel.LoadedTrackId = _loadedTrackId;

        ApplyPauseState(true);
        ResetPlayerStateForNewTrack();
        _duration = duration;
        _fps = fps;
        _gapFreezeHandler.EnterFreezeCaptureWithReload(previousTrack.Id, target, previousTrack.FilePath);

        Log.Information(
            "Continue mode: loading previous track final frame for gap freeze track={Track} target={Target:F3} duration={Duration:F3} fps={Fps:F3} loadRc={LoadRc} pauseRc={PauseRc}",
            previousTrack.Name, target, duration, fps, commandResult.LoadRc, commandResult.PauseRc);
    }

    private void HandleNoTracksSync()
    {
        var action = _gapFreezeHandler.DecideNoTracksEnter(_vm.Sync.GapBehavior, _loadedTrackId);

        switch (action.Type)
        {
            case GapEnterActionType.EnterFreezeFromLastTrack:
                EnterNoTracksFreeze();
                break;
            case GapEnterActionType.ForceBlack:
                EnterForceBlack();
                break;
        }
        UpdateCurrentTrackLabel();
    }

    private void LtcMonitor_Stopped(object? sender, Exception? exception)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _vm.Sync.LtcTimecodeText = "--:--:--:--";
            _vm.Sync.LtcRealTimeText = "-.--- s";
            _vm.Sync.LtcFormatText = exception == null ? "LTC 停止中" : "LTC 停止エラー";
            _vm.Sync.IsLtcRunning = false;
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
        if (_playlist.Select(PlaylistList.SelectedIndex))
        {
            SyncPlaylistSelection();
            LoadCurrentPlaylistTrack();
        }
    }

    private void PlaylistList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _playlistDragStartPoint = e.GetPosition(PlaylistList);
    }

    private void PlaylistList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PlaylistTrack? track = PlaylistList.SelectedItem as PlaylistTrack;
        System.Windows.Point startPoint = _playlistDragStartPoint.GetValueOrDefault();
        System.Windows.Point currentPoint = e.GetPosition(PlaylistList);
        double dx = Math.Abs(currentPoint.X - startPoint.X);
        double dy = Math.Abs(currentPoint.Y - startPoint.Y);
        if (!PlaylistDragInitiationPolicy.ShouldBeginDrag(
                _playlistDragStartPoint.HasValue,
                e.LeftButton == System.Windows.Input.MouseButtonState.Pressed,
                track != null,
                dx,
                dy,
                SystemParameters.MinimumHorizontalDragDistance,
                SystemParameters.MinimumVerticalDragDistance))
            return;

        DragDrop.DoDragDrop(PlaylistList, track!, DragDropEffects.Move);
        _playlistDragStartPoint = null;
    }

    private void PlaylistList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PlaylistTrack))
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void PlaylistList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PlaylistTrack)) is not PlaylistTrack draggedTrack)
            return;

        int fromIndex = _playlist.Tracks.IndexOf(draggedTrack);
        int hitIndex = GetPlaylistIndexFromPoint(e.GetPosition(PlaylistList));
        int toIndex = PlaylistDropTargetPolicy.ResolveTargetIndex(hitIndex, _playlist.Tracks.Count);
        if (toIndex < 0)
            return;

        if (!_playlist.MoveTrack(fromIndex, toIndex)) return;

        PlaylistList.SelectedIndex = toIndex;
        UpdateCurrentTrackLabel();
        UpdatePlaylistTimelineDisplay();
        e.Handled = true;
    }

    private int GetPlaylistIndexFromPoint(System.Windows.Point point)
    {
        var element = PlaylistList.InputHitTest(point) as DependencyObject;
        while (element != null)
        {
            if (element is System.Windows.Controls.ListBoxItem item)
                return PlaylistList.ItemContainerGenerator.IndexFromContainer(item);
            element = VisualTreeHelper.GetParent(element);
        }

        return -1;
    }

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
        await ReadDurationsInBackground(loadResult.Paths.ToList());
    }

    private async Task ReadDurationsInBackground(List<string> paths, int startIndex = 0)
    {
        try
        {
            await _playlistDurationBackfillService.BackfillAsync(
                _playlist.Tracks,
                paths,
                startIndex,
                async (trackId, duration) =>
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        bool autoOffset = _settingsManager.Current.AutoOffsetOnAdd;
                        _playlist.UpdateMediaDuration(trackId, duration, recalculate: autoOffset);
                        UpdatePlaylistTimelineDisplay();
                    });
                });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "ReadDurationsInBackground failed");
            _ = Dispatcher.BeginInvoke(() =>
            {
                MessageBox.Show("メディアのduration読み込みに失敗しました。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }

    private async void BtnSaveProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "TimecodeSyncPlayer Project (*.tsp)|*.tsp",
            DefaultExt = "tsp",
            Title = "プロジェクトを保存"
        };

        string? selectedPath = dialog.ShowDialog() == true ? dialog.FileName : null;
        var runner = new ProjectFileActionRunner();
        await runner.SaveAsync(
            selectedPath,
            path => _projectSaveExecutor.SaveAsync(path, _vm.Sync.SyncMode, _vm.Sync.GapBehavior),
            path => Log.Information("プロジェクトを保存しました: {Path}", path),
            ex =>
            {
                Log.Error(ex, "プロジェクトの保存に失敗しました");
                MessageBox.Show("プロジェクトの保存に失敗しました。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
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
        var runner = new ProjectFileActionRunner();
        await runner.LoadAsync(
            selectedPath,
            ProjectSerializer.LoadAsync,
            project => _ = Dispatcher.BeginInvoke(() =>
            {
                StopPlayback();
                ApplyLoadedProject(project);

                SyncPlaylistSelection();
                UpdatePlaylistTimelineDisplay();
                UpdateCurrentTrackLabel();
                LoadCurrentPlaylistTrack();
                _ = ReadDurationsInBackground(_playlist.Tracks.Select(t => t.FilePath).ToList());
            }),
            path => Log.Information("プロジェクトを読み込みました: {Path}", path),
            () => MessageBox.Show("プロジェクトファイルの形式が不正です。", "エラー",
                MessageBoxButton.OK, MessageBoxImage.Error),
            ex =>
            {
                Log.Error(ex, "プロジェクトの読み込みに失敗しました");
                MessageBox.Show("プロジェクトの読み込みに失敗しました。ファイルが破損している可能性があります。", "エラー",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
    }

    private void LoadCurrentPlaylistTrack()
    {
        PlaylistTrack? track = _playlist.Current;
        if (track == null) return;

        _loadedTrackId = track.Id;
        if (_timelinePanel != null)
            _timelinePanel.LoadedTrackId = _loadedTrackId;
        LoadFile(track.FilePath);
        UpdateCurrentTrackLabel();
        UpdatePlaylistTimelineDisplay();
        Log.Information("Playlist track loaded index={Index} name={Name} path={Path}",
            _playlist.CurrentIndex, track.Name, track.FilePath);
    }

    private void SyncPlaylistSelection()
    {
        PlaylistList.SelectedIndex = _playlist.CurrentIndex;
        UpdateCurrentTrackLabel();
    }

    private void UpdateCurrentTrackLabel()
    {
        _vm.Playlist.CurrentTrackLabel = PlaylistCurrentTrackLabelFormatter.Format(
            _vm.Sync.SyncMode,
            _vm.Sync.GapBehavior,
            _gapFreezeHandler.IsInactive,
            _playlist.Tracks,
            _playlist.CurrentIndex,
            _loadedTrackId);
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
            DefaultFallbackFps);
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
    {
        if (_mpv == IntPtr.Zero) return;

        _mpvApi.CommandString(_mpv, MpvCommandStop);
        _mpvApi.SetPropertyString(_mpv, "pause", MpvValueYes);
        ApplyPauseState(true);
        ResetPlayerStateForNewTrack();
        _videoWidth = 0;
        _videoHeight = 0;
        _loadedTrackId = null;
        if (_timelinePanel != null)
            _timelinePanel.LoadedTrackId = null;
        SetSeekBarValueFromPlayer(0);
        _vm.Player.TimeLabel = DefaultTimeLabel;
        _vm.Player.PlayPauseIcon = IconPlay;
        _gapFreezeHandler.ResetAll();
        _bufferManager.ClearGapFreezeFrame();
    }

    private bool LoadFile(string path, double? startPosition = null)
    {
        if (_mpv == IntPtr.Zero) return false;
        bool success;
        if (startPosition.HasValue)
        {
            int loadRc = _mpvApi.CommandString(_mpv, MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition));
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start={Start:F3} loadRc={LoadRc}",
                path, startPosition.Value, loadRc);
        }
        else
        {
            int loadRc = _mpvApi.CommandString(_mpv, MpvPlaybackCommandBuilder.BuildLoadFileCommand(path, startPosition: null));
            int pauseRc = _mpvApi.SetPropertyString(_mpv, "pause", MpvValueNo);
            success = loadRc == 0;
            Log.Information("LoadFile path={Path} start=none loadRc={LoadRc} pauseRc={PauseRc}",
                path, loadRc, pauseRc);
        }
        if (!success) return false;
        ApplyPauseState(false);
        ResetPlayerStateForNewTrack();
        _videoWidth  = 0;
        _videoHeight = 0;
        _gapFreezeHandler.Reset();
        SetSeekBarValueFromPlayer(0);
        _vm.Player.TimeLabel = DefaultTimeLabel;
        return true;
    }

    // ── Playback helpers ───────────────────────────────────────────

    private bool SeekTo(double seconds, bool suppressOsd = true)
    {
        try
        {
            var prefix = suppressOsd ? MpvCommandNoOsd : "";
            var command = $"{prefix} seek {seconds.ToString("F3", CultureInfo.InvariantCulture)} {MpvSeekModeAbsolute}".Trim();
            int rc = _mpvApi.CommandString(_mpv, command);
            if (rc != 0)
            {
                Log.Warning("Seek failed: rc={Rc}, target={Target}", rc, seconds);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek error: target={Target}", seconds);
            return false;
        }
    }

    // ── IPlaybackController ────────────────────────────────────────────────
    void IPlaybackController.TogglePlayPause()
    {
        if (_mpv == IntPtr.Zero) return;
        PlaybackPauseChange change = _playbackControl.TogglePlayPause();
        _mpvApi.SetPropertyString(_mpv, "pause", change.MpvPauseValue);
        ResetPlaybackPerformanceStats();
        _vm.Player.PlayPauseIcon = change.PlayPauseIcon;
    }

    void IPlaybackController.SeekRelative(double seconds)
    {
        if (_mpv == IntPtr.Zero) return;
        _mpvApi.CommandString(_mpv, $"seek {seconds} {MpvSeekModeRelative}");
    }

    void IPlaybackController.CycleSpeed()
    {
        if (_mpv == IntPtr.Zero) return;
        PlaybackSpeedChange change = _playbackControl.CycleSpeed();
        _mpvApi.SetPropertyString(_mpv, "speed", change.Speed.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _vm.Player.SpeedLabel = change.Label;
    }

    private void ApplyPauseState(bool paused)
    {
        PlaybackPauseChange change = _playbackControl.SetPaused(paused);
        _vm.Player.PlayPauseIcon = change.PlayPauseIcon;
    }

    // ── Spout ─────────────────────────────────────────────────────

    private void BtnSpout_Click(object sender, RoutedEventArgs e)
    {
        _spoutOutput.IsEnabled = !_spoutOutput.IsEnabled;
        _vm.Sync.SpoutToggleLabel = ToggleLabelFormatter.Format(_spoutOutput.IsEnabled, SpoutOnLabel, SpoutOffLabel);
        Log.Information("Spout 出力: {State}", _spoutOutput.IsEnabled ? "ON" : "OFF");
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

        _syncService.ClearSeekState();
        bool success = SeekTo(e.TargetSeconds);
        Log.Information("Timeline seek target={Target:F3} trackIndex={TrackIndex} success={Success}",
            e.TargetSeconds, e.TrackIndex, success);
    }

    // ── シークバー操作 ────────────────────────────────────────────

    private void Seek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
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
        if (_mpv == IntPtr.Zero) return;

        int durationRc = _mpvApi.GetProperty(_mpv, "duration", _mpvApi.FormatDouble, out double dur);
        if (durationRc == 0 && SeekBarUpdateState.IsUsableDuration(dur))
            _duration = dur;

        if (!_metadataFetched && _duration > 0)
            FetchMetadata();

        // Gap 状態では mpv のレンダーコールバックが止まるため、
        // タイマーでタイムライン位置を更新する
        if (!_gapFreezeHandler.IsInactive
            && _vm.Sync.SyncMode == SyncMode.Continue
            && _lastLtcSeconds > 0)
        {
            _timelinePanel?.UpdatePlaybackPosition(_lastLtcSeconds);
        }

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
                    _loadedTrackId = nextTrack.Id;
                    if (_timelinePanel != null)
                        _timelinePanel.LoadedTrackId = _loadedTrackId;
                }
                return;
            }

            case ContinueModeEndAdvanceAction.EnterNoTracks:
                _endAdvanceTriggered = true;
                Log.Information("Continue mode: reached final track end, entering no-tracks gap state");
                HandleNoTracksSync();
                return;
        }
    }

    // ── SW レンダーコールバック（フレーム描画） ───────────────────

    /// <summary>
    /// mpv のレンダー更新コールバックから Dispatcher 経由で呼ばれる（UI スレッド）。
    /// 新しいフレームがあれば RenderFrame を呼び、フレームごとのUI更新を行う。
    /// </summary>
    private void OnRenderUpdate()
    {
        try
        {
            if (_renderCtx == IntPtr.Zero) return;
            ulong flags = _mpvRenderApi.RenderContextUpdate(_renderCtx);
            bool hasFrame = (flags & _mpvRenderApi.MpvRenderUpdateFrame) != 0;
            _playbackPerformanceStats.RecordRenderUpdate(hasFrame);

            if (_gapFreezeHandler.CurrentState == GapState.EnteringFreeze && hasFrame)
            {
                int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double actualPos);
                GapFrameCaptureDecision decision = GapFrameCaptureCoordinator.Decide(
                    _gapFreezeHandler.CurrentState,
                    hasFrame,
                    IsCurrentMpvPathExpectedForGapFreeze(),
                    timePosRc == 0,
                    actualPos,
                    _gapFreezeHandler.PendingTargetSeconds,
                    _fps > 0 ? _fps : DefaultFallbackFps);

                if (decision != GapFrameCaptureDecision.SendFrameStep)
                {
                    Log.Debug("Continue mode: gap freeze seek not yet complete actual={Actual:F3} target={Target:F3}", actualPos, _gapFreezeHandler.PendingTargetSeconds);
                    return;
                }

                _mpvApi.CommandString(_mpv, $"{MpvCommandNoOsd} frame-step");
                _gapFreezeHandler.CurrentState = GapState.WaitingForFrameStep;
                Log.Debug("Continue mode: seek complete, sent frame-step actual={Actual:F3} target={Target:F3}", actualPos, _gapFreezeHandler.PendingTargetSeconds);
            }
            else if (_gapFreezeHandler.CurrentState == GapState.WaitingForFrameStep && hasFrame)
            {
                int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double actualPos);
                GapFrameCaptureDecision decision = GapFrameCaptureCoordinator.Decide(
                    _gapFreezeHandler.CurrentState,
                    hasFrame,
                    IsCurrentMpvPathExpectedForGapFreeze(),
                    timePosRc == 0,
                    actualPos,
                    _gapFreezeHandler.PendingTargetSeconds,
                    _fps > 0 ? _fps : DefaultFallbackFps);

                if (decision != GapFrameCaptureDecision.RenderAndCapture)
                {
                    Log.Debug("Continue mode: frame-step target not reached actual={Actual:F3} target={Target:F3}", actualPos, _gapFreezeHandler.PendingTargetSeconds);
                    return;
                }

                RenderFrame();
                _gapFreezeHandler.OnFreezeComplete(_loadedTrackId);
                _bufferManager.CopyFrozenToGapFreezeFrame(_videoWidth, _videoHeight);
                Log.Information("Continue mode: gap freeze activated, final frame captured");
            }
            else if (hasFrame && _gapFreezeHandler.IsInactive)
            {
                RenderFrame();
            }

            GapRenderFrameDecision gapRenderDecision = GapRenderFramePolicy.Decide(
                _gapFreezeHandler.CurrentState,
                _vm.Sync.GapBehavior,
                _bufferManager.FrozenFrameBuffer != null,
                _videoWidth,
                _videoHeight);
            if (gapRenderDecision == GapRenderFrameDecision.Black)
            {
                _frameRenderer.RenderBlack(_videoWidth, _videoHeight);
            }
            else if (gapRenderDecision == GapRenderFrameDecision.GapFreeze)
            {
                _frameRenderer.RenderGapFreeze(_videoWidth, _videoHeight);
            }

            UpdatePerFrameUI();
        }
        finally
        {
            if (_renderUpdateScheduler.CompleteDispatch() && _renderCtx != IntPtr.Zero)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, OnRenderUpdate);
        }
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
    /// OnRenderUpdate から呼ばれる。
    /// </summary>
    private void UpdatePerFrameUI()
    {
        if (_mpv == IntPtr.Zero) return;

        int timePosRc = _mpvApi.GetProperty(_mpv, "time-pos", _mpvApi.FormatDouble, out double pos);

        double? gapTimelinePosition = PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            _gapFreezeHandler.IsInactive,
            _vm.Sync.SyncMode,
            _lastLtcSeconds);
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
                    _lastLtcSeconds,
                    pos);
                _timelinePanel?.UpdatePlaybackPosition(timelinePosition);
            }
        }
    }

    /// <summary>
    /// 現在の動画フレームをピクセルバッファに描画し、
    /// WriteableBitmap で表示して SpoutOutput に送信する（UI スレッド）。
    /// </summary>
    private void RenderFrame()
    {
        if (_renderCtx == IntPtr.Zero || _mpv == IntPtr.Zero) return;

        if (_renderParams == null) return;

        _renderFrameCoordinator.Render();
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
            disposeRenderContext: () =>
            {
                if (_renderCtx != IntPtr.Zero)
                {
                    _mpvRenderApi.RenderContextFree(_renderCtx);
                    _renderCtx = IntPtr.Zero;
                }
            },
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
            disposeBuffer: () => _bufferManager.Dispose());
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
        if (int.TryParse(widthStr,  out int w) && w > 0) _videoWidth  = w;
        if (int.TryParse(heightStr, out int h) && h > 0) _videoHeight = h;

        if (_videoWidth <= 0 || _videoHeight <= 0)
            return;

        _metadataFetched = true;
        Log.Information("FetchMetadata: {W}x{H} {Fps:F3}fps V:{VCodec} A:{ACodec}",
            _videoWidth, _videoHeight, _fps, vcodec, acodec);

        _metaLine = MetadataDisplayFormatter.FormatMetadataLine(
            _videoWidth,
            _videoHeight,
            _fps,
            vcodec,
            acodec);
        _vm.Player.MetaLine = _metaLine;
    }

    // ── OSD ───────────────────────────────────────────────────────

    private void UpdateOsd(double pos)
    {
        if (_mpv == IntPtr.Zero) return;
        int frame = _fps > 0 ? (int)(pos * _fps) : (int)pos;
        if (!_osdUpdateState.ShouldUpdate(frame, DateTime.UtcNow)) return;
        string timePart = PlaybackTimeFormatter.FormatFrames(pos, _fps);
        string text = MetadataDisplayFormatter.FormatOsdText(timePart, _metaLine);
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
        RenderUpdateSchedulerStats renderStats = _renderUpdateScheduler.ConsumeStats();

        Log.Information(
            "Playback perf elapsed={Elapsed:F2}s expectedFps={ExpectedFps:F3} playbackRate={PlaybackRate:F3} displayedFps={DisplayedFps:F2} ticks={Ticks} renderCallbacks={RenderCallbacks} coalescedRenderCallbacks={CoalescedRenderCallbacks} renderUpdates={RenderUpdates} frameUpdates={FrameUpdates} renderedFrames={RenderedFrames} avgRenderMs={AvgRenderMs:F2} maxRenderMs={MaxRenderMs:F2} avgBitmapMs={AvgBitmapMs:F2} maxBitmapMs={MaxBitmapMs:F2} avgSpoutMs={AvgSpoutMs:F2} maxSpoutMs={MaxSpoutMs:F2} size={Width}x{Height} spoutEnabled={SpoutEnabled}",
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
                "Playback perf warning: displayed FPS is below source FPS expectedFps={ExpectedFps:F3} displayedFps={DisplayedFps:F2} playbackRate={PlaybackRate:F3}",
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
        _renderUpdateScheduler.Reset();
    }

    private void ResetPlayerStateForNewTrack()
    {
        _metadataFetched = false;
        _duration = 0;
        _fps = 0;
        _metaLine = "";
        _osdUpdateState.Reset();
        _renderFrameDisplayUpdater.Reset();
        _renderUpdateScheduler.Reset();
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
