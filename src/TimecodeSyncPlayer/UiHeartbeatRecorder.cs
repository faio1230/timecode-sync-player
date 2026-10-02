namespace TimecodeSyncPlayer;

/// <summary>
/// 起動直後の UI スレッドの生存記録（v0.5.4、L-2 の UIA の時間切れの切り分け）。
/// 起動から <see cref="Window"/> の間、<see cref="Interval"/> ごとの tick を 1 行ずつ記録する。
/// 遅れ（lateMs）は「直前の tick から interval 後」という予定からの遅れ（DispatcherTimer は発火のたびに
/// 次の予定を取り直すため、固定の格子ではなく直前の tick を基準にする）。
/// 区間の始まりと終わりの行を必ず出すので、始まりの後に行が途切れていれば、その時刻から UI スレッドが
/// 回っていない。記録だけで、製品の判断・状態には使わない。
/// </summary>
internal sealed class UiHeartbeatRecorder
{
    /// <summary>tick の間隔。</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// 記録する区間（起動から）。実測の起動から最初の同期の適用までは 8.8〜18.8 秒（2026-09-26〜27 の app ログ）。
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly Action<string> _write;
    private readonly Action<UiHeartbeatSummary>? _onFinished;
    private TimeSpan _startedAt;
    private TimeSpan _previousTick;
    private long _seq;
    private double _maxLateMs;
    private double _firstLateMs = double.NaN;
    private bool _active;
    private bool _finished;

    /// <param name="write">1 行ずつの記録（Debug）。</param>
    /// <param name="finished">v0.6.3 段 1: 区間の終わりの要約（配布ビルドでも出す Information の 1 行用）。</param>
    public UiHeartbeatRecorder(Action<string> write, Action<UiHeartbeatSummary>? finished = null)
    {
        _write = write;
        _onFinished = finished;
    }

    public bool IsActive => _active;

    /// <summary>v0.6.4 段 6: seq=1 の遅れ（まだ tick が無ければ NaN）。Startup timing の行が読む。</summary>
    public double FirstLateMs => _firstLateMs;

    /// <summary>区間を始める（1 回だけ）。</summary>
    public void Start(TimeSpan now)
    {
        if (_active || _finished) return;
        _active = true;
        _startedAt = now;
        _previousTick = now;
        _write("start intervalMs=" + Format(Interval.TotalMilliseconds) + " windowMs=" + Format(Window.TotalMilliseconds));
    }

    /// <summary>tick を記録する。区間が終わったら終わりの行を出して false を返す（呼び出し側はタイマーを捨てる）。</summary>
    public bool Tick(TimeSpan now)
    {
        if (!_active) return false;
        _seq++;
        double lateMs = (now - _previousTick - Interval).TotalMilliseconds;
        _previousTick = now;
        _maxLateMs = Math.Max(_maxLateMs, lateMs);
        if (_seq == 1)
            _firstLateMs = lateMs;
        _write("seq=" + _seq + " lateMs=" + Format(lateMs));
        if (now - _startedAt < Window) return true;
        Finish(now, "window");
        return false;
    }

    /// <summary>区間の途中で止める（終了手順）。区間の外では何も出さない。</summary>
    public void Stop(TimeSpan now, string reason)
    {
        if (!_active) return;
        Finish(now, reason);
    }

    private void Finish(TimeSpan now, string reason)
    {
        _active = false;
        _finished = true;
        double elapsedMs = (now - _startedAt).TotalMilliseconds;
        _write("end reason=" + reason + " ticks=" + _seq + " maxLateMs=" + Format(_maxLateMs) +
            " elapsedMs=" + Format(elapsedMs));
        _onFinished?.Invoke(new UiHeartbeatSummary(reason, _seq, _firstLateMs, _maxLateMs, elapsedMs));
    }

    private static string Format(double value) =>
        value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// v0.6.3 段 1: 起動直後の UI スレッドの生存記録の要約。FirstLateMs は seq=1 の遅れ（tick が 1 度も無ければ NaN）。
/// </summary>
internal sealed record UiHeartbeatSummary(string Reason, long Ticks, double FirstLateMs, double MaxLateMs, double ElapsedMs);
