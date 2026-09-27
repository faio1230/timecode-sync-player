using System.Diagnostics;

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
    public const string StopPlaybackStepName = "GStreamer 停止";
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
    private readonly Action _disposePlayer;
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
    // v0.5.4（終了時の間欠の切り分け）: 段と各処理の開始・終了（経過 ms）を Debug で出す。記録だけ。
    private readonly Action<string> _debugLog;
    private int _nextStage;
    private bool _attempted;
    private bool _stopped;
    private bool _outputStopped;
    private bool _contextFreed;

    public MainWindowResourceDisposer(
        Action disposeTimer,
        Action disposeRenderContext,
        Action disposePlayer,
        Action disposeLtc,
        Action disposeSpout,
        Action disposeTimeline,
        Action disposeBuffer,
        Action? stopRender = null,
        Action? closeFullscreen = null,
        Action? stopOutput = null,
        Action? disposeOutput = null,
        Action? stopAcceptingNewWork = null,
        Action<string>? debugLog = null)
    {
        _debugLog = debugLog ?? DefaultDebugLog;
        _disposeTimer = disposeTimer;
        _disposeRenderContext = disposeRenderContext;
        _disposePlayer = disposePlayer;
        _disposeLtc = disposeLtc;
        _disposeSpout = disposeSpout;
        _disposeTimeline = disposeTimeline;
        _disposeBuffer = disposeBuffer;
        _stopRender = stopRender;
        _closeFullscreen = closeFullscreen;
        _stopOutput = stopOutput;
        _disposeOutput = disposeOutput;
        _stopAcceptingNewWork = stopAcceptingNewWork;

        // I8: 新規受付停止 → RenderSession.Stop → OutputEngine.Stop → 全画面閉 → player destroy
        // → OutputEngine.Dispose → Spout → バッファ。従来の順序をそのまま段階へ分割する。
        _stages =
        [
            new(StopAcceptingStepName, RunsOffUiThread: false, () => TryCleanup("stopAcceptingNewWork", _stopAcceptingNewWork)),
            new(StopPlaybackStepName, RunsOffUiThread: true, () => _stopped = TryCleanup("stopRender", _stopRender)),
            new(StopOutputStepName, RunsOffUiThread: true, () => _outputStopped = TryCleanup("stopOutput", _stopOutput)),
            new(CloseFullscreenStepName, RunsOffUiThread: false, () =>
            {
                TryCleanup("closeFullscreen", _closeFullscreen);
                TryCleanup("disposeTimer", _disposeTimer);
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: true, () =>
            {
                _contextFreed = _stopped
                    ? TryCleanup("disposeRenderContext", _disposeRenderContext)
                    : Skip("disposeRenderContext", "stopped=False");
                // 0.4.8: shim の破棄は、GPU worker が止まってリースを返し終えた（OutputEngine.Stop が
                // 成功した）ときだけ。止まっていない worker がリングを参照したまま shim を消さない。
                if (_contextFreed && (_outputStopped || _stopOutput == null)) TryCleanup("disposePlayer", _disposePlayer);
                else Skip("disposePlayer", "contextFreed=" + _contextFreed + " outputStopped=" + _outputStopped);
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () => TryCleanup("disposeLtc", _disposeLtc)),
            new(ReleaseResourcesStepName, RunsOffUiThread: true, () =>
            {
                if (_outputStopped || _stopOutput == null) TryCleanup("disposeOutput", _disposeOutput);
                else Skip("disposeOutput", "outputStopped=False");
                if (_stopped) TryCleanup("disposeSpout", _disposeSpout);
                else Skip("disposeSpout", "stopped=False");
            }),
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () => TryCleanup("disposeTimeline", _disposeTimeline)),
            // RenderSession.Dispose（コンテキスト解放とネイティブスレッド join）は UI スレッド専用。
            new(ReleaseResourcesStepName, RunsOffUiThread: false, () =>
            {
                if (_contextFreed) TryCleanup("disposeBuffer", _disposeBuffer);
                else Skip("disposeBuffer", "contextFreed=False");
            }),
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
        int index = _nextStage;
        ResourceCleanupStage stage = _stages[index];
        long started = Stopwatch.GetTimestamp();
        _debugLog("stage.begin index=" + index + " step=" + stage.StepName + " offUi=" + stage.RunsOffUiThread +
            " thread=" + Environment.CurrentManagedThreadId);
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
            _debugLog("stage.end index=" + index + " step=" + stage.StepName + " elapsedMs=" + ElapsedMs(started));
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

    private bool TryCleanup(string name, Action? cleanup)
    {
        if (cleanup == null) return true;
        long started = Stopwatch.GetTimestamp();
        _debugLog("action.begin name=" + name);
        bool ok = false;
        try
        {
            cleanup();
            ok = true;
            return true;
        }
        catch (Exception ex)
        {
            _errors.Add(ex);
            return false;
        }
        finally
        {
            _debugLog("action.end name=" + name + " ok=" + ok + " elapsedMs=" + ElapsedMs(started));
        }
    }

    /// <summary>条件で実行しなかった処理を記録する（ふだん出る行が無いときに、飛ばしたのか止まったのかを分ける）。</summary>
    private bool Skip(string name, string reason)
    {
        _debugLog("action.skip name=" + name + " " + reason);
        return false;
    }

    private static void DefaultDebugLog(string fields) => Serilog.Log.Debug("shutdown {Fields}", fields);

    private static string ElapsedMs(long started) =>
        Stopwatch.GetElapsedTime(started).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}
