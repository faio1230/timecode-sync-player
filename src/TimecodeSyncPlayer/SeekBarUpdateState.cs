namespace TimecodeSyncPlayer;

public sealed class SeekBarUpdateState : ISeekBarUpdateState
{
    private readonly TimeSpan _timeout;
    private readonly double _settleToleranceSeconds;
    private DateTime _sentAt = DateTime.MinValue;
    private double _targetSeconds;

    public SeekBarUpdateState()
        : this(TimeSpan.FromSeconds(5), settleToleranceSeconds: 0.5)
    {
    }

    public SeekBarUpdateState(TimeSpan timeout, double settleToleranceSeconds = 0.5)
    {
        _timeout = timeout;
        _settleToleranceSeconds = settleToleranceSeconds;
    }

    public bool HasPendingSeek { get; private set; }
    public double TargetSeconds => _targetSeconds;

    public void MarkSeekSent(double targetSeconds, DateTime sentAt)
    {
        _targetSeconds = Math.Max(0, targetSeconds);
        _sentAt = sentAt;
        HasPendingSeek = true;
    }

    public void Clear()
    {
        HasPendingSeek = false;
        _sentAt = DateTime.MinValue;
        _targetSeconds = 0;
    }

    public double GetDisplayPosition(double playerPositionSeconds, DateTime now)
    {
        if (!HasPendingSeek)
            return playerPositionSeconds;

        if (Math.Abs(playerPositionSeconds - _targetSeconds) <= _settleToleranceSeconds)
        {
            Clear();
            return playerPositionSeconds;
        }

        if (now - _sentAt >= _timeout)
        {
            Clear();
            return playerPositionSeconds;
        }

        return _targetSeconds;
    }

    public static double ToSliderValue(double positionSeconds, double durationSeconds, double fallbackValue)
    {
        if (!IsUsableDuration(durationSeconds))
            return Math.Clamp(fallbackValue, 0, 1);

        return Math.Clamp(positionSeconds / durationSeconds, 0, 1);
    }

    public static double ToSliderValueFromPointer(double pointerX, double actualWidth, double fallbackValue)
    {
        if (!double.IsFinite(pointerX) || !double.IsFinite(actualWidth) || actualWidth <= 0)
            return Math.Clamp(fallbackValue, 0, 1);

        return Math.Clamp(pointerX / actualWidth, 0, 1);
    }

    public static bool IsUsableDuration(double durationSeconds)
        => double.IsFinite(durationSeconds) && durationSeconds > 0;
}
