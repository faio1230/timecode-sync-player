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

    public MainWindowResourceDisposer(
        Action disposeTimer,
        Action disposeRenderContext,
        Action disposeMpv,
        Action disposeLtc,
        Action disposeSpout,
        Action disposeTimeline,
        Action disposeBuffer)
    {
        _disposeTimer = disposeTimer;
        _disposeRenderContext = disposeRenderContext;
        _disposeMpv = disposeMpv;
        _disposeLtc = disposeLtc;
        _disposeSpout = disposeSpout;
        _disposeTimeline = disposeTimeline;
        _disposeBuffer = disposeBuffer;
    }

    public void DisposeAll()
    {
        _disposeTimer();
        _disposeRenderContext();
        _disposeMpv();
        _disposeLtc();
        _disposeSpout();
        _disposeTimeline();
        _disposeBuffer();
    }
}
