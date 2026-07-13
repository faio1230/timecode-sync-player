namespace TimecodeSyncPlayer;

internal sealed class TimecodeSyncSeekState : ITimecodeSyncSeekState
{
    private readonly TimeSpan _timeout;
    private DateTime _sentAt = DateTime.MinValue;
    private DateTime _settledAt = DateTime.MinValue;
    private DateTime _lastSettledAt = DateTime.MinValue;
    private double _lastSettledTargetSeconds = double.NaN;
    private static readonly TimeSpan SettleCooldown = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan PostSettleSuppress = TimeSpan.FromMilliseconds(500);
    private const double ContinuousPlaybackSettleSlackMultiplier = 2.0;

    public TimecodeSyncSeekState()
        : this(TimeSpan.FromSeconds(2))
    {
    }

    public TimecodeSyncSeekState(TimeSpan timeout)
    {
        _timeout = timeout;
    }

    public bool HasPendingSeek { get; private set; }
    public double TargetSeconds { get; private set; }
    public TimecodeSyncSeekPendingStatus LastStatus { get; private set; } = TimecodeSyncSeekPendingStatus.None;

    public void BeginSeek(double targetSeconds, DateTime sentAt)
    {
        _settledAt = DateTime.MinValue;
        TargetSeconds = Math.Max(0, targetSeconds);
        _sentAt = sentAt;
        HasPendingSeek = true;
        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
    }

    public void Clear()
    {
        _settledAt = DateTime.MinValue;
        HasPendingSeek = false;
        TargetSeconds = 0;
        _sentAt = DateTime.MinValue;
        LastStatus = TimecodeSyncSeekPendingStatus.None;
    }

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now)
    {
        if (!HasPendingSeek)
        {
            if (_lastSettledAt != DateTime.MinValue
                && now - _lastSettledAt < PostSettleSuppress
                && IsWithinSettledTarget(playbackSeconds, toleranceSeconds))
            {
                LastStatus = TimecodeSyncSeekPendingStatus.Settled;
                return true;
            }

            LastStatus = TimecodeSyncSeekPendingStatus.None;
            return false;
        }

        if (HasReachedSeekTarget(playbackSeconds, toleranceSeconds))
        {
            if (_settledAt == DateTime.MinValue)
                _settledAt = now;

            if (now - _settledAt < SettleCooldown)
            {
                LastStatus = TimecodeSyncSeekPendingStatus.Pending;
                return true;
            }

            _lastSettledAt = now;
            _lastSettledTargetSeconds = TargetSeconds;
            Clear();
            LastStatus = TimecodeSyncSeekPendingStatus.Settled;
            return true;               // セットルティックも抑止（1-tick 隙間を閉じる）
        }

        if (now - _sentAt >= _timeout)
        {
            Clear();
            LastStatus = TimecodeSyncSeekPendingStatus.TimedOut;
            return false;
        }

        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
        return true;
    }

    private bool HasReachedSeekTarget(double playbackSeconds, double toleranceSeconds)
    {
        double boundedTolerance = Math.Max(0, toleranceSeconds);
        double lowerBound = TargetSeconds - boundedTolerance;
        double upperBound = TargetSeconds + (boundedTolerance * ContinuousPlaybackSettleSlackMultiplier);

        return playbackSeconds >= lowerBound && playbackSeconds <= upperBound;
    }

    private bool IsWithinSettledTarget(double playbackSeconds, double toleranceSeconds)
    {
        if (double.IsNaN(_lastSettledTargetSeconds))
            return false;

        return Math.Abs(playbackSeconds - _lastSettledTargetSeconds) <= Math.Max(0, toleranceSeconds);
    }
}

public enum TimecodeSyncSeekPendingStatus
{
    None,
    Pending,
    Settled,
    TimedOut
}
