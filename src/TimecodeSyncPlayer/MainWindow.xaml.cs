using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Gst;
using TimecodeSyncPlayer.Output;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer;

public partial class MainWindow : Window, IDisposable, IPlaybackController
{
    // ── 再生 ──────────────────────────────────────────────────────
    private DispatcherTimer?  _timer;
    private readonly PlaybackControlState _playbackControl = new();
    private readonly SeekBarInteractionController _seekBarInteraction = new();
    private readonly MainViewModel _vm;
    private double            _duration        = 0;
    private double            _fps             = 0;
    private bool              _metadataFetched = false;
    // D34: ロード後のメタデータ取得を最大 MetadataFetchRetrySeconds 秒まで 100ms tick で再試行する。
    private DateTime?         _metadataFetchDeadlineUtc;
    private bool              _metadataFetchTimedOutLogged;
    private const int         MetadataFetchRetrySeconds = 5;

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
    private readonly ProjectLoadApplicator _projectLoadApplicator;
    private readonly ProjectFileCoordinator _projectFileCoordinator;
    private readonly IPlaybackApi _playbackApi;
    private readonly GstPlaybackApi _gstPlaybackApi;
    private readonly AudioControlCoordinator _audioControlCoordinator;

    // ── Spout ─────────────────────────────────────────────────────
    private readonly ISpoutOutput _spoutOutput;

    // ── GPU 出力 ──────────────────────────────────────────────────
    private readonly OutputEngine? _outputEngine;
    private readonly GstBackendState _gstBackendState;
    private readonly IGstNativeApi _gstNativeApi;
    // R1 1-2: 再生可否の唯一の判定元。GPU 検出失敗・ワーカー初期化失敗・player 生成失敗を集約する。
    private readonly PlaybackAvailabilityState _playbackAvailability = new();
    private bool _playbackUnavailableDialogShown;
    // D4: ロード安定ゲートが数える「表示経路に到達したフレーム数」の供給元。
    private readonly RenderedFrameCounter _syncGateRenderedFrames;
    private WriteableBitmap? _outputPreviewBitmap;
    // v0.5.1: プレビューが 1 秒以上更新されないときにログへ出す（ギャップ入りで黒が映らなかった件の切り分け）。
    private readonly PreviewStallWatch _previewStallWatch = new(Stopwatch.Frequency);
    private long _lastShownPreviewSequence;
    private int _lastShownPreviewLuma = -1;

    // ── 終了（段階 5.1） ──────────────────────────────────────────
    private ExitDialogHost? _exitDialogHost;
    private ExitCoordinator? _exitCoordinator;
    private MainWindowResourceDisposer? _resourceDisposer;
    private DispatcherTimer? _gpuStatusResetTimer;

    // ── キャンバス設定（段階 4、UI スレッド所有） ──────────────────
    private readonly ProjectCanvasState _projectCanvasState = new();
    private Guid? _contextMenuTrackId;
    private (bool CanChange, string? Tip)? _canvasUiCache;
    private bool _isLoadingCanvasInputs;

    // ── LTC ───────────────────────────────────────────────────────
    private readonly LtcSyncController _ltcSyncController;
    private readonly ILtcMonitor _ltcMonitor;
    private readonly TimecodeSyncService _syncService;
    private readonly SeekingProbe _seekingProbe = new();
    private readonly FileLoadStabilityLogState _fileLoadStabilityLogState = new(TimeSpan.FromSeconds(1));
    private readonly GapPlaybackCommandExecutor _gapPlaybackCommandExecutor;
    private volatile bool _disposed;
    private readonly GapFreezeHandler _gapFreezeHandler;
    private bool _isRefreshingLtcDevices;

    // ── Constants ──────────────────────────────────────────────────

    // Seek debounce timing
    private const double LoadfileReloadDebounceMs = 1000.0;

    // Playback icons
    private const string IconPause = "⏸";

    // Timer interval (ms)
    private const int TimerIntervalMs = 100;

    // Additional repeated strings
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
    /// <summary>
    /// D37-f: 最大ギャップ 1 秒あたりの「シークを出してから絵が実際に動き出すまで」の
    /// 見積もり係数。<b>理論値ではなく調整値</b>。
    ///
    /// D37-g: 当初の 0.3 は<b>着地までしか測っていなかった</b>。着地（位置が目標に到達）の
    /// あと、デコーダからの供給が戻るまでさらに止まる。実測（検証機 2026-09-19、
    /// 停止中の gst.delivery は 0.72 秒で 3 件＝約 4fps）:
    ///   最大ギャップ 6.633 秒 → 着地 1.98 + 停止 0.79 = 2.77 秒（ギャップの 0.42 倍）
    ///   最大ギャップ 0.501 秒 → 着地 0.35 + 停止 0.15 = 0.50 秒（0.1 秒程度の基底が支配的）
    ///   最大ギャップ 0.017 秒 → 停止 0.10 秒（基底のみ）
    /// 停止はギャップに比例し、定数ではない。基底の 0.1 秒は短いギャップの素材でしか
    /// 効かず、そこでは絶対量が小さいので係数 1 本で表す。
    /// </summary>
    private const double SeekCostPerGapSecond = 0.42;

    /// <summary>
    /// D37-f: シーク所要の見積もりの上限。スキャンが壊れた値を返しても、行き先が
    /// 壊れないようにするための歯止め。実測で最も遅い素材でも 2 秒台なので 5 秒で足りる。
    /// </summary>
    private const double MaxSeekCostHintSeconds = 5.0;

    // 0.4.5-C3: 読み込み時にコンテナを読んでキーフレーム分布を測る（デコードしない）。
    // 再生経路には触れない独立したパイプラインなので、ロードの状態機械に影響しない。
    // 0.4.7: 「デコードが追いついていない」表示（表示だけ。同期の制御は変えない）。
    private readonly DecodeHealthMonitor _decodeHealth = new();

    private readonly GopScanCache _gopScanCache =
        new(path => Gst.GstPlaybackApi.ScanGop(path));
    /// <summary>
    /// D37-h: スキャン由来のシーク所要の見積もりを同期へ渡すか。<b>0.4.5 では既定で無効。</b>
    ///
    /// 見積もりは <c>最大ギャップ × 係数</c> で、これは「<b>ファイル中で最悪の場所に着地する</b>」
    /// 前提である。実際にはキーフレームの近くへ落ちることが多く、そのとき<b>大きく行き過ぎる</b>。
    /// 検証機の実測（候補 6）:
    /// <list type="bullet">
    /// <item>追従開始の 1 回目が <b>4/4 とも +2.53〜+2.64 秒の行き過ぎ</b>（見積もり 2.786、実コスト 0.19〜0.31）</item>
    /// <item>その戻りシークが最悪の GOP 区間に着地して 2.2 秒かかる</item>
    /// <item><b>シナリオ試験の失敗が 1 件 → 6 件に増えた。</b>先行量の最大が 0.171 秒 → 3.500 秒になり、
    ///   着地位置そのものがテストの許容（±0.3 秒）を外れる（S-3 で 1 対 1 に追跡済み）</item>
    /// <item>狙いだった M3 の追従開始も、5 回中 1 回が上限 5 秒を超えた（5.043 秒）</item>
    /// </list>
    /// <b>新しい機能が既存の合格を 5 件壊しているので、既定では渡さない。</b>
    /// 正しい直し方は「最大ギャップからの点推定」をやめ、<b>キーフレーム列を見て着地先を選ぶ</b>こと
    /// （docs/analysis/2026-09-19-keyframe-aware-seek-target.md）。0.4.6 で入れる。
    /// 計測用に <c>TCS_SEEK_COST_HINT=on</c> で有効にできる。
    /// </summary>
    private readonly bool _seekCostHintEnabled =
        string.Equals(Environment.GetEnvironmentVariable("TCS_SEEK_COST_HINT"), "on",
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 0.4.5-C3: キーフレーム分布のスキャンを行うか（`TCS_GOP_SCAN=off` で止める）。<b>既定は on。</b>
    ///
    /// 足した理由は 2 つ。
    /// 1. <b>切り分けのため。</b>v0.4.3（スキャン無し）は M8 ×5 で配信の落ち込みが 0/277 窓、
    ///    0.4.5 は 7/292 窓。**時間的には重なっていない**ことは確認済みだが、13.4GB を読み切る
    ///    ことでページキャッシュやディスク待ち行列に影響している可能性は残る。
    ///    これを切って比べれば分かる（docs/analysis/2026-09-19-decode-stall-overshoot.md）。
    /// 2. <b>現場の逃げ道として。</b>300GB 級の ProRes では初回のスキャンに 8 分程度かかる見込み。
    ///    素材が推奨どおりだと分かっているなら、止めて構わない。
    ///
    /// 止めると<b>キーフレーム間隔の警告も出なくなる</b>（判定の材料が無くなるため）。
    /// </summary>
    private readonly bool _gopScanEnabled =
        !string.Equals(Environment.GetEnvironmentVariable("TCS_GOP_SCAN"), "off",
            StringComparison.OrdinalIgnoreCase);

    // ── 同期コーディネータ（遅延生成キャッシュ。ラムダは this のフィールドのみを参照するため
    //    呼び出しごとの再生成は不要。初回呼び出し時に確定する） ──
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
        ProjectLoadApplicator projectLoadApplicator,
        PlaybackPerformanceStats playbackPerformanceStats,
        OutputBackendState outputBackendState,
        IServiceProvider services)
    {
        _ltcMonitor = ltcMonitor;
        _playlist = playlist;
        _syncService = syncService;
        _gapPlaybackCommandExecutor = gapPlaybackCommandExecutor;
        _gapFreezeHandler = gapFreezeHandler;
        _settingsManager = settingsManager;
        _spoutOutput = spoutOutput;
        _mediaDurationReader = mediaDurationReader;
        _playlistDurationBackfillService = playlistDurationBackfillService;
        _playlistLoadCoordinator = playlistLoadCoordinator;
        _projectLoadApplicator = projectLoadApplicator;
        _playbackPerformanceStats = playbackPerformanceStats;
        // GStreamer 内部型は公開せず、DI 経由で取得する（Gpu 出力時のみ使用）。
        _gstBackendState = services.GetRequiredService<GstBackendState>();
        _gstNativeApi = services.GetRequiredService<IGstNativeApi>();
        _playbackApi = services.GetRequiredService<IPlaybackApi>();
        _gstPlaybackApi = services.GetRequiredService<GstPlaybackApi>();
        if (!outputBackendState.PlaybackAvailable)
            _playbackAvailability.MarkUnavailable(outputBackendState.Decision.Detail);

        _vm = new MainViewModel();
        _vm.Player   = new PlayerViewModel(this);
        _vm.Playlist = new PlaylistViewModel(_playlist, _mediaDurationReader);
        _vm.Sync     = new SyncViewModel(_ltcMonitor);
        _vm.Output   = new OutputControlViewModel();
        _vm.Output.InitializeTestCard(OutputEngineSettings.TestCardRequested());
        _renderSession = new RenderSession(services.GetRequiredService<IRenderUpdateSource>(),
            _playbackPerformanceStats,
            action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action));
        _renderSession.FrameUpdate = ProcessRenderFrameUpdateAsync;
        if (outputBackendState.IsInitialized && outputBackendState.PlaybackAvailable)
        {
            // プレビューは OutputEngine の読み戻しで更新する。
            OutputTrace outputTrace = OutputTrace.Create(Environment.GetEnvironmentVariable(OutputTrace.EnvironmentVariable));
            OutputTrace.Current = outputTrace;
            _outputEngine = new OutputEngine(new OutputEngineSettings
            {
                CanvasWidth = CanvasSettings.Default.Width,
                CanvasHeight = CanvasSettings.Default.Height,
                SenderName = ResolveOutputSenderName(),
                AdapterLuid = OutputDisplays.FindAdapterLuid(settingsManager.Current.FullscreenDisplayDeviceName),
                TestCardEnabled = _vm.Output.TestCardEnabled,
                Trace = outputTrace,
                PreviewFrameReady = OnOutputPreviewFrame,
                SimulatedDeviceLossSeconds = OutputEngineSettings.ParseSimulatedDeviceLossSeconds(
                    Environment.GetEnvironmentVariable(OutputEngineSettings.SimulateDeviceLossEnvironmentVariable)),
                GpuStatusChanged = OnGpuStatusChanged,
                GStreamerRebindRequested = OnGStreamerRebindRequested,
                SourceFrameReady = OnSourceFrameReady,
            });
            Log.Information("OutputEngine: Gpu backend を開始（OutputBackend={Backend}）", outputBackendState.Decision.Requested);
            _outputEngine.Start();
            // shim は合成デバイスのアダプター LUID だけを使い、自前デバイス +
            // 共有テクスチャリング（NT ハンドル + 共有フェンス）でリースを直接ソースにする。
            // 合成デバイスの context は shim から触らない。
            if (_outputEngine.WaitForDevice(TimeSpan.FromSeconds(5)))
                _gstBackendState.SetExternalDevice(_outputEngine.DevicePointer);
            else
                Log.Error("OutputEngine: デバイス初期化がタイムアウトし、GStreamer shim へ Adopt できません");
        }
        // D4: 表示経路に到達したフレーム数は GPU 合成の公開数だけを見る。
        _syncGateRenderedFrames = new RenderedFrameCounter(
            gpuPublishedFrames: () => _outputEngine?.PublishedFrameCount ?? 0);
        _ltcSyncController = new LtcSyncController(
            _playlist, _gapFreezeHandler, _syncService, ltcFrameProcessor,
            settingsManager.Current.LtcSignalLossTimeoutMs, settingsManager.Current.LtcSignalResumeFrames,
            new LtcSyncEffects(
                GetContext: () => new LtcSyncContext(
                    IsPlayerReady, _vm.Sync.SyncEnabled, _vm.Sync.SyncMode,
                    _seekBarInteraction.IsSeeking, _vm.Sync.IsLtcRunning, _playbackControl.IsPaused,
                    _vm.Sync.LtcSignalLossMode, _vm.Sync.LtcFpsMode, _vm.Sync.GapBehavior,
                    _loadedTrackId, _fps, _duration,
                    _settingsManager.Current.LtcSignalLossTimeoutMs, _settingsManager.Current.LtcSignalResumeFrames,
                    // D33: Single の補正を D29 の範囲に収めるための [MediaIn, MediaOut]。
                    _playlist.Current?.MediaIn.TotalSeconds ?? 0.0,
                    _playlist.Current?.MediaOut?.TotalSeconds),
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
                    if (!IsPlaybackAvailable) return;
                    _playbackApi.SetPaused(paused);
                    ApplyPauseState(paused);
                },
                ResumeProjectRestorePause: ResumeProjectRestorePauseForSyncIfNeeded,
                ClearGapFreezeFrame: () => _renderSession.Invalidate(),
                RefreshCurrentVideoFrame: RefreshCurrentVideoFrame,
                UpdateTimelinePosition: seconds => _timelinePanel?.UpdatePlaybackPosition(seconds),
                UpdateCurrentTrackLabel: UpdateCurrentTrackLabel,
                ResumeGapPause: () =>
                {
                    if (!IsPlaybackAvailable) return;
                    _playbackApi.SetPaused(false);
                    ApplyPauseState(false);
                },
                GetCorrectionMode: () => _vm.Sync.SyncCorrectionMode,
                GetPlaybackSeconds: () => ReadPlaybackTimePos(),
                GetTotalRenderedFrames: () => _syncGateRenderedFrames.Read(),
                ApplyRateInstant: rate => _playbackApi.SetRateInstant(rate).Success,
                SeekTo: target => SeekTo(target),
                SetCorrectionStatus: text => _vm.Sync.SyncCorrectionStatus = text,
                GetSyncOffsetMilliseconds: () => _vm.Sync.SyncOffsetMs,
                IsPlaybackPositionUnstable: () => _gstPlaybackApi.IsPositionUnstable,
                // v0.5.3 段 3i: 信号断の一時停止を解いたときの再開判定（§6 の 9）。
                GetOtherPauseOwners: () => SyncRules.CollectPauseOwnersExceptSignalLoss(
                    _singleModeSyncCoordinator?.IsBoundaryHeld ?? false,
                    _gapFreezeHandler.IsPauseOwnedByGap,
                    _projectRestorePauseState.IsPending)),
            CreateSingleModeSyncCoordinator, CreateContinueOnTrackCoordinator, CreateGapEnterCoordinator);
        // 0.4.5-A フェーズ 1: shadow の「出したとしたら」レートに、実際の補正モードと着地窓を渡す。
        _syncService.CorrectionModeSource = () => _vm.Sync.SyncCorrectionMode;
        _syncService.CorrectionLandingActiveSource = _ltcSyncController.IsCorrectionLandingWindowActive;
        var audioState = new AudioControlState(
            settingsManager.Current.IsMuted,
            settingsManager.Current.Volume);
        _audioControlCoordinator = new AudioControlCoordinator(
            audioState,
            new AudioControlEffects(
                SetVolume: volume => _playbackApi.SetVolume(volume),
                SetMute: mute => _playbackApi.SetMute(mute),
                ApplyUi: ApplyAudioControlUi,
                Persist: snapshot => _ = _settingsManager.UpdateAsync(settings => settings with
                {
                    IsMuted = snapshot.IsMuted,
                    Volume = snapshot.Volume,
                })));
        ApplyAudioControlUi(audioState.Snapshot);
        _vm.Sync.SyncModeIndex = ProjectSyncSelectionMapper.GetSyncModeIndex(settingsManager.Current.SyncMode);
        _vm.Sync.GapBehaviorIndex = ProjectSyncSelectionMapper.GetGapBehaviorIndex(settingsManager.Current.GapBehavior);
        _vm.Sync.SyncCorrectionModeIndex =
            settingsManager.Current.SyncCorrectionMode == SyncCorrectionMode.Jump ? 1 : 0;
        _vm.Sync.SyncOffsetMs = settingsManager.Current.SyncOffsetMs;
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
                SaveAsync: path => SaveProjectAsync(path, _vm.Sync.SyncMode, _vm.Sync.GapBehavior),
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
                {
                    // U1 計測: 設定保存の同期開始部とギャップ再評価（Reapply）を分けて記録する。
                    long settingsStarted = Stopwatch.GetTimestamp();
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        GapBehavior = _vm.Sync.GapBehavior,
                    });
                    long settingsReturned = Stopwatch.GetTimestamp();
                    Log.Information("Gap behavior changed to {Behavior}", _vm.Sync.GapBehavior);
                    _ltcSyncController.GapBehaviorChanged();
                    long reapplyReturned = Stopwatch.GetTimestamp();
                    Log.Debug("Gap behavior change: settingsStartMs={SettingsMs:F1} ltcReapplyMs={ReapplyMs:F1}",
                        Stopwatch.GetElapsedTime(settingsStarted, settingsReturned).TotalMilliseconds,
                        Stopwatch.GetElapsedTime(settingsReturned, reapplyReturned).TotalMilliseconds);
                    break;
                }
                case nameof(SyncViewModel.LtcFpsMode):
                    _ltcSyncController.FpsModeChanged();
                    Log.Information("LTC fps mode changed mode={Mode}", _vm.Sync.LtcFpsMode);
                    break;
                case nameof(SyncViewModel.SyncCorrectionMode):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        SyncCorrectionMode = _vm.Sync.SyncCorrectionMode,
                    });
                    _ltcSyncController.CorrectionModeChanged();
                    Log.Information("Sync correction mode changed mode={Mode}", _vm.Sync.SyncCorrectionMode);
                    break;
                case nameof(SyncViewModel.SyncOffsetMs):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        SyncOffsetMs = _vm.Sync.SyncOffsetMs,
                    });
                    Log.Information("Sync offset changed offsetMs={OffsetMs}", _vm.Sync.SyncOffsetMs);
                    break;
                case nameof(SyncViewModel.LtcSignalLossMode):
                    _ = _settingsManager.UpdateAsync(settings => settings with
                    {
                        LtcSignalLossMode = _vm.Sync.LtcSignalLossMode,
                    });
                    _ltcSyncController.SignalLossModeChanged();
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
        LoadCanvasInputsFromState();
        UpdateCanvasUiState();
    }

    private async Task SaveProjectAsync(string path, SyncMode syncMode, GapBehavior gapBehavior)
    {
        await ProjectSerializer.SaveAsync(path, _playlist, syncMode, gapBehavior, _projectCanvasState.ToData());
        _projectCanvasState.MarkSaved();
    }

    internal MainViewModel ViewModel => _vm;

    // ── R1 1-2: 再生可否の判定はここだけ ────────────────────────────
    // GPU 検出失敗・GPU ワーカー初期化失敗・player 生成失敗は EnterPlaybackUnavailable に集約する。
    // 再生・シーク・LTC 同期の開始は IsPlaybackAvailable / IsPlayerReady だけを見て止める。
    private bool IsPlaybackAvailable => _playbackAvailability.IsAvailable;
    private bool IsPlayerReady => IsPlaybackAvailable && _gstBackendState.Player != IntPtr.Zero;

    private void EnterPlaybackUnavailable(string detail, bool gpuHardwareRequired)
    {
        _playbackAvailability.MarkUnavailable(detail);
        PlaybackUnavailablePanel.Visibility = Visibility.Visible;
        PlaybackUnavailableDetailText.Text = _playbackAvailability.Detail ?? "";
        if (_playbackUnavailableDialogShown)
            return;
        _playbackUnavailableDialogShown = true;
        string condition = gpuHardwareRequired
            ? "\n\n必要な条件: Direct3D 11.4 に対応した GPU とドライバ"
            : "";
        MessageBox.Show(
            "映像出力を開始できません。再生はできません。\n\n原因: " +
            (_playbackAvailability.Detail ?? "原因を特定できませんでした。") + condition +
            "\n\nログ: " + ResolveLogFilePath(),
            "映像出力を利用できません", MessageBoxButton.OK, MessageBoxImage.Warning);
        Log.Error("Playback unavailable: {Detail}", _playbackAvailability.Detail);
    }

    private static string ResolveLogFilePath() =>
        Path.Combine(AppContext.BaseDirectory, "logs", $"timecodesyncplayer-{DateTime.Now:yyyyMMdd}.log");

    private readonly AppSettingsManager _settingsManager;

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
        PlaylistList.ItemsSource = _playlist.Tracks;
        _ltcMonitor.FrameReceived += LtcMonitor_FrameReceived;
        _ltcMonitor.Stopped += LtcMonitor_Stopped;
        RefreshLtcDevices();
        AutoOffsetCheckBox.IsChecked = _settingsManager.Current.AutoOffsetOnAdd;
    }

    /// <summary>現在の再生位置（秒）。取得できないときは null。</summary>
    private double? ReadPlaybackTimePos()
    {
        return _playbackApi.TryGetTimePos(out double pos) && double.IsFinite(pos)
            ? pos
            : null;
    }

    // 0.4.5-A フェーズ 1: 同期評価の位置は 1 回の _ex 照会から値とサンプルの両方を作る。
    // v0.5.1: 秒とサンプルを 1 つの結果で返す（呼び出し順の暗黙の約束を無くした）。
    private SyncPositionRead ReadSyncPosition()
    {
        if (_playbackApi.TryGetPositionSample(out PlaybackPositionSample sample))
            return new SyncPositionRead(true, sample.Seconds, sample);
        return _playbackApi.TryGetTimePos(out double playbackSeconds)
            ? new SyncPositionRead(true, playbackSeconds, null)
            : SyncPositionRead.Failed;
    }

    // Gpu backend: ギャップ・カード・世代・位置を GPU worker の mailbox へ渡す（最新1件）。
    private void SubmitOutputState()
    {
        if (_outputEngine == null || _disposed) return;
        OutputGapMode gap = GapRenderFramePolicy.Decide(_gapFreezeHandler.CurrentState, _vm.Sync.GapBehavior) switch
        {
            GapRenderFrameDecision.Black => OutputGapMode.Black,
            GapRenderFrameDecision.GapFreeze => OutputGapMode.GapFreeze,
            GapRenderFrameDecision.Hold => OutputGapMode.Hold,
            _ => OutputGapMode.None,
        };
        _outputEngine.SubmitTimelineState(new TimelineOutputState(
            _renderSession.CaptureGeneration(),
            gap,
            _vm.Output.TestCardEnabled,
            _projectCanvasState.Current,
            TimelineOutputState.PlacementFor(_playlist.Current),
            ReadPlaybackTimePos() ?? 0));
    }

    private static string ResolveOutputSenderName()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(SpoutSender.SenderNameEnvironmentVariable);
            return string.IsNullOrWhiteSpace(fromEnv) ? SpoutDefaults.DefaultSenderName : fromEnv;
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
                    NotePreviewShown(frame);
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

    private void NotePreviewShown(PreviewFrame frame)
    {
        long now = Stopwatch.GetTimestamp();
        long? stalledTicks = _previewStallWatch.Shown(now);
        long previousSequence = _lastShownPreviewSequence;
        _lastShownPreviewSequence = frame.Sequence;
        _lastShownPreviewLuma = frame.MeanLuma;
        if (OutputTrace.Current.IsEnabled)
            OutputTrace.Current.Record(new("preview.shown", "UI", now, 0, frame.Sequence, 0, Value: frame.MeanLuma));
        if (stalledTicks is long ticks)
        {
            Log.Warning(
                "Preview resumed: stalledMs={StalledMs:F0} sequence={Sequence} previousSequence={Previous} luma={Luma}",
                ticks * 1000.0 / Stopwatch.Frequency, frame.Sequence, previousSequence, frame.MeanLuma);
        }
    }

    // v0.5.1: プレビューが 1 秒以上画面へ書き込まれていないとき、GPU 側の読み戻しが進んでいるかと一緒に 1 回だけ出す。
    private void CheckPreviewStall()
    {
        if (_outputEngine == null) return;
        long now = Stopwatch.GetTimestamp();
        if (_previewStallWatch.Check(now) is not long ageTicks) return;
        PreviewDiagnostics d = _outputEngine.GetPreviewDiagnostics();
        Log.Warning(
            "Preview stalled: sinceShownMs={SinceShownMs:F0} shownSequence={ShownSequence} shownLuma={ShownLuma} " +
            "readbacks={Readbacks} sinceReadbackMs={SinceReadbackMs:F0} readbackLuma={ReadbackLuma} busySkips={BusySkips} noImageSkips={NoImageSkips} gap={Gap}",
            ageTicks * 1000.0 / Stopwatch.Frequency, _lastShownPreviewSequence, _lastShownPreviewLuma,
            d.Readbacks, d.LastReadbackQpc == 0 ? -1.0 : (now - d.LastReadbackQpc) * 1000.0 / Stopwatch.Frequency,
            d.LastMeanLuma, d.BusySkips, d.NoImageSkips, _gapFreezeHandler.CurrentState);
    }

    // GPU worker から呼ばれる。UI は Dispatcher に投げるだけで待たない。
    private void OnGpuStatusChanged(GpuOutputStatus status)
    {
        try
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                if (_disposed) return;
                switch (status)
                {
                    case GpuOutputStatus.Recovering:
                        UpdateGpuStatus("GPU デバイス消失、復旧中", retryVisible: false, resetAfter: null);
                        // 復旧中は最後の合成画像が失われるため黒を表示する。
                        VideoImage.Source = null;
                        break;
                    case GpuOutputStatus.Recovered:
                        UpdateGpuStatus("復旧", retryVisible: false, resetAfter: TimeSpan.FromSeconds(5));
                        break;
                    case GpuOutputStatus.Failed:
                        UpdateGpuStatus("GPU 出力停止。再試行", retryVisible: true, resetAfter: null);
                        break;
                }
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "GPU 状態通知の受信に失敗");
        }
    }

    private void UpdateGpuStatus(string text, bool retryVisible, TimeSpan? resetAfter)
    {
        GpuStatusText.Text = text;
        BtnGpuRetry.Visibility = retryVisible ? Visibility.Visible : Visibility.Collapsed;
        _gpuStatusResetTimer?.Stop();
        if (resetAfter is not { } delay) return;
        _gpuStatusResetTimer ??= CreateGpuStatusResetTimer();
        _gpuStatusResetTimer.Interval = delay;
        _gpuStatusResetTimer.Start();
    }

    private DispatcherTimer CreateGpuStatusResetTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            GpuStatusText.Text = "";
        };
        return timer;
    }

    private void BtnGpuRetry_Click(object sender, RoutedEventArgs e)
    {
        BtnGpuRetry.Visibility = Visibility.Collapsed;
        GpuStatusText.Text = "GPU デバイス消失、復旧中";
        _outputEngine?.RetryGpuRecovery();
    }

    // GPU worker（復旧）から呼ばれる。共有リングを開き直せない場合のみ player を再生成し、
    // 現在ファイルの再ロード＋直前位置シーク＋再生状態復帰を行ってからソースを再接続する。
    private void OnGStreamerRebindRequested(IntPtr devicePointer)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            try
            {
                if (_disposed || _outputEngine == null) return;
                double position = ReadPlaybackTimePos() ?? 0;
                // 0.4.6: 描画側は古いプレイヤーのハンドルを持っているので、破棄の前に外し、
                // 作り直した後で新しいハンドルへつなぎ直す（外さないと破棄済みのプレイヤーへ
                // 通知の読み出しを続けていた。Codex のレビュー）。
                _renderSession.DetachPlayer();
                if (!_gstBackendState.RecreatePlayer(devicePointer))
                {
                    Log.Error("GPU 復旧: GStreamer player の再生成に失敗");
                    return;
                }
                _renderSession.AttachPlayer(_gstBackendState.Player);
                Log.Information("GPU 復旧: フレーム通知を新しいプレイヤーへつなぎ直した");
                _outputEngine.AttachGStreamerSource(_gstBackendState.Player, _gstNativeApi,
                    _gstBackendState.Seeking.NotifyEnded);
                ReloadCurrentTrackAfterGpuRecovery(position);
                Log.Information("GPU 復旧: GStreamer player を再生成し位置 {Position:F3}s へ復帰", position);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GPU 復旧: GStreamer の再接続に失敗");
            }
        });
    }

    /// <summary>
    /// v0.5.3 段 3b（§6 の 2 の経路 #1）: GPU 復旧後に現在トラックを直前位置で読み直し、
    /// 位置つきの読み込みを同期の入口にも通す（source = "gpu-recovery"）。
    /// </summary>
    private bool ReloadCurrentTrackAfterGpuRecovery(double position)
    {
        PlaylistTrack? track = _playlist.Current;
        if (track == null || !LoadFile(track.FilePath, position))
            return false;

        _syncService.BeginFileLoad(position, _syncGateRenderedFrames.Read(),
            loadIssuedQpc: 0, source: "gpu-recovery");
        return true;
    }

    private bool InitializeWindowLoadedSession()
    {
        if (!IsPlaybackAvailable)
        {
            // ダイアログは起動処理の完了後に出す（Show() 中のモーダルで UIA の起動待ちを阻害しない）。
            string detail = _playbackAvailability.Detail ?? "";
            PlaybackUnavailablePanel.Visibility = Visibility.Visible;
            PlaybackUnavailableDetailText.Text = detail;
            Dispatcher.BeginInvoke(
                new Action(() => EnterPlaybackUnavailable(detail, gpuHardwareRequired: true)),
                DispatcherPriority.Background);
            return false;
        }

        var spoutUiApplicator = new SpoutStartupUiApplicator(
            setButtonEnabled: enabled => BtnSpout.IsEnabled = enabled,
            setToggleLabel: label => _vm.Sync.SpoutToggleLabel = label);
        var sessionInitializer = new WindowLoadedSessionInitializer(
            initializePlayback: () => _gstPlaybackApi.Initialize(),
            applyAudioSettings: _audioControlCoordinator.ApplyStartup,
            createRenderContext: () => _renderSession.Create(_gstBackendState.Player),
            // GPU 構成では OutputEngine の SendTexture 経路が送信者を持つため、CPU 側 spoutDX は初期化しない。
            initializeSpout: () => SpoutStartupState.FromInitializationResult(true),
            applySpoutStartupState: spoutUiApplicator.Apply,
            startTimer: () => _timer = StartupTimerFactory.CreateStartedTimer(TimeSpan.FromMilliseconds(TimerIntervalMs), OnTick),
            initializeTimeline: InitializeTimeline,
            showError: ShowWindowLoadedSessionInitializationError);
        bool initialized = sessionInitializer.Initialize();
        if (initialized)
        {
            // プレイヤー生成後にエンジンへソースを接続する。
            _outputEngine?.AttachGStreamerSource(_gstBackendState.Player, _gstNativeApi,
                _gstBackendState.Seeking.NotifyEnded);
            RefreshDisplaySelection(_settingsManager.Current.FullscreenDisplayDeviceName);
        }
        return initialized;
    }

    private void ScheduleProjectLaunchAction(ProjectLaunchActionPlan launchActionPlan)
    {
        var launchActionExecutor = new ProjectLaunchActionExecutor(
            LoadProjectFromLaunchAsync,
            paths => ReplacePlaylistAndLoadAsync(paths),
            path => SaveProjectAsync(path, _vm.Sync.SyncMode, _vm.Sync.GapBehavior));
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

    private void ShowWindowLoadedSessionInitializationError(WindowLoadedSessionInitializationError error)
    {
        // 見せ方は GPU 利用不可と同じ 1 か所に集約する。生成・初期化の失敗は確認先を示す。
        string detail = error switch
        {
            WindowLoadedSessionInitializationError.PlaybackInitializeFailed =>
                "再生エンジンの初期化に失敗しました。GStreamer ランタイムと tcs_gstreamer.dll を確認してください。",
            WindowLoadedSessionInitializationError.RenderContextCreateFailed =>
                "レンダーコンテキストの作成に失敗しました。",
            _ => "初期化に失敗しました。"
        };

        EnterPlaybackUnavailable(detail, gpuHardwareRequired: false);
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
        ApplyProjectCanvas(project.Canvas);

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

        // 0.4.6: 反映できなかったトラックを黙って捨てずに知らせる。モーダルにするのは、
        // そのまま上書き保存するとトラックがプロジェクトから外れるため（気づかずに保存させない）。
        if (SkippedProjectTracksMessage.Format(result.SkippedTracks) is { } message)
        {
            Log.Warning("プロジェクトの読み込み: {Count} 件のトラックを反映できませんでした", result.SkippedTracks.Count);
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, message,
                SkippedProjectTracksMessage.Caption, MessageBoxButton.OK, MessageBoxImage.Warning));
        }
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
        if (!IsPlaybackAvailable) return;

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
        if (!IsPlaybackAvailable) return;

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
        // M1 計測: Dispatcher 待ちと UI 処理をフレームへ紐付ける（トレース無効時は時刻を取らない）。
        long enqueuedTicks = SyncAccuracyTrace.Current.IsEnabled ? Stopwatch.GetTimestamp() : 0;
        Dispatcher.BeginInvoke(() =>
        {
            if (_disposed) return;
            long uiStartTicks = enqueuedTicks != 0 ? Stopwatch.GetTimestamp() : 0;
            _ltcSyncController.ReceiveFrame(e, receivedAtMilliseconds);
            if (enqueuedTicks != 0)
                SyncAccuracyTrace.Current.RecordDispatch(
                    e.FrameEndTimestamp, enqueuedTicks, uiStartTicks, Stopwatch.GetTimestamp());
        });
    }

    private SingleModeSyncCoordinator CreateSingleModeSyncCoordinator() =>
        _singleModeSyncCoordinator ??= new SingleModeSyncCoordinator(
            _syncService,
            new SingleModeSyncEffects(
                ReadPosition: ReadSyncPosition,
                BuildPlaybackState: playbackSeconds => new SyncPlaybackState(
                    SyncEnabled: _vm.Sync.SyncEnabled,
                    HasCurrentTrack: _playlist.Current != null,
                    IsSeeking: _seekBarInteraction.IsSeeking,
                    PlaybackSeconds: playbackSeconds,
                    DurationSeconds: _duration,
                    VideoFps: _fps,
                    TimecodeFps: _ltcSyncController.LastTimecodeFps,
                    // D29: Single の LTC → 素材位置はトラックの MediaIn/MediaOut に収める。
                    MediaInSeconds: _playlist.Current?.MediaIn.TotalSeconds ?? 0.0,
                    MediaOutSeconds: _playlist.Current?.MediaOut?.TotalSeconds),
                SeekTo: target => SeekTo(target),
                GetTotalRenderedFrames: () => _syncGateRenderedFrames.Read(),
                IsNativeSeeking: IsNativeSeeking,
                // D33: 範囲外 LTC の終端ホールド。一時停止／解除を UI 状態と一緒に反映する。
                SetEndHold: ApplyBoundaryHold,
                // D35-b: ホールド解除時に保留シークと保持着地のラッチを解除する。
                OnBoundaryHoldReleased: () => _ltcSyncController.NotifyClipBoundaryHoldReleased()));

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
                },
                SeekTo: target => SeekTo(target),
                ResumePlayback: () => _playbackApi.SetPaused(false),
                ApplyPauseState: paused => ApplyPauseState(paused),
                UpdateCurrentTrackLabel: () => UpdateCurrentTrackLabel(),
                GetLoadedTrackId: () => _loadedTrackId,
                SetLoadedTrackId: id => SetLoadedTrack(id),
                LoadFile: (path, start) => LoadFile(path, startPosition: start),
                GetTotalRenderedFrames: () => _syncGateRenderedFrames.Read(),
                ReadPosition: ReadSyncPosition,
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
            PauseForGap: () => _gapPlaybackCommandExecutor.PauseForGap(),
            ApplyPauseState: paused => ApplyPauseState(paused),
            ClearGapFreezeFrame: () => RunTimedGapAction("clearGapFreezeFrame", () => _renderSession.Invalidate()),
            SeekTo: target => SeekTo(target),
            GetPlayerDuration: () =>
            {
                return _playbackApi.TryGetDuration(out double duration)
                    ? (0, duration)
                    : (-1, 0.0);
            },
            IsPlayerReady: () => IsPlayerReady,
            LoadPausedAt: (path, target) => _gapPlaybackCommandExecutor.LoadPausedAt(path, target),
            ResetPlayerStateForNewTrack: () => ResetPlayerStateForNewTrack(),
            GetLoadedTrackId: () => _loadedTrackId,
            SetLoadedTrackId: id => SetLoadedTrack(id),
            GetDuration: () => _duration,
            SetDuration: d => _duration = d,
            GetFps: () => _fps,
            SetFps: f => _fps = f,
            GetGapBehavior: () => _vm.Sync.GapBehavior,
            UpdateCurrentTrackLabel: () => UpdateCurrentTrackLabel(),
            // v0.5.3 段 3g: ギャップの読み込みの後に通す同期側の口（ロード中の印を立てない）。
            BeginGapFreezeLoad: source => _syncService.BeginGapFreezeLoad(source)),
        GapPlayerModePolicy.Current);

    private void RefreshCurrentVideoFrame()
    {
        // Re-seek the current position to redraw immediately after leaving a black/frozen gap.
        if (IsPlayerReady && _playbackApi.TryGetTimePos(out double currentPos))
            SeekTo(currentPos);
    }

    // U1 計測: ギャップ切替ハンドラ内で同期実行されるエフェクトの呼び出し所要
    // （フリーズ画像の世代クリアは同期実行）。
    private static void RunTimedGapAction(string action, Action work)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            work();
        }
        finally
        {
            Log.Debug("Gap action {Action}: callMs={CallMs:F1}",
                action, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
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
        // 着地遅延 L はトラック単位で保持する。切替では消さず、対象トラックの学習値へ引き当てる。
        if (id != _loadedTrackId)
            _syncService.LatencyCompensator.SelectTrack(id);
        _loadedTrackId = id;
        if (_timelinePanel != null)
            _timelinePanel.LoadedTrackId = id;
        BeginGopScan(id);
    }

    /// <summary>
    /// 0.4.5-C3: 読み込んだ素材のキーフレーム分布を測る。I/O を待つのでバックグラウンドで走らせ、
    /// 終わったら UI スレッドで表示を更新する。同じ素材は 1 回しか測らない。
    /// </summary>
    private void BeginGopScan(Guid? trackId)
    {
        if (!_gopScanEnabled) return;
        if (!trackId.HasValue) return;
        PlaylistTrack? track = _playlist.FindTrackById(trackId.Value);
        string? path = track?.FilePath;
        if (string.IsNullOrEmpty(path)) return;
        if (_gopScanCache.TryGet(path) is not null)
        {
            UpdateGopScanWarning();
            return;
        }
        Guid id = trackId.Value;
        Task.Run(() =>
        {
            try
            {
                _gopScanCache.EnsureScanned(path);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "GOP scan failed for {Path}", path);
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                if (_loadedTrackId == id)
                    UpdateGopScanWarning();
            });
        });
    }

    /// <summary>0.4.5-C3: スキャン結果からステータス行とプレイリストの印を更新する。</summary>
    private void UpdateGopScanWarning()
    {
        PlaylistTrack? track = _loadedTrackId.HasValue
            ? _playlist.FindTrackById(_loadedTrackId.Value) : null;
        GopScanResult? scan = _gopScanCache.TryGet(track?.FilePath);
        GopSeekQuality quality = scan?.Quality ?? GopSeekQuality.Unknown;
        string text = scan is { } s ? GopScanVerdict.Format(quality, s.MaxGapSeconds) : string.Empty;

        if (_vm.Sync.LongGopWarning != text)
            _vm.Sync.LongGopWarning = text;

        // D37-f: シーク所要の見積もりを同期へ渡す。ロード直後の 1 回目のシークには学習値が
        // 無く（ロードで学習を捨てるため）、D37-e の先行補償が 0 になっていた。所要は
        // キーフレームからの距離にほぼ比例する（実測: 距離 0 で 244ms、6.6 秒先で 2,164ms）。
        // キーフレーム 2 枚未満はスキャンが信用できない（パーサ次第で 1 枚しか立たない素材がある）。
        // この値はシークの行き先に足すので、誤ると数十秒先へ飛ぶ。渡さない方が安全。
        if (scan is { Keyframes: >= 2 } hintScan)
        {
            // 上限を付ける。スキャンが壊れても行き先が壊れないようにするための歯止め。
            double hint = Math.Min(
                hintScan.MaxGapSeconds * SeekCostPerGapSecond, MaxSeekCostHintSeconds);
            // D37-h: 既定で渡さない。理由は下の定数のコメント。
            _syncService.SetSeekCostHintSeconds(_seekCostHintEnabled ? hint : 0.0);
            Log.Information(
                "Seek cost hint: track={Track} maxGapMs={Max:F0} hintMs={Hint:F0} keyframes={Keyframes} enabled={Enabled}",
                track?.Name, hintScan.MaxGapSeconds * 1000.0, hint * 1000.0, hintScan.Keyframes,
                _seekCostHintEnabled);
        }

        if (track != null && quality is GopSeekQuality.Warning or GopSeekQuality.Error
            && !track.LongGopWarning)
        {
            Log.Information(
                "Long GOP warning: track={Track} keyframes={Keyframes} maxGapMs={Max:F0} medianGapMs={Median:F0} headGapMs={Head:F0} tailGapMs={Tail:F0}",
                track.Name, scan!.Value.Keyframes, scan.Value.MaxGapSeconds * 1000.0,
                scan.Value.MedianGapSeconds * 1000.0, scan.Value.HeadGapSeconds * 1000.0,
                scan.Value.TailGapSeconds * 1000.0);
            _playlist.MarkLongGopWarning(track.Id);
        }
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

        string input = textBox.Text;
        var result = PlaylistTimelineOffsetEditor.Apply(
            _playlist,
            trackId,
            input,
            _settingsManager.Current.AutoOffsetOnAdd,
            GapFreezeHandler.DefaultFallbackFps);
        if (result.Status == PlaylistTimelineOffsetEditStatus.TrackNotFound)
            return;

        Log.Debug("TimelineOffset edit: track={Track} input='{Input}' fps={Fps} currentOffset={CurrentOffset:F3} autoOffset={AutoOffset}",
            result.OriginalTrack!.Name,
            input,
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
            if (result.Adjusted)
            {
                // 丸め・繰り上げが起きたら確定値を入力欄へ書き戻す（黙って違う値を採用しない）。
                // 表示形式は FormatTimecode のコロンのまま。
                textBox.Text = PlaylistTrackFormatter.FormatTimecode(
                    result.UpdatedTrack!.TimelineOffset, result.Fps);
                Log.Information("TimelineOffset adjusted: track={Track} input='{Input}' confirmed={Confirmed}",
                    result.OriginalTrack.Name, input, textBox.Text);
            }
            Log.Information("TimelineOffset updated: track={Track} offset={Offset:F3} actualIn={ActualIn:F3} actualOut={ActualOut:F3} autoOffset={AutoOffset}",
                result.OriginalTrack.Name,
                result.UpdatedTrack!.TimelineOffset.TotalSeconds,
                result.UpdatedTrack.GetActualTimelineIn().TotalSeconds,
                result.UpdatedTrack.GetActualTimelineOut().TotalSeconds,
                _settingsManager.Current.AutoOffsetOnAdd);
        }
        else
        {
            Log.Warning("TimelineOffset parse failed: track={Track} input='{Input}' fps={Fps}", result.OriginalTrack!.Name, input, result.Fps);
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
        // T7: 再生停止・プロジェクト/プレイリスト差し替えで補正状態を捨てる。
        _ltcSyncController.PlaybackStopped();
        CreatePlaybackOperationsCoordinator().StopPlayback();
    }

    private bool LoadFile(string path, double? startPosition = null)
    {
        if (!IsPlaybackAvailable) return false;
        if (!CreatePlaybackOperationsCoordinator().LoadFile(path, startPosition)) return false;
        // F1 の切り分け: 位置つきロードは、表示がフレーム通知待ちのまま残ることがある。
        // 成功直後に表示位置を 1 回だけ明示更新する（UpdatePerFrameUI は自動送り等の副作用を持つため呼ばない）。
        if (startPosition.HasValue)
            RefreshPositionDisplayAfterLocatedLoad(startPosition.Value);
        return true;
    }

    /// <summary>
    /// F1: 位置つきロード直後の表示更新。ネイティブの位置がまだ読めない/0 のときは要求した開始位置を使う。
    /// ラベルとシークバーだけを更新し、自動送り・統計・世代には触れない。
    /// </summary>
    private void RefreshPositionDisplayAfterLocatedLoad(double requestedPosition)
    {
        double position = requestedPosition;
        if (_playbackApi.TryGetTimePos(out double native) && double.IsFinite(native) && native > 0)
            position = native;
        if (position < 0)
            position = 0;

        SetSeekBarValueFromPlayer(SeekBarUpdateState.ToSliderValue(position, _duration, SeekBar.Value));
        _vm.Player.TimeLabel =
            $"{PlaybackTimeFormatter.FormatFrames(position, _fps)} / {PlaybackTimeFormatter.FormatFrames(_duration, _fps)}";
    }

    private bool LoadFilePaused(string path)
        => IsPlaybackAvailable && CreatePlaybackOperationsCoordinator().LoadFilePaused(path);

    // ── Playback helpers ───────────────────────────────────────────

    private bool SeekTo(double seconds)
        => IsPlaybackAvailable && CreatePlaybackOperationsCoordinator().SeekTo(seconds);

    // ── IPlaybackController ────────────────────────────────────────────────
    void IPlaybackController.TogglePlayPause()
    {
        if (!IsPlayerReady) return;
        // T7: 操作者の再生・一時停止で補正状態を捨てる。
        _ltcSyncController.PlayPauseToggled();
        _projectRestorePauseState.Clear();
        PlaybackPauseChange change = _playbackControl.TogglePlayPause();
        _playbackApi.SetPaused(change.IsPaused);
        ResetPlaybackPerformanceStats();
        _vm.Player.PlayPauseIcon = change.PlayPauseIcon;
    }

    private void ResumeProjectRestorePauseForSyncIfNeeded()
    {
        if (!IsPlaybackAvailable)
            return;
        if (!_projectRestorePauseState.TryConsume())
            return;

        _playbackApi.SetPaused(false);
        ApplyPauseState(false);
        Log.Information("Project restore pause released by on-track sync");
    }

    private void ApplyBoundaryHold(bool held)
    {
        if (!IsPlaybackAvailable) return;
        if (held)
        {
            _playbackApi.SetPaused(true);
            ApplyPauseState(true);
            return;
        }

        PauseOwners otherOwners = SyncRules.CollectOtherPauseOwners(
            _ltcSyncController.IsSignalLossPauseOwned,
            _gapFreezeHandler.IsPauseOwnedByGap,
            _projectRestorePauseState.IsPending);
        if (!SyncRules.ShouldResumeOnBoundaryHoldRelease(otherOwners))
        {
            Log.Information(
                "Single mode: boundary hold released; playback stays paused owners={Owners}",
                otherOwners);
            return;
        }

        _playbackApi.SetPaused(false);
        ApplyPauseState(false);
    }

    void IPlaybackController.SeekRelative(double seconds)
    {
        if (!IsPlayerReady) return;
        _ltcSyncController.CancelPendingSync("relative");
        // 決定 5: 相対シークはクライアント計算（Seek(absolute) へ加算）。
        if (!_playbackApi.TryGetTimePos(out double current))
        {
            Log.Warning("SeekRelative: time-pos が取得できず相対シークを中断 seconds={Seconds}", seconds);
            return;
        }
        if (_playbackApi.Seek(current + seconds).Success)
            _playbackApi.SetPaused(_playbackControl.IsPaused);
    }

    void IPlaybackController.CycleSpeed()
    {
        if (!IsPlayerReady) return;
        PlaybackSpeedChange change = _playbackControl.CycleSpeed();
        PlaybackResult result = _playbackApi.SetRate(change.Speed);
        if (!result.Success)
            Log.Warning("CycleSpeed: rate 設定に失敗 speed={Speed} error={Error}", change.Speed, result.Error);
        _vm.Player.SpeedLabel = change.Label;
    }

    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        if (!IsPlayerReady) return;
        _audioControlCoordinator.ToggleMute();
    }

    private void VolumeSlider_ValueChanged(
        object sender,
        RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsPlayerReady) return;
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
            IsPlayerReady: () => IsPlayerReady,
            Load: (path, start, paused) => _playbackApi.Load(path, start, paused),
            Seek: seconds => _playbackApi.Seek(seconds),
            Stop: () => _playbackApi.Stop(),
            SetPaused: paused => _playbackApi.SetPaused(paused),
            ResetPlayerStateForNewTrack: () => ResetPlayerStateForNewTrack(),
            ClearLoadedTrackId: () => _loadedTrackId = null,
            HasTimelinePanel: () => _timelinePanel != null,
            ClearTimelineLoadedTrackId: () => _timelinePanel!.LoadedTrackId = null,
            SetSeekBarValueFromPlayer: value => SetSeekBarValueFromPlayer(value),
            SetTimeLabel: value => _vm.Player.TimeLabel = value,
            SetPlayPauseIcon: value => _vm.Player.PlayPauseIcon = value,
            ResetGapFreezeAll: () => _gapFreezeHandler.ResetAll(),
            ResetGapFreeze: () => _gapFreezeHandler.Reset(),
            ClearGapFreezeFrame: () => _renderSession.Invalidate(),
            // D20-b: 手動ロードを同期のロードゲートへ記録する（保持 LTC のロード解除再適用）。
            BeginSyncFileLoad: startPosition => _syncService.BeginFileLoad(startPosition, _syncGateRenderedFrames.Read()),
            // D33-b: ロードが速いと 100ms タイマーが取得前に次のロードへ置き換わり得るため、
            // ロード成功直後に 1 回だけ取得を予約する（サイズ未取得ならタイマーが再試行）。
            RequestMetadataFetch: ScheduleMetadataFetch));

    // ── Spout ─────────────────────────────────────────────────────

    private void BtnSpout_Click(object sender, RoutedEventArgs e)
    {
        _spoutOutput.IsEnabled = !_spoutOutput.IsEnabled;
        _outputEngine?.SetSpoutEnabled(_spoutOutput.IsEnabled);
        _vm.Sync.SpoutToggleLabel = ToggleLabelFormatter.Format(_spoutOutput.IsEnabled, SpoutOnLabel, SpoutOffLabel);
        Log.Information("Spout 出力: {State}", _spoutOutput.IsEnabled ? "ON" : "OFF");
    }

    // ── キャンバス設定・テストカード（段階 4） ────────────────────

    private void BtnTestCard_Click(object sender, RoutedEventArgs e)
    {
        _vm.Output.ToggleTestCard();
        _outputEngine?.SetTestCardEnabled(_vm.Output.TestCardEnabled);
        SubmitOutputState();
        Log.Information("テストカード: {State}", _vm.Output.TestCardEnabled ? "ON" : "OFF");
    }

    private void CanvasPresetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_isLoadingCanvasInputs) return;
        if (CanvasPresetCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;
        if (item.Tag is not string preset || preset == "custom") return;
        string[] parts = preset.Split('x');
        CanvasWidthBox.Text = parts[0];
        CanvasHeightBox.Text = parts[1];
    }

    private void BtnApplyCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (!CanvasChangeGate.CanChange(IsPlaying(), IsLtcFollowing(), IsRenderingFrozenOnly()))
        {
            UpdateCanvasUiState();
            return;
        }
        if (!TryReadCanvasInputs(out int width, out int height))
        {
            Log.Warning("キャンバス寸法が不正です: width='{Width}' height='{Height}'", CanvasWidthBox.Text, CanvasHeightBox.Text);
            MessageBox.Show("キャンバス寸法は 16〜16384 の整数で入力してください。", "キャンバス設定",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = new CanvasSettings(width, height, GetSelectedFitId());
        if (settings == _projectCanvasState.Current) return;
        _projectCanvasState.Select(settings);
        ApplyCanvasToEngine();
        UpdateCanvasUiState();
    }

    private void ApplyProjectCanvas(CanvasData? canvas)
    {
        _projectCanvasState.OnProjectLoaded(canvas);
        if (canvas == null)
        {
            var dialog = new CanvasSelectDialog(
                _projectCanvasState.Current.Width,
                _projectCanvasState.Current.Height,
                _projectCanvasState.Current.DefaultFitId)
            {
                Owner = this
            };
            bool? result = dialog.ShowDialog();
            if (result == true && dialog.Selection is { } selection)
                _projectCanvasState.Select(selection);
            Log.Information("キャンバス未設定プロジェクト: 選択={Selection}",
                dialog.Selection is { } chosen ? $"{chosen.Width}x{chosen.Height}" : "キャンセル(1920x1080)");
        }
        LoadCanvasInputsFromState();
        ApplyCanvasToEngine();
        UpdateCanvasUiState();
    }

    private void ApplyCanvasToEngine()
    {
        CanvasSettings current = _projectCanvasState.Current;
        _outputEngine?.SetCanvas(current);
        Log.Information("キャンバス設定: canvas={Width}x{Height} defaultFit={Fit} unset={Unset} dirty={Dirty}",
            current.Width, current.Height, current.DefaultFitId, _projectCanvasState.IsUnsetInProject, _projectCanvasState.IsDirty);
    }

    private void LoadCanvasInputsFromState()
    {
        CanvasSettings current = _projectCanvasState.Current;
        _isLoadingCanvasInputs = true;
        try
        {
            CanvasWidthBox.Text = current.Width.ToString(CultureInfo.InvariantCulture);
            CanvasHeightBox.Text = current.Height.ToString(CultureInfo.InvariantCulture);
            CanvasFitCombo.SelectedIndex = current.DefaultFitId == FitWidth.FitId ? 1 : 0;
            CanvasPresetCombo.SelectedIndex = (current.Width, current.Height) switch
            {
                (1920, 1080) => 0,
                (3840, 2160) => 1,
                (1080, 1920) => 2,
                _ => 3,
            };
        }
        finally
        {
            _isLoadingCanvasInputs = false;
        }
    }

    private bool TryReadCanvasInputs(out int width, out int height)
    {
        width = height = 0;
        if (!int.TryParse(CanvasWidthBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out width) ||
            !int.TryParse(CanvasHeightBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out height))
            return false;
        return width is >= 16 and <= 16384 && height is >= 16 and <= 16384;
    }

    private string GetSelectedFitId()
        => (CanvasFitCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? FitHeight.FitId;

    private bool IsPlaying() => IsPlayerReady && !_playbackControl.IsPaused;

    private bool IsLtcFollowing() => _vm.Sync.SyncEnabled;

    private bool IsRenderingFrozenOnly() => !_gapFreezeHandler.IsInactive || _projectRestorePauseState.IsPending;

    /// <summary>変更可否を UI に反映する。不可のときは入力と適用ボタンを無効化し理由をツールチップに出す。</summary>
    private void UpdateCanvasUiState()
    {
        if (_disposed) return;
        bool isPlaying = IsPlaying();
        bool isLtcFollowing = IsLtcFollowing();
        bool isFrozenOnly = IsRenderingFrozenOnly();
        bool canChange = CanvasChangeGate.CanChange(isPlaying, isLtcFollowing, isFrozenOnly);
        string? tip = CanvasChangeGate.DescribeReason(isPlaying, isLtcFollowing, isFrozenOnly);

        var state = (canChange, tip);
        if (_canvasUiCache == state) return;
        _canvasUiCache = state;

        CanvasPresetCombo.IsEnabled = canChange;
        CanvasWidthBox.IsEnabled = canChange;
        CanvasHeightBox.IsEnabled = canChange;
        CanvasFitCombo.IsEnabled = canChange;
        BtnApplyCanvas.IsEnabled = canChange;
        BtnTestCard.IsEnabled = true;
        System.Windows.Controls.ToolTipService.SetToolTip(CanvasGroup, tip);
        System.Windows.Controls.ToolTipService.SetToolTip(BtnTestCard, null);
    }

    // ── クリップ配置（右クリックメニュー） ────────────────────────

    private void PlaylistList_ContextMenuOpening(object sender, System.Windows.Controls.ContextMenuEventArgs e)
    {
        int index = ListBoxItemHitTester.GetItemIndexAt(PlaylistList, System.Windows.Input.Mouse.GetPosition(PlaylistList));
        if (index < 0)
        {
            e.Handled = true;
            return;
        }
        _playlist.Select(index);
        SyncPlaylistSelection();
        _contextMenuTrackId = _playlist.Tracks[index].Id;
    }

    private void TrackFitMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem item || _contextMenuTrackId is not { } trackId)
            return;
        int index = _playlist.FindIndexById(trackId);
        if (index < 0) return;
        string? fitId = item.Tag as string;
        PlaylistTrack track = _playlist.Tracks[index];
        _playlist.Tracks[index] = track with { Fit = fitId };
        SubmitOutputState();
        Log.Information("クリップ配置: track={Track} fit={Fit}",
            track.Name, fitId ?? "project-default");
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
        if (_outputEngine == null)
            return; // 再生不可（GPU 出力なし）では全画面を開かない。

        var window = new FullscreenOutputWindow(target, _displayCatalog, _outputEngine);
        window.Closed += FullscreenWindow_Closed;
        _fullscreenWindow = window;

        try
        {
            window.Show();
            DisplayCombo.IsEnabled = false;
            BtnFullscreen.Content = FullscreenCloseLabel;
            var previewBitmap = VideoImage.Source as BitmapSource;
            Log.Information("Fullscreen output opened on {Display} previewBitmap={PreviewWidth}x{PreviewHeight}",
                target.DeviceName, previewBitmap?.PixelWidth ?? 0, previewBitmap?.PixelHeight ?? 0);
        }
        catch
        {
            window.Closed -= FullscreenWindow_Closed;
            _fullscreenWindow = null;
            throw;
        }
    }

    private void FullscreenWindow_Closed(object? sender, EventArgs e)
    {
        _outputEngine?.DetachFullscreen();
        if (sender is FullscreenOutputWindow window)
            window.Closed -= FullscreenWindow_Closed;
        _fullscreenWindow = null;
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
        if (!IsPlaybackAvailable) return;

        _ltcSyncController.TimelineSeek();
        bool success = SeekTo(e.TargetSeconds);
        Log.Information("Timeline seek target={Target:F3} trackIndex={TrackIndex} success={Success}",
            e.TargetSeconds, e.TrackIndex, success);
    }

    // ── シークバー操作 ────────────────────────────────────────────

    private void Seek_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _ltcSyncController.CancelPendingSync("seekbar-down");
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
            return;
        }

        CommitSeekBarSeek(e.NewValue, "Automation");
    }

    // ── 定期 UI 更新（タイマー） ──────────────────────────────────
    // 周期性タスクのみ（duration取得・メタデータ取得）。
    // フレームごとのUI更新（シークバー・時刻表示・OSD等）は OnRenderUpdate で行う。

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // GPU ワーカーが資源初期化で停止した場合も、起動時検出と同じ見せ方にそろえる（R1 1-2）。
        if (_outputEngine is { Faulted: true } faultedEngine)
        {
            EnterPlaybackUnavailable(
                faultedEngine.FirstFault ?? "GPU 出力ワーカーが停止しました。",
                gpuHardwareRequired: true);
            _timer?.Stop();
            return;
        }
        if (!IsPlayerReady) return;

        SubmitOutputState();
        UpdateCanvasUiState();
        CheckPreviewStall();
        _ltcSyncController.Tick(Environment.TickCount64);

        int durationRc = _playbackApi.TryGetDuration(out double dur) ? 0 : -1;
        if (durationRc == 0 && SeekBarUpdateState.IsUsableDuration(dur))
            _duration = dur;

        TickMetadataFetch();
        // ロング GOP の判定は読み込み時の静的スキャン（0.4.5-C3、BeginGopScan）で行う。
        // 再生中の観測による旧判定（C2）は「読み込み位置から次のキーフレームまでの距離」を
        // 見ており、可変 GOP の実素材で誤検出した（中央値 0.708 秒の素材で 187 回発報）。
        // 比較のために残していた経路は v0.5.0 で削除した。

        // Gap 状態ではレンダーコールバックが止まるため、
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
        {
            try
            {
                TryCompleteGapFreeze(_renderSession.CaptureGeneration(), false, allowRedraw: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Gap freeze completion retry failed");
            }
        }

        if (_gapFreezeHandler.HasTimedOut())
        {
            Log.Warning("Continue mode: gap freeze final-frame capture timed out, holding current frame");
            _gapFreezeHandler.ForceFreezeComplete();
        }
    }

    // ── 0.4.5-C: ロング GOP 警告（表示のみ） ─────────────────────
    // 100ms タイマーで shim の検出値をポーリングする。同期の制御則・再生の可否には
    // 一切触れず、ステータス行とプレイリスト行の表示だけを更新する。


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
                    // v0.5.3 段 3b: 位置つきだけ同期の入口にも通す（経路 #2）。位置なしは
                    // PlaybackOperationsCoordinator.LoadFile が BeginSyncFileLoad(0) を通るので二重にしない。
                    if (startPos > 0)
                        _syncService.BeginFileLoad(startPos, _syncGateRenderedFrames.Read(),
                            loadIssuedQpc: 0, source: "auto-advance");
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

    // ── フレーム通知（UI 更新） ───────────────────────────────────

    /// <summary>
    /// shim のフレーム通知から Dispatcher 経由で呼ばれる（UI スレッド）。
    /// Gap のキャプチャ状態を接続し、フレームごとの UI 更新と GPU への状態送信を行う。
    /// 画像の合成は OutputEngine（GPU worker）が行う。
    /// </summary>
    private Task ProcessRenderFrameUpdateAsync(int renderGeneration, bool hasFrame)
    {
        TryCompleteGapFreeze(renderGeneration, hasFrame);
        if (_disposed || !_renderSession.IsCurrent(renderGeneration)) return Task.CompletedTask;
        SubmitOutputState();
        if (_disposed || !_renderSession.IsCurrent(renderGeneration)) return Task.CompletedTask;
        UpdatePerFrameUI();
        return Task.CompletedTask;
    }

    /// <summary>
    /// ギャップのフリーズを確定できるか見て、できれば確定する（同期。待つものは無い）。
    /// 最終フレームの画像は GPU 合成層が進入時に保存している。
    /// </summary>
    private void TryCompleteGapFreeze(int renderGeneration, bool hasFrame, bool allowRedraw = false)
    {
        // D21-b: 最終フレームの到着は OnSourceFrameReady（ソースフレームの位置）で判定する。
        // 描画コールバックだけではシーク前の実行中フレームを最終フレームと誤認する。

        if (_gapFreezeHandler.CurrentState == GapState.EnteringFreeze && !IsNativeSeeking() &&
            _playbackApi.IsPaused())
        {
            bool hasPosition = _playbackApi.TryGetTimePos(out double actualPos);
            GapFrameCaptureDecision decision = GapFrameCaptureCoordinator.Decide(
                _gapFreezeHandler.CurrentState,
                hasFrame,
                IsCurrentPathExpectedForGapFreeze(),
                hasPosition,
                actualPos,
                _gapFreezeHandler.PendingTargetSeconds,
                _fps > 0 ? _fps : GapFreezeHandler.DefaultFallbackFps,
                allowRedraw: allowRedraw,
                frameSeenSinceCapture: _gapFreezeHandler.FrameSeenSinceCapture);

            if (decision == GapFrameCaptureDecision.RenderAndCapture)
            {
                bool captured = GapFreezeCaptureOperation.Run(
                    _gapFreezeHandler, _loadedTrackId,
                    () => !_disposed && _renderSession.IsCurrent(renderGeneration),
                    stillCurrent => _renderSession.CanConfirmGapFreeze(renderGeneration,
                        () => stillCurrent() && IsNativeGapFreezeTargetReady()));
                if (captured)
                {
                    Log.Information("Continue mode: gap freeze activated, final frame captured");
                    // タイマー経由の再試行でも状態を送る: 一時停止中のデコーダは
                    // ネイティブシーク完了が観測できた後にコールバックを出さないことがある。
                    // GPU 合成層は Freeze 進入時のソース画像を自身で保存する（SaveFreeze）。
                    if (!_disposed && _renderSession.IsCurrent(renderGeneration))
                        SubmitOutputState();
                }
            }
        }
    }

    /// <summary>
    /// OutputEngine の GPU worker から呼ばれる（UI スレッドではない）。ソースフレームの位置（PTS）が
    /// 目標の最終フレームと一致したときだけ「届いた」と数える（D21-b）。
    /// 一致しないフレームはシーク前の実行中フレームなので、目標へ向けた再シークを予約する。
    /// </summary>
    private void OnSourceFrameReady(long qpc, int generation, long sequence, double positionSeconds)
    {
        _syncService.LatencyCompensator.ObserveFrameReady(qpc, generation, sequence);

        GapFreezeHandler handler = _gapFreezeHandler;
        double fps = _fps > 0 ? _fps : GapFreezeHandler.DefaultFallbackFps;
        // D32: 3 秒のタイムアウトで打ち切った後でも、同じギャップの目標に一致するフレームが
        // 遅れて届いたら捕捉を開き直して確定する（タイムアウトは Held のまま待ち続けない保険）。
        if (handler.CurrentState == GapState.FreezeComplete &&
            handler.IsLateConfirmFrame(positionSeconds, fps))
        {
            RequestGapFreezeLateFrameConfirm();
            return;
        }

        if (handler.CurrentState is not (GapState.EnteringFreeze or GapState.WaitingForFrameStep) ||
            handler.FrameSeenSinceCapture ||
            !double.IsFinite(positionSeconds))
        {
            return;
        }

        // 許容は 2 フレーム（フレーム先頭/終端の解釈差と実素材の端数を含む）。
        if (Math.Abs(positionSeconds - handler.PendingTargetSeconds) <= 2.0 / fps)
        {
            handler.NotifyFrameArrived();
            return;
        }

        RequestGapFreezeSeekRetryForStaleFrame(positionSeconds);
    }

    private int _gapFreezeLateFramePosted;

    /// <summary>
    /// D32: 遅延して届いた目標フレームで、タイムアウト済みのフリーズ捕捉を開き直す。
    /// GPU worker から呼ばれるため、状態の更新は UI スレッドで行う（1 件だけ予約する）。
    /// </summary>
    private void RequestGapFreezeLateFrameConfirm()
    {
        if (Interlocked.CompareExchange(ref _gapFreezeLateFramePosted, 1, 0) != 0)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Exchange(ref _gapFreezeLateFramePosted, 0);
            ConfirmLateGapFreezeFrame();
        });
    }

    private void ConfirmLateGapFreezeFrame()
    {
        if (_disposed)
            return;
        GapFreezeHandler handler = _gapFreezeHandler;
        if (handler.CurrentState != GapState.FreezeComplete || !handler.HasLateConfirmTarget)
            return;
        handler.ReopenCaptureForLateFrame();
        Log.Information(
            "Continue mode: late final-frame arrival after the capture timeout, retrying the freeze capture target={Target:F3}",
            handler.PendingTargetSeconds);
    }

    private int _gapFreezeStaleFrameRetryPosted;

    private void RequestGapFreezeSeekRetryForStaleFrame(double stalePositionSeconds)
    {
        // 連続で届くフレームごとに Dispatcher へ積まない（1 件だけ予約する）。
        if (Interlocked.CompareExchange(ref _gapFreezeStaleFrameRetryPosted, 1, 0) != 0)
            return;
        double expectedTarget = _gapFreezeHandler.PendingTargetSeconds;
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Interlocked.Exchange(ref _gapFreezeStaleFrameRetryPosted, 0);
            RetryGapFreezeSeekForStaleFrame(stalePositionSeconds, expectedTarget);
        });
    }

    private void RetryGapFreezeSeekForStaleFrame(double stalePositionSeconds, double expectedTarget)
    {
        if (_disposed)
            return;
        GapFreezeHandler handler = _gapFreezeHandler;
        if (handler.FrameSeenSinceCapture ||
            handler.CurrentState is not (GapState.EnteringFreeze or GapState.WaitingForFrameStep) ||
            Math.Abs(handler.PendingTargetSeconds - expectedTarget) > 0.001)
        {
            return;
        }

        double target = handler.PendingTargetSeconds;
        if (!handler.TryBeginSeekRetry())
        {
            Log.Warning(
                "Continue mode: stale gap freeze source frame and no seek retry left stalePosition={Stale:F3} target={Target:F3}",
                stalePositionSeconds, target);
            return;
        }

        bool seekSuccess = SeekTo(target);
        Log.Warning(
            "Continue mode: stale gap freeze source frame, reissuing final-frame seek stalePosition={Stale:F3} target={Target:F3} retry={Retry} seekOk={SeekOk}",
            stalePositionSeconds, target, handler.SeekRetryCount, seekSuccess);
        if (!seekSuccess)
            handler.ForceFreezeComplete();
    }

    private bool IsNativeSeeking()
    {
        string raw;
        bool seeking;
        if (_gstBackendState.Player == IntPtr.Zero)
        {
            raw = "<null>";
            seeking = true;
        }
        else
        {
            seeking = _playbackApi.IsSeeking();
            raw = seeking ? "yes" : "no";
        }
        _seekingProbe.Record(raw, seeking);
        return seeking;
    }

    // Recheck on the UI thread after the native render await. A manual operation can
    // start and finish during that await without changing the gap capture attempt.
    private bool IsNativeGapFreezeTargetReady()
    {
        if (IsNativeSeeking() || !_playbackApi.IsPaused())
            return false;
        if (!string.IsNullOrWhiteSpace(_gapFreezeHandler.PendingPath) &&
            !ContinueModePlaybackPolicy.IsExpectedMediaPath(
                _playbackApi.GetPath(), _gapFreezeHandler.PendingPath))
            return false;
        bool hasPosition = _playbackApi.TryGetTimePos(out double position);
        return GapFrameCaptureCoordinator.Decide(_gapFreezeHandler.CurrentState, true, true,
            hasPosition, position, _gapFreezeHandler.PendingTargetSeconds, _fps,
            frameSeenSinceCapture: _gapFreezeHandler.FrameSeenSinceCapture) ==
            GapFrameCaptureDecision.RenderAndCapture;
    }

    private bool IsCurrentPathExpectedForGapFreeze()
    {
        GapFreezePathCheckResult result = GapFreezePathGuard.Check(
            _playbackApi,
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
            // v0.5.3 段 3g: 読み直しが成功した後、同期側の口（ロード中の印を立てない）を通す。
            if (result.Load?.Success == true)
                _syncService.BeginGapFreezeLoad("path-guard");
            Log.Warning(
                "Continue mode: ignored stale gap freeze frame currentPath={CurrentPath} expectedPath={ExpectedPath}; reissued load target={Target:F3} loadOk={LoadOk} pauseOk={PauseOk}",
                result.CurrentPath, _gapFreezeHandler.PendingPath, _gapFreezeHandler.PendingTargetSeconds,
                result.Load?.Success, result.Pause?.Success);
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
        if (!IsPlayerReady) return;

        bool hasPosition = _playbackApi.TryGetTimePos(out double pos);

        double? gapTimelinePosition = PlaybackTimelinePositionPolicy.GetGapTimelinePosition(
            _gapFreezeHandler.IsInactive,
            _vm.Sync.SyncMode,
            _ltcSyncController.LastLtcSeconds);
        if (gapTimelinePosition.HasValue)
            _timelinePanel?.UpdatePlaybackPosition(gapTimelinePosition.Value);

        if (!hasPosition) return;

        if (!_playbackControl.IsPaused)
        {
            long windowGeneration = _playbackPerformanceStats.WindowGeneration;
            PlaybackPerformanceSnapshot? performance =
                _playbackPerformanceStats.RecordTick(pos, Stopwatch.GetElapsedTime(0), _gstPlaybackApi.Activity.Disturbances);
            if (performance != null)
            {
                LogPlaybackPerformance(performance);
                ObserveDecodeHealth(performance, pos);
            }
            else if (_playbackPerformanceStats.WindowGeneration != windowGeneration)
            {
                // 0.4.7: 窓が snapshot 無しで作り直された（最初の tick か、シークなどの操作をまたいで位置が戻った）。
                // 判定の基準を窓に合わせる。
                PlaybackActivityLedger activity = _gstPlaybackApi.Activity;
                _decodeHealth.BeginWindow(activity.Disturbances, activity.RateIntegralSeconds());
            }
        }
        // 表示は最後の落ち込みから一定時間で消える。毎 tick 見直す（変わらなければ何もしない）。
        _vm.Sync.DecodeHealthWarning = _decodeHealth.StatusText(DateTime.UtcNow);

        if (!_seekBarInteraction.IsSeeking)
        {
            double displayPos = pos;
            SetSeekBarValueFromPlayer(SeekBarUpdateState.ToSliderValue(displayPos, _duration, SeekBar.Value));
            _vm.Player.TimeLabel = $"{PlaybackTimeFormatter.FormatFrames(displayPos, _fps)} / {PlaybackTimeFormatter.FormatFrames(_duration, _fps)}";
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
        GetResourceDisposer().DisposeAll();
    }

    private MainWindowResourceDisposer GetResourceDisposer() => _resourceDisposer ??= new MainWindowResourceDisposer(
        disposeTimer: () =>
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer.Tick -= OnTick;
            }
        },
        disposeRenderContext: _renderSession.FreeContext,
        disposePlayer: _gstBackendState.DisposePlayer,
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
        disposeOutput: () => _outputEngine?.Dispose(),
        stopAcceptingNewWork: () => _disposed = true);

    private ExitCoordinator GetExitCoordinator()
    {
        if (_exitCoordinator != null) return _exitCoordinator;
        _exitDialogHost = new ExitDialogHost(this);
        _exitCoordinator = new ExitCoordinator(
            _exitDialogHost,
            GetResourceDisposer(),
            runOffUiThread: action => Task.Run(action),
            forceExit: ForceExitProcess,
            shutdownCompleted: () => Dispatcher.BeginInvoke(new Action(Close)));
        _exitDialogHost.CancelRequested += _exitCoordinator.CancelRequested;
        _exitDialogHost.NormalExitRequested += _exitCoordinator.NormalExitRequested;
        _exitDialogHost.ForceExitRequested += _exitCoordinator.ForceRequested;
        return _exitCoordinator;
    }

    // 強制終了: 追加確認なし。ログ 1 行、Environment.Exit(2)、2 秒の番人で Kill。
    private static void ForceExitProcess()
    {
        Log.Information("強制終了");
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(2000);
            try { Process.GetCurrentProcess().Kill(); } catch { }
        })
        {
            IsBackground = true,
            Name = "ExitCoordinator.Watchdog",
        };
        watchdog.Start();
        Environment.Exit(2);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        try
        {
            // Dispose() 済み（テスト含む）と Application.Shutdown 中はそのまま閉じる。通常は ExitCoordinator 経由。
            if (_disposed || (Application.Current?.Dispatcher.HasShutdownStarted ?? false)) return;
            e.Cancel = GetExitCoordinator().OnClosingRequested();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during MainWindow cleanup");
        }
    }

    // ── メタデータ取得 ────────────────────────────────────────────

    /// <summary>
    /// D33-b: ロード成功後のメタデータ取得を 1 回だけ予約する。ロードが速く 100ms タイマーの
    /// tick をまたぐ前に次のロードへ置き換わっても、ここで取得できる。サイズが未取得なら
    /// <see cref="FetchMetadata"/> はフラグを立てずに戻り、従来どおりタイマーが再試行する。
    /// </summary>
    private void ScheduleMetadataFetch()
    {
        if (_disposed) return;
        // D34: ロード直後にサイズ・尺が揃わない場合（caps 未確定を成功のまま返さない修正後も
        // 数 tick かかることがある）に備え、100ms tick の再試行期限をここで張り直す。
        _metadataFetchDeadlineUtc = DateTime.UtcNow.AddSeconds(MetadataFetchRetrySeconds);
        _metadataFetchTimedOutLogged = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_disposed)
                FetchMetadata();
        });
    }

    /// <summary>
    /// D34: タイマー tick からのメタデータ取得。期限（ロード後 5 秒）内はサイズ未取得でも
    /// 100ms ごとに再試行し、期限切れで 1 回だけ警告する。従来の「尺が取れたら取得」も残す。
    /// </summary>
    private void TickMetadataFetch()
    {
        if (_metadataFetched) return;
        bool withinWindow = _metadataFetchDeadlineUtc is { } deadline && DateTime.UtcNow < deadline;
        if (withinWindow || _duration > 0)
        {
            FetchMetadata();
            return;
        }
        if (_metadataFetchDeadlineUtc.HasValue && !_metadataFetchTimedOutLogged)
        {
            _metadataFetchTimedOutLogged = true;
            Log.Warning(
                "FetchMetadata: メタデータ（サイズ）が {Seconds} 秒以内に取得できませんでした",
                MetadataFetchRetrySeconds);
        }
    }

    /// <summary>
    /// 0.4.7: 読み込んだ素材のコーデックが推奨外なら、ステータス行とプレイリストで知らせる
    /// （<see cref="CodecAdvice"/>。表示だけで、再生は止めない）。
    /// </summary>
    private void ApplyCodecAdvice(string decoderName)
    {
        string text = CodecAdvice.StatusText(decoderName);
        _vm.Sync.CodecWarning = text;
        if (text.Length == 0 || !_loadedTrackId.HasValue) return;
        PlaylistTrack? track = _playlist.FindTrackById(_loadedTrackId.Value);
        if (track is null || track.CodecWarning) return;
        Log.Information("Codec warning: track={Track} decoder={Decoder}", track.Name, decoderName);
        _playlist.MarkCodecWarning(track.Id);
    }

    private void FetchMetadata()
    {
        if (_playbackApi.TryGetFps(out double fps) && fps > 0)
            _fps = fps;

        string vcodec = _playbackApi.GetVideoCodec();
        ApplyCodecAdvice(vcodec);
        // GStreamer 実装に音声デコーダ名の問い合わせは無い（常に空）。
        string acodec = "";

        if (!_playbackApi.TryGetSize(out int width, out int height) || width <= 0 || height <= 0)
            return;

        _metadataFetched = true;
        _metadataFetchDeadlineUtc = null;
        Log.Information("FetchMetadata: {W}x{H} {Fps:F3}fps V:{VCodec} A:{ACodec}",
            width, height, _fps, vcodec, acodec);

        _vm.Player.MetaLine = MetadataDisplayFormatter.FormatMetadataLine(
            width,
            height,
            _fps,
            vcodec,
            acodec);
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
        if (!IsPlaybackAvailable) return;

        SeekBarCommit commit = _seekBarInteraction.CreateCommit(sliderValue, SeekBar.Minimum, SeekBar.Maximum, _duration);
        if (!commit.ShouldCommit) return;

        _ltcSyncController.CancelPendingSync("seekbar-commit");
        _vm.Player.SeekBarValue = commit.SliderValue;
        bool success = SeekTo(commit.TargetSeconds);
        _playbackApi.TryGetTimePos(out double timePos);
        Log.Information(
            "Seek command sent source={Source} value={SliderValue:F6} duration={Duration:F3} target={Target:F3} success={Success} immediateTimePos={TimePos:F3}",
            source, commit.SliderValue, _duration, commit.TargetSeconds, success, timePos);
    }

    /// <summary>
    /// 0.4.7: 2 秒の窓で届いたフレームが期待より明らかに少なければ「デコードが追いついていない」と
    /// 表示する。シーク・一時停止・読み込みなどをまたいだ窓と、止まっている・ギャップの中は判定しない
    /// （<see cref="DecodeHealthMonitor"/>）。表示するだけで、同期の制御は変えない。
    /// </summary>
    private void ObserveDecodeHealth(PlaybackPerformanceSnapshot snapshot, double position)
    {
        PlaybackActivityLedger activity = _gstPlaybackApi.Activity;
        DecodeWindowVerdict verdict = _decodeHealth.Observe(
            snapshot.FrameUpdates, snapshot.Elapsed.TotalSeconds, _fps,
            activity.Disturbances, activity.RateIntegralSeconds(),
            position, _duration,
            pausedOrInGap: _playbackApi.IsPaused() || !_gapFreezeHandler.IsInactive,
            DateTime.UtcNow);
        if (verdict != DecodeWindowVerdict.Behind) return;
        (int delivered, int expected) = _decodeHealth.LastBehind;
        double elapsed = snapshot.Elapsed.TotalSeconds;
        // 窓の長さと平均の指示速度も残す（expected = fps × elapsed × rate）。期待値の基準が窓とずれていないかを
        // ログだけで確かめられるように（047cand1 の誤検知は、ここが窓より長い区間になっていた）。
        Log.Warning(
            "Decode behind: delivered={Delivered} expected={Expected} ratio={Ratio:F2} elapsed={Elapsed:F2}s rate={Rate:F3} fps={Fps:F3} position={Position:F3} codec={Codec} count={Count}",
            delivered, expected, expected > 0 ? delivered / (double)expected : 0,
            elapsed, _fps > 0 && elapsed > 0 ? expected / (_fps * elapsed) : 0, _fps, position,
            _playbackApi.GetVideoCodec(), _decodeHealth.BehindCount);
    }

    private void LogPlaybackPerformance(PlaybackPerformanceSnapshot snapshot)
    {
        RenderUpdateSchedulerStats renderStats = _renderSession.ConsumeUpdateStats();

        Log.Information(
            "Playback perf elapsed={Elapsed:F2}s expectedFps={ExpectedFps:F3} playbackRate={PlaybackRate:F3} displayedFps={DisplayedFps:F2} ticks={Ticks} renderCallbacks={RenderCallbacks} coalescedRenderCallbacks={CoalescedRenderCallbacks} renderReschedules={RenderReschedules} missedReschedules={MissedReschedules} renderUpdates={RenderUpdates} frameUpdates={FrameUpdates} renderedFrames={RenderedFrames} avgRenderMs={AvgRenderMs:F2} maxRenderMs={MaxRenderMs:F2} avgBitmapMs={AvgBitmapMs:F2} maxBitmapMs={MaxBitmapMs:F2} avgSpoutMs={AvgSpoutMs:F2} maxSpoutMs={MaxSpoutMs:F2} size={Width}x{Height} spoutEnabled={SpoutEnabled} gpuPublishedFrames={GpuPublishedFrames} gstRingOutsideFrames={GstRingOutsideFrames} frameBoundary=full-resolution-bitmap",
            snapshot.Elapsed.TotalSeconds, _fps, snapshot.PlaybackRate,
            snapshot.DisplayedFps, snapshot.TickCount, renderStats.Requests,
            renderStats.CoalescedRequests, renderStats.Reschedules, renderStats.MissedReschedules,
            snapshot.RenderUpdates,
            snapshot.FrameUpdates, snapshot.RenderedFrames, snapshot.AvgRenderMs,
            snapshot.MaxRenderMs, snapshot.AvgBitmapMs, snapshot.MaxBitmapMs,
            snapshot.AvgSpoutMs, snapshot.MaxSpoutMs, snapshot.Width,
            snapshot.Height, snapshot.SpoutEnabled, _outputEngine?.PublishedFrameCount ?? 0,
            _outputEngine?.GstRingOutsideFrames ?? 0);

        // 0.4.8: 前の絵を出し続けた tick の内訳。「送信は続いているのに中身が変わらない」を、理由
        // （shim のコピー完了待ち＝fencePending / 新しいフレームが来ない＝noNewFrame）付きで残す。
        // 1 フレーム程度の Held は平常なので、100ms 以上続いた窓だけ出す。
        if (_outputEngine?.TakeHoldSnapshot() is { } hold && hold.LongestHeldMs >= 100.0)
            Log.Information(
                "Output held: longestHeldMs={LongestHeldMs:F0} fencePendingTicks={FencePendingTicks} noNewFrameTicks={NoNewFrameTicks} newFrameTicks={NewFrameTicks} maxFenceWaitMs={MaxFenceWaitMs:F0}",
                hold.LongestHeldMs, hold.FencePendingTicks, hold.NoNewFrameTicks, hold.NewFrameTicks, hold.MaxFenceWaitMs);

        // 0.4.8: 位置照会が後退した区間（同期の補正を止めた区間）が終わったら、回数と最大幅を残す。
        if (_gstPlaybackApi.PositionContinuity.TakeEndedEpisode() is { } unstable)
            Log.Information(
                "Playback position was unstable: backward samples={Count} maxBackwardMs={MaxBackwardMs:F1} (held the latest position)",
                unstable.Count, unstable.MaxBackSeconds * 1000.0);

        // 0.4.8: 位置の戻りは窓を作り直さず、別の行で数える（性能ログを途切れさせない）。
        if (snapshot.BackwardJumps > 0)
            Log.Information(
                "Playback position went backward count={Count} maxBackwardMs={MaxBackwardMs:F1} elapsed={Elapsed:F2}s",
                snapshot.BackwardJumps, snapshot.MaxBackwardSeconds * 1000.0, snapshot.Elapsed.TotalSeconds);

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
        // 0.4.8: 一時停止・ロードをまたいだ Held を次の窓に持ち込まない。
        _outputEngine?.TakeHoldSnapshot();
        _renderSession.ResetUpdateStats();
    }

    private void ResetPlayerStateForNewTrack()
    {
        _renderSession.Invalidate();
        _metadataFetched = false;
        _metadataFetchDeadlineUtc = null;
        _metadataFetchTimedOutLogged = false;
        // D34: 前トラックのメタデータ表示（サイズ・fps）を次のロードまで残さない。
        _vm.Player.MetaLine = string.Empty;
        _duration = 0;
        _fps = 0;
        _renderSession.ResetUpdateStats();
        ResetPlaybackPerformanceStats();
        // 0.4.7: 素材ごとに数え直す。コーデックの警告も次の素材で判定し直す。
        _decodeHealth.Reset();
        _vm.Sync.DecodeHealthWarning = string.Empty;
        _vm.Sync.CodecWarning = string.Empty;
        _endAdvanceTriggered = false;
    }

    // ── ユーティリティ ────────────────────────────────────────────

    private static (double ScaleX, double ScaleY) GetDpiScale(Visual visual)
    {
        var dpi = VisualTreeHelper.GetDpi(visual);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
    }

}
