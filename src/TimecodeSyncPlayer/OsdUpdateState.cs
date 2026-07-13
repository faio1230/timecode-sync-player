namespace TimecodeSyncPlayer;

public sealed class OsdUpdateState
{
    private readonly TimeSpan _minimumInterval;
    private int? _lastFrame;
    private DateTime _lastUpdatedAt = DateTime.MinValue;

    public OsdUpdateState(TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));

        _minimumInterval = minimumInterval;
    }

    public bool ShouldUpdate(int frame, DateTime now)
    {
        if (_lastFrame == frame)
            return false;

        if (_lastFrame != null && now - _lastUpdatedAt < _minimumInterval)
            return false;

        _lastFrame = frame;
        _lastUpdatedAt = now;
        return true;
    }

    public void Reset()
    {
        _lastFrame = null;
        _lastUpdatedAt = DateTime.MinValue;
    }
}
