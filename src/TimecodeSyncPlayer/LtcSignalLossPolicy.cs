namespace TimecodeSyncPlayer;

internal enum LtcSignalLossAction
{
    None,
    Pause,
    ResumeAndSync
}

internal sealed record LtcSignalLossContext(
    LtcSignalLossMode Mode,
    bool SyncEnabled,
    bool IsMonitoring,
    bool IsGapActive,
    bool IsPlaybackPaused);

internal sealed class LtcSignalLossMonitoringState
{
    private bool _stoppedUnexpectedly;

    public bool IsDetectionActive(bool isReportedRunning) =>
        isReportedRunning || _stoppedUnexpectedly;

    public void MarkStarted() => _stoppedUnexpectedly = false;

    public bool MarkStopped(Exception? exception)
    {
        _stoppedUnexpectedly = exception != null;
        return !_stoppedUnexpectedly;
    }
}

/// <summary>
/// LTC signal-loss edge detection and recovery hysteresis without external side effects.
/// </summary>
internal sealed class LtcSignalLossPolicy
{
    private readonly TimeSpan _timeout;
    private readonly int _resumeFrameCount;
    private long? _lastValidFrameAtMilliseconds;
    private bool _isLost;
    private bool _pausedByPolicy;
    private bool _manualResumeSuppressesPause;
    private bool? _lastIsPlaybackPaused;
    private int _consecutiveResumeFrames;

    public LtcSignalLossPolicy(TimeSpan timeout, int resumeFrameCount)
    {
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (resumeFrameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(resumeFrameCount));

        _timeout = timeout;
        _resumeFrameCount = resumeFrameCount;
    }

    public bool ShouldSuppressSync => _isLost && _pausedByPolicy;
    public bool IsLost => _isLost;

    public void Reset()
    {
        _lastValidFrameAtMilliseconds = null;
        _isLost = false;
        _pausedByPolicy = false;
        _manualResumeSuppressesPause = false;
        _lastIsPlaybackPaused = null;
        _consecutiveResumeFrames = 0;
    }

    public LtcSignalLossAction ObserveValidFrame(long receivedAtMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        ObservePlaybackState(context);

        if (!_isLost)
        {
            _lastValidFrameAtMilliseconds = receivedAtMilliseconds;
            _consecutiveResumeFrames = 0;
            return LtcSignalLossAction.None;
        }

        _lastValidFrameAtMilliseconds = receivedAtMilliseconds;
        _consecutiveResumeFrames++;
        if (_consecutiveResumeFrames < _resumeFrameCount)
            return LtcSignalLossAction.None;

        bool canApplyPolicyOwnedResume =
            context.SyncEnabled &&
            context.IsMonitoring &&
            !context.IsGapActive;
        if (_pausedByPolicy && !canApplyPolicyOwnedResume)
            return LtcSignalLossAction.None;

        _isLost = false;
        _consecutiveResumeFrames = 0;
        _manualResumeSuppressesPause = false;
        bool shouldResume = _pausedByPolicy;
        _pausedByPolicy = false;

        return shouldResume
            ? LtcSignalLossAction.ResumeAndSync
            : LtcSignalLossAction.None;
    }

    public LtcSignalLossAction Evaluate(long nowMilliseconds, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        ObservePlaybackState(context);

        if (_isLost)
        {
            if (_consecutiveResumeFrames > 0 &&
                _lastValidFrameAtMilliseconds.HasValue &&
                ElapsedMilliseconds(_lastValidFrameAtMilliseconds.Value, nowMilliseconds) >= _timeout.TotalMilliseconds)
            {
                _consecutiveResumeFrames = 0;
            }

            return EvaluatePause(context);
        }

        if (!_lastValidFrameAtMilliseconds.HasValue ||
            ElapsedMilliseconds(_lastValidFrameAtMilliseconds.Value, nowMilliseconds) < _timeout.TotalMilliseconds)
            return LtcSignalLossAction.None;

        _isLost = true;
        _consecutiveResumeFrames = 0;
        return EvaluatePause(context);
    }

    private LtcSignalLossAction EvaluatePause(LtcSignalLossContext context)
    {
        if (context.Mode != LtcSignalLossMode.Stop ||
            !context.SyncEnabled ||
            context.IsGapActive ||
            context.IsPlaybackPaused ||
            _pausedByPolicy ||
            _manualResumeSuppressesPause)
        {
            return LtcSignalLossAction.None;
        }

        _pausedByPolicy = true;
        return LtcSignalLossAction.Pause;
    }

    private void ObservePlaybackState(LtcSignalLossContext context)
    {
        if (_isLost &&
            !context.IsPlaybackPaused &&
            (_pausedByPolicy || _lastIsPlaybackPaused == true))
        {
            _pausedByPolicy = false;
            _manualResumeSuppressesPause = true;
        }

        _lastIsPlaybackPaused = context.IsPlaybackPaused;
    }

    private static long ElapsedMilliseconds(long earlier, long later) =>
        Math.Max(0, later - earlier);
}
