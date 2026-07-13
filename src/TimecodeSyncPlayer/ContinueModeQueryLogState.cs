namespace TimecodeSyncPlayer;

internal sealed class ContinueModeQueryLogState
{
    private readonly TimeSpan _interval;
    private readonly double _mediaPositionToleranceSeconds;
    private DateTime _lastLoggedAt = DateTime.MinValue;
    private TimelineQueryStatus? _lastStatus;
    private string? _lastTrackName;
    private double _lastMediaPositionSeconds;

    public ContinueModeQueryLogState(TimeSpan interval, double mediaPositionToleranceSeconds)
    {
        _interval = interval;
        _mediaPositionToleranceSeconds = mediaPositionToleranceSeconds;
    }

    public bool ShouldLog(
        TimelineQueryStatus status,
        string? trackName,
        double mediaPositionSeconds,
        DateTime now)
    {
        bool changed =
            _lastStatus != status ||
            !string.Equals(_lastTrackName, trackName, StringComparison.Ordinal) ||
            Math.Abs(_lastMediaPositionSeconds - mediaPositionSeconds) > _mediaPositionToleranceSeconds;

        bool elapsed = _lastLoggedAt == DateTime.MinValue || now - _lastLoggedAt >= _interval;

        if (!changed && !elapsed)
            return false;

        _lastStatus = status;
        _lastTrackName = trackName;
        _lastMediaPositionSeconds = mediaPositionSeconds;
        _lastLoggedAt = now;
        return true;
    }
}
