namespace TimecodeSyncPlayer;

/// <summary>終了手順の 1 実行単位。RunsOffUiThread が true の手順は 50ms 以上ブロックし得る。</summary>
internal sealed record ResourceCleanupStage(string StepName, bool RunsOffUiThread, Action Run);

/// <summary>
/// MainWindow の資源解放を I8 の順序で実行する（段階 5.1 で段階実行に対応）。
/// 手順名は終了ダイアログの進捗表示（5 行）に対応し、この順序は変更しない。
/// </summary>
internal sealed class MainWindowResourceDisposer
{
    public const string StopAcceptingStepName = "新規受付停止";
    public const string StopPlaybackStepName = "mpv／GStreamer 停止";
    public const string StopOutputStepName = "出力停止（Spout 完了待ち）";
    public const string CloseFullscreenStepName = "全画面終了";
    public const string ReleaseResourcesStepName = "資源解放";

    /// <summary>終了ダイアログに表示する 5 手順（I8 順）。</summary>
    public static readonly IReadOnlyList<string> StepNames =
    [
        StopAcceptingStepName,
        StopPlaybackStepName,
        StopOutputStepName,
        CloseFullscreenStepName,
        ReleaseResourcesStepName,
    ];

    private readonly Action? _stopAcceptingNewWork;
    private readonly Action _disposeTimer;
    private readonly Action _disposeRenderContext;
    private readonly Action _disposeMpv;
    private readonly Action _disposeLtc;
    private readonly Action _disposeSpout;
    private readonly Action _disposeTimeline;
    private readonly Action _disposeBuffer;
    private readonly Action? _stopRender;
    private readonly Action? _stopOutput;
    private readonly Action? _disposeOutput;
    private readonly Action? _closeFullscreen;
    private readonly List<ResourceCleanupStage> _stages;
    private readonly List<Exception> _errors = new();
    private int _nextStage;
    private bool _attempted;
    private bool _stopped;
    private bool _outputStopped;
    private bool _contextFreed;

    public MainWindowResourceDisposer(
        Action disposeTimer,
        Action disposeRenderContext,
        Action disposeMpv,
        Action disposeLtc,
        Action disposeSpout,
        Action disposeTimeline,
        Action disposeBuffer,
        Action? stopRender = null,
        Action? closeFullscreen = null,
        Action? stopOutput = null,
        Action? disposeOutput = null,
        Action? stopAcceptingNewWork = null)
    {
        _disposeTimer = disposeTimer;
        _disposeRenderContext = disposeRenderContext;
        _disposeMpv = disposeMpv;
        _disposeLtc = disposeLtc;
        _disposeSpout = disposeSpout;
        _disposeTimeline = disposeTimeline;
        _disposeBuffer = disposeBuffer;
        _stopRender = stopRender;
        _closeFullscreen = closeFullscreen;
        _stopOutput = stopOutput;
        _disposeOutput = disposeOutput;
        _stopAcceptingNewWork = stopAcceptingNewWork;

        // I8: 新規受付停止 → RenderSession.Stop → OutputEngine.Stop → 全画面閉 → mpv/shim destroy
        // → OutputEngine.Dispose → Spout → バッファ。従来の順序をそのまま段階へ分割する。
        _stages =
        [
            new(StopAcceptingStepName, RunsOffUiThread: false, () => TryCleanup(_stopAcceptingNewWork)),
            new(StopPlaybackStepName, RunsOffUiThread: true, () => _stopped = TryCleanup(_stopRender)),
            new(StopOutputStepName, RunsOffUiThread: true, () => _outputStopped = TryCleanup(_stopOutput)),
            new(CloseFullscreenStepName, RunsOffUiThread: false, () =>
            {
                TryCleanup(_closeFullscreen);
                TryCleanup(_disposeTimer);
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: true, () =>
            {
                _contextFreed = _stopped && TryCleanup(_disposeRenderContext);
                if (_contextFreed) TryCleanup(_disposeMpv);
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () => TryCleanup(_disposeLtc)),
            new(ReleaseResourcesStepName, RunsOffUiThread: true, () =>
            {
                if (_outputStopped || _stopOutput == null) TryCleanup(_disposeOutput);
                if (_stopped) TryCleanup(_disposeSpout);
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () => TryCleanup(_disposeTimeline)),
            // RenderSession.Dispose は UI 所有の PreviewFramePresenter（DispatcherTimer）を解放するため UI スレッド専用。
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () => { if (_contextFreed) TryCleanup(_disposeBuffer); }),
        ];
    }

    public bool HasMoreStages => _nextStage < _stages.Count;

    public IReadOnlyList<Exception> Errors => _errors;

    public ResourceCleanupStage? PeekNextStage() => HasMoreStages ? _stages[_nextStage] : null;

    /// <summary>次の 1 手順を実行する。失敗は Errors に集約し、例外は投げない。</summary>
    public void RunNextStage()
    {
        if (!HasMoreStages) return;
        _attempted = true;
        ResourceCleanupStage stage = _stages[_nextStage];
        try
        {
            stage.Run();
        }
        catch (Exception ex)
        {
            _errors.Add(ex);
        }
        finally
        {
            _nextStage++;
        }
    }

    public void DisposeAll()
    {
        // A failing native dispose may already have released part of its resource.
        // Do not automatically repeat it on a second/reentrant Window.Dispose call.
        if (_attempted) return;
        _attempted = true;
        while (HasMoreStages) RunNextStage();
        if (_errors.Count != 0) throw new AggregateException("MainWindow resource cleanup failed", _errors);
    }

    private bool TryCleanup(Action? cleanup)
    {
        try
        {
            cleanup?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add(ex);
            return false;
        }
    }
}
