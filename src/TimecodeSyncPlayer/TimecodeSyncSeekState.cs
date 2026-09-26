using Serilog;

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

    /// <summary>
    /// v0.5.3 段 3f: 直前の着地の記録だけを忘れる（読み込みで素材が変わるとき。§6 の 10）。
    /// <see cref="Clear"/> の意味は変えない（ほかの呼び出し元に影響させない）。
    /// </summary>
    public void ForgetLastSettled()
    {
        _lastSettledAt = DateTime.MinValue;
        _lastSettledTargetSeconds = double.NaN;
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
                // v0.5.4 段 0: 着地後の抑止（門 9）を着地（門 6）と分けて数える。
                Log.Debug("sync.gate post-settle-suppress elapsedMs={ElapsedMs:F1} target={Target:F3}",
                    (now - _lastSettledAt).TotalMilliseconds, _lastSettledTargetSeconds);
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
            // v0.5.4 段 0: 着地の確定（門 6）を数える（従来は pending "Settled" 行を門 9 と共有していた）。
            Log.Debug("sync.gate seek-settled target={Target:F3} elapsedMs={ElapsedMs:F1}",
                TargetSeconds, (now - _sentAt).TotalMilliseconds);
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
            // v0.5.4 段 0: 到達不能 pending の置き換え（門 8 の re-pend）を数える。
            Log.Debug("sync.gate pending-replace pendingTarget={PendingTarget:F3} requestedTarget={RequestedTarget:F3}",
                TargetSeconds, requestedTargetSeconds);
            TargetSeconds = Math.Max(0, requestedTargetSeconds);
            _sentAt = now;
            _settledAt = DateTime.MinValue;
            LastStatus = TimecodeSyncSeekPendingStatus.Pending;
            return false;
        }

        if (now - _sentAt >= _timeout)
        {
            // v0.5.4 段 0: 保留のタイムアウト（門 7）の実測時間を残す。
            Log.Debug("sync.gate pending-timeout elapsedMs={ElapsedMs:F1} target={Target:F3}",
                (now - _sentAt).TotalMilliseconds, TargetSeconds);
            Clear();
            LastStatus = TimecodeSyncSeekPendingStatus.TimedOut;
            return false;
        }

        LastStatus = TimecodeSyncSeekPendingStatus.Pending;
        return true;
    }

    /// <summary>
    /// D38 (b): 未信頼のフレームで、要求が pending の目標からも現在位置からも 4×tolerance を
    /// 超えて離れているとき、到達不能な pending を捨てる（置き換えず、再確認とゲートを
    /// 通してからシークさせる。現在位置の近くの要求は、pending の着地観測を残すため捨てない）。
    /// 捨てたときは LastStatus = Superseded（TrackSeekStatusTransition が再確認へ入る）。
    /// </summary>
    public bool DiscardIfUnreachable(
        double requestedTargetSeconds, double toleranceSeconds, double playbackSeconds)
    {
        if (!HasPendingSeek || !IsNewRequestFarFromPending(requestedTargetSeconds, toleranceSeconds))
            return false;
        // いま着地の窓に入っている pending は、捨てずに既存の着地判定（Settled）へ渡す。
        if (HasReachedSeekTarget(playbackSeconds, toleranceSeconds))
            return false;
        // 現在位置の近くの要求は、pending の着地観測を残すため捨てない。
        if (Math.Abs(requestedTargetSeconds - playbackSeconds) <=
            Math.Max(0, toleranceSeconds) * PendingSupersedeToleranceMultiplier)
            return false;
        Clear();
        LastStatus = TimecodeSyncSeekPendingStatus.Superseded;
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

    /// <summary>
    /// v0.5.2 段 0: ラッチが立っているかの読み取り専用の写し（特性テスト用。状態は変えない）。
    /// lastSettledRecent は、直近の着地の記録が <paramref name="now"/> の時点でまだ着地後の抑止
    /// （ShouldSuppressSeek の PostSettleSuppress）に効く状態か。
    /// </summary>
    internal IReadOnlyDictionary<string, bool> LatchSnapshot(DateTime now) => new Dictionary<string, bool>
    {
        ["pendingSeek"] = HasPendingSeek,
        ["lastSettledRecent"] = _lastSettledAt != DateTime.MinValue &&
            now - _lastSettledAt < PostSettleSuppress &&
            !double.IsNaN(_lastSettledTargetSeconds),
    };
}

public enum TimecodeSyncSeekPendingStatus
{
    None,
    Pending,
    Settled,
    TimedOut,
    /// <summary>D38 (b): 未信頼の間に、離れた新しい要求で到達不能な pending を捨てた。</summary>
    Superseded
}
