using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

internal enum WindowLoadedSessionInitializationError
{
    PlaybackInitializeFailed,
    RenderContextCreateFailed
}

internal sealed class WindowLoadedSessionInitializer
{
    private readonly Func<PlaybackResult> _initializePlayback;
    private readonly Action _applyAudioSettings;
    private readonly Func<bool> _createRenderContext;
    private readonly Func<SpoutStartupState> _initializeSpout;
    private readonly Action<SpoutStartupState> _applySpoutStartupState;
    private readonly Action _startTimer;
    private readonly Action _initializeTimeline;
    private readonly Action<WindowLoadedSessionInitializationError> _showError;

    public WindowLoadedSessionInitializer(
        Func<PlaybackResult> initializePlayback,
        Action applyAudioSettings,
        Func<bool> createRenderContext,
        Func<SpoutStartupState> initializeSpout,
        Action<SpoutStartupState> applySpoutStartupState,
        Action startTimer,
        Action initializeTimeline,
        Action<WindowLoadedSessionInitializationError> showError)
    {
        _initializePlayback = initializePlayback;
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
        PlaybackResult session = _initializePlayback();
        if (!session.Success)
        {
            _showError(WindowLoadedSessionInitializationError.PlaybackInitializeFailed);
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
