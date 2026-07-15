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

/// <summary>
/// LTC signal-loss edge detection and recovery hysteresis without external side effects.
/// </summary>
internal sealed class LtcSignalLossPolicy
{
    private readonly TimeSpan _timeout;
    private readonly int _resumeFrameCount;
    private DateTime? _lastValidFrameAt;
    private bool _isLost;
    private bool _pausedByPolicy;
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

    public void Reset()
    {
        _lastValidFrameAt = null;
        _isLost = false;
        _pausedByPolicy = false;
        _consecutiveResumeFrames = 0;
    }

    public LtcSignalLossAction ObserveValidFrame(DateTime receivedAt, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        DetectManualPlaybackResume(context);

        if (!_isLost)
        {
            _lastValidFrameAt = receivedAt;
            _consecutiveResumeFrames = 0;
            return LtcSignalLossAction.None;
        }

        _lastValidFrameAt = receivedAt;
        _consecutiveResumeFrames++;
        if (_consecutiveResumeFrames < _resumeFrameCount)
            return LtcSignalLossAction.None;

        _isLost = false;
        _consecutiveResumeFrames = 0;
        bool shouldResume =
            _pausedByPolicy &&
            context.SyncEnabled &&
            context.IsMonitoring &&
            !context.IsGapActive;
        _pausedByPolicy = false;

        return shouldResume
            ? LtcSignalLossAction.ResumeAndSync
            : LtcSignalLossAction.None;
    }

    public LtcSignalLossAction Evaluate(DateTime now, LtcSignalLossContext context)
    {
        if (!context.IsMonitoring)
        {
            Reset();
            return LtcSignalLossAction.None;
        }

        DetectManualPlaybackResume(context);

        if (_isLost)
        {
            if (_consecutiveResumeFrames > 0 &&
                _lastValidFrameAt.HasValue &&
                now - _lastValidFrameAt.Value >= _timeout)
            {
                _consecutiveResumeFrames = 0;
            }

            return LtcSignalLossAction.None;
        }

        if (!_lastValidFrameAt.HasValue || now - _lastValidFrameAt.Value < _timeout)
            return LtcSignalLossAction.None;

        _isLost = true;
        _consecutiveResumeFrames = 0;

        if (context.Mode != LtcSignalLossMode.Stop ||
            !context.SyncEnabled ||
            context.IsGapActive ||
            context.IsPlaybackPaused)
        {
            return LtcSignalLossAction.None;
        }

        _pausedByPolicy = true;
        return LtcSignalLossAction.Pause;
    }

    private void DetectManualPlaybackResume(LtcSignalLossContext context)
    {
        if (_pausedByPolicy && !context.IsPlaybackPaused)
            _pausedByPolicy = false;
    }
}
