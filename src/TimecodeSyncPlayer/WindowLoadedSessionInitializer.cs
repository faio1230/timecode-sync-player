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
    private readonly Action _applyAudioSettings;
    private readonly Func<bool> _createRenderContext;
    private readonly Func<SpoutStartupState> _initializeSpout;
    private readonly Action<SpoutStartupState> _applySpoutStartupState;
    private readonly Action _startTimer;
    private readonly Action _initializeTimeline;
    private readonly Action<WindowLoadedSessionInitializationError> _showError;

    public WindowLoadedSessionInitializer(
        Func<MpvSessionInitializationResult> initializeMpvSession,
        Action<IntPtr> assignMpv,
        Action applyAudioSettings,
        Func<bool> createRenderContext,
        Func<SpoutStartupState> initializeSpout,
        Action<SpoutStartupState> applySpoutStartupState,
        Action startTimer,
        Action initializeTimeline,
        Action<WindowLoadedSessionInitializationError> showError)
    {
        _initializeMpvSession = initializeMpvSession;
        _assignMpv = assignMpv;
        _applyAudioSettings = applyAudioSettings;
        _createRenderContext = createRenderContext;
        _initializeSpout = initializeSpout;
        _applySpoutStartupState = applySpoutStartupState;
        _startTimer = startTimer;
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

        _applyAudioSettings();

        if (!_createRenderContext())
        {
            _showError(WindowLoadedSessionInitializationError.RenderContextCreateFailed);
            return false;
        }

        SpoutStartupState spoutStartupState = _initializeSpout();
        _applySpoutStartupState(spoutStartupState);
        _startTimer();
        _initializeTimeline();

        return true;
    }
}
