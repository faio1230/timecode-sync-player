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
    // D20-b: 到達不能な pending を置き換える距離（tolerance の倍数）。
    private const double PendingSupersedeToleranceMultiplier = 4.0;
    // D37-b: 着地までの実測時間（秒）。素材ごとに学習し、シークと速度補正の分岐に使う。
    // 異常値（復帰不能なほど長い、0 に近すぎる）は学習に混ぜない。
    private const double LearnedSeekMinSeconds = 0.05;
    private const double LearnedSeekMaxSeconds = 10.0;
    private const double LearnedSeekEmaKeep = 0.7;
    private double _learnedSeekSeconds = double.NaN;

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

    /// <summary>D37-b: 着地までの実測時間（移動平均）。未学習は null。</summary>
    public double? LearnedSeekDurationSeconds =>
        double.IsFinite(_learnedSeekSeconds) ? _learnedSeekSeconds : null;

    /// <summary>D37-b: 素材が変わったとき（ロード）に学習を捨てる。</summary>
    public void ResetLearning() => _learnedSeekSeconds = double.NaN;

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

    public bool ShouldSuppressSeek(double playbackSeconds, double toleranceSeconds, DateTime now,
        double requestedTargetSeconds = double.NaN)
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

            // D37-b: 着地までの実測時間を学習する（目標に到達したと最初に観測した時刻まで）。
            if (_sentAt != DateTime.MinValue)
                LearnSeekDuration((_settledAt == DateTime.MinValue ? now : _settledAt) - _sentAt);
            _lastSettledAt = now;
            _lastSettledTargetSeconds = TargetSeconds;
            Clear();
            LastStatus = TimecodeSyncSeekPendingStatus.Settled;
            return true;               // セットルティックも抑止（1-tick 隙間を閉じる）
        }

        // D20-b (ii): 到達不能な pending（例: 終端静止中の target 0）は、新しい要求が
        // pending の目標から離れていればその要求で置き換え、今回のシークを抑止しない。
        if (IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
        {
            TargetSeconds = Math.Max(0, requestedTargetSeconds);
            _sentAt = now;
            _settledAt = DateTime.MinValue;
            LastStatus = TimecodeSyncSeekPendingStatus.Pending;
            return false;
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

    /// <summary>
    /// D20-b: 新しい要求が pending の目標から離れているか。連続して進む LTC の経路では
    /// pending と要求はほぼ一致するため置き換えは起きない。
    /// </summary>
    private bool IsNewRequestFarFromPending(double requestedTargetSeconds, double toleranceSeconds)
    {
        if (!double.IsFinite(requestedTargetSeconds))
            return false;

        double distance = Math.Abs(requestedTargetSeconds - TargetSeconds);
        return distance > Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier;
    }

    private void LearnSeekDuration(TimeSpan elapsed)
    {
        double seconds = elapsed.TotalSeconds;
        if (seconds < LearnedSeekMinSeconds || seconds > LearnedSeekMaxSeconds)
            return;
        _learnedSeekSeconds = double.IsFinite(_learnedSeekSeconds)
            ? _learnedSeekSeconds * LearnedSeekEmaKeep + seconds * (1.0 - LearnedSeekEmaKeep)
            : seconds;
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
