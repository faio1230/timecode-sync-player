namespace TimecodeSyncPlayer;

public sealed class MainWindowResourceDisposer
{
    private readonly Action _disposeTimer;
    private readonly Action _disposeRenderContext;
    private readonly Action _disposeMpv;
    private readonly Action _disposeLtc;
    private readonly Action _disposeSpout;
    private readonly Action _disposeTimeline;
    private readonly Action _disposeBuffer;
    private readonly Action? _stopRender;
    private readonly Action? _closeFullscreen;
    private bool _attempted;

    public MainWindowResourceDisposer(
        Action disposeTimer,
        Action disposeRenderContext,
        Action disposeMpv,
        Action disposeLtc,
        Action disposeSpout,
        Action disposeTimeline,
        Action disposeBuffer,
        Action? stopRender = null,
        Action? closeFullscreen = null)
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
    }

    public void DisposeAll()
    {
        // A failing native dispose may already have released part of its resource.
        // Do not automatically repeat it on a second/reentrant Window.Dispose call.
        if (_attempted) return;
        _attempted = true;
        var errors = new List<Exception>();
        bool stopped = TryCleanup(_stopRender, errors);
        TryCleanup(_closeFullscreen, errors);
        TryCleanup(_disposeTimer, errors);
        bool contextFreed = stopped && TryCleanup(_disposeRenderContext, errors);
        if (contextFreed) TryCleanup(_disposeMpv, errors);
        TryCleanup(_disposeLtc, errors);
        if (stopped) TryCleanup(_disposeSpout, errors);
        TryCleanup(_disposeTimeline, errors);
        // Context, callback, buffers and mpv must stay alive together if native free fails.
        if (contextFreed) TryCleanup(_disposeBuffer, errors);
        if (errors.Count != 0) throw new AggregateException("MainWindow resource cleanup failed", errors);
    }

    private static bool TryCleanup(Action? cleanup, List<Exception> errors)
    {
        try
        {
            cleanup?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            errors.Add(ex);
            return false;
        }
    }
}
