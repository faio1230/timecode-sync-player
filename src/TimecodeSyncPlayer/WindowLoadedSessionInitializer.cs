namespace TimecodeSyncPlayer;

internal enum WindowLoadedSessionInitializationError
{
    MpvCreateFailed,
    MpvInitializeFailed,
    RenderContextCreateFailed
}

internal sealed class WindowLoadedSessionInitializer
{
    private readonly Func<MpvSessionInitializationResult> _initializeMpvSession;
    private readonly Action<IntPtr> _assignMpv;
    private readonly Func<bool> _createRenderContext;
    private readonly Action _allocateRenderParameters;
    private readonly Func<SpoutStartupState> _initializeSpout;
    private readonly Action<SpoutStartupState> _applySpoutStartupState;
    private readonly Action _initializeFrameRenderer;
    private readonly Action _startTimer;
    private readonly Action _initializeStartupBuffer;
    private readonly Action _initializeTimeline;
    private readonly Action<WindowLoadedSessionInitializationError> _showError;

    public WindowLoadedSessionInitializer(
        Func<MpvSessionInitializationResult> initializeMpvSession,
        Action<IntPtr> assignMpv,
        Func<bool> createRenderContext,
        Action allocateRenderParameters,
        Func<SpoutStartupState> initializeSpout,
        Action<SpoutStartupState> applySpoutStartupState,
        Action initializeFrameRenderer,
        Action startTimer,
        Action initializeStartupBuffer,
        Action initializeTimeline,
        Action<WindowLoadedSessionInitializationError> showError)
    {
        _initializeMpvSession = initializeMpvSession;
        _assignMpv = assignMpv;
        _createRenderContext = createRenderContext;
        _allocateRenderParameters = allocateRenderParameters;
        _initializeSpout = initializeSpout;
        _applySpoutStartupState = applySpoutStartupState;
        _initializeFrameRenderer = initializeFrameRenderer;
        _startTimer = startTimer;
        _initializeStartupBuffer = initializeStartupBuffer;
        _initializeTimeline = initializeTimeline;
        _showError = showError;
    }

    public bool Initialize()
    {
        MpvSessionInitializationResult sessionResult = _initializeMpvSession();
        _assignMpv(sessionResult.Mpv);

        if (sessionResult.Failure == MpvSessionInitializationFailure.CreateFailed)
        {
            _showError(WindowLoadedSessionInitializationError.MpvCreateFailed);
            return false;
        }

        if (sessionResult.Failure == MpvSessionInitializationFailure.InitializeFailed)
        {
            _showError(WindowLoadedSessionInitializationError.MpvInitializeFailed);
            return false;
        }

        if (!_createRenderContext())
        {
            _showError(WindowLoadedSessionInitializationError.RenderContextCreateFailed);
            return false;
        }

        _allocateRenderParameters();
        SpoutStartupState spoutStartupState = _initializeSpout();
        _applySpoutStartupState(spoutStartupState);
        _initializeFrameRenderer();
        _startTimer();
        _initializeStartupBuffer();
        _initializeTimeline();

        return true;
    }
}
