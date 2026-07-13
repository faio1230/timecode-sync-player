namespace TimecodeSyncPlayer;

internal sealed class FileLoadStabilityLogState
{
    private readonly TimeSpan _interval;
    private DateTime _lastLoggedAt = DateTime.MinValue;

    public FileLoadStabilityLogState(TimeSpan interval)
    {
        _interval = interval;
    }

    public bool ShouldLog(DateTime now)
    {
        if (_lastLoggedAt == DateTime.MinValue || now - _lastLoggedAt >= _interval)
        {
            _lastLoggedAt = now;
            return true;
        }

        return false;
    }

    public void Reset()
    {
        _lastLoggedAt = DateTime.MinValue;
    }
}
