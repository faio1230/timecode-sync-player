namespace TimecodeSyncPlayer;

/// <summary>
/// D37-a: 粗い同期シークの判定を瞬間値で出さないためのゲート。
/// 直近の窓（既定 250ms、LTC 25fps で 5 サンプル前後）の残差の中央値が許容を超えたときだけ
/// Seek を許す。サンプルが疎でも本物のずれを取り逃さないよう、許容超えが連続した場合も許す。
/// 前の採用サンプルからの変化が「経過時間 × 最大再生レート + LTC の粒度」を超えるサンプルは、
/// 再生位置が物理的に動けない量なので測定の乱れとして採用しない（弾いた回数を数える）。
/// 位置が飛ぶ操作（シーク発行・手動移動・ロード）の後は <see cref="Reset"/> で系列を切る。
/// </summary>
internal sealed class SeekDecisionGate
{
    internal readonly record struct Result(
        bool ShouldSeek,
        bool Rejected,
        double MedianSeconds,
        int ConsecutiveExceeded,
        int Samples,
        long RejectedTotal,
        double DeltaSeconds,
        double PreviousDeltaSeconds,
        double ChangeSeconds,
        double AllowedChangeSeconds,
        double DtSeconds);

    private readonly double _windowSeconds;
    private readonly int _minSamples;
    private readonly int _consecutiveLimit;
    private readonly double _maxPlaybackRate;
    private readonly List<(double At, double Delta)> _samples = new();
    private int _consecutiveExceeded;
    private double _lastAcceptedAt = double.NaN;
    private double _lastAcceptedDelta = double.NaN;

    public SeekDecisionGate(
        double windowSeconds = 0.25,
        int minSamples = 3,
        int consecutiveLimit = 5,
        double maxPlaybackRate = 1.2)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (minSamples < 1)
            throw new ArgumentOutOfRangeException(nameof(minSamples));
        if (consecutiveLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(consecutiveLimit));
        if (!double.IsFinite(maxPlaybackRate) || maxPlaybackRate < 1.0)
            throw new ArgumentOutOfRangeException(nameof(maxPlaybackRate));

        _windowSeconds = windowSeconds;
        _minSamples = minSamples;
        _consecutiveLimit = consecutiveLimit;
        _maxPlaybackRate = maxPlaybackRate;
    }

    /// <summary>採用済みサンプル数（窓内）。</summary>
    public int Samples => _samples.Count;

    /// <summary>許容を連続で超えている採用サンプル数。</summary>
    public int ConsecutiveExceeded => _consecutiveExceeded;

    /// <summary>これまでに弾いたサンプル数（リセットでは消さない）。</summary>
    public long RejectedSamples { get; private set; }

    /// <summary>
    /// 残差の 1 サンプルを観測する。nowSeconds は単調な秒（QPC 等）、ltcGranularitySeconds は
    /// LTC 1 フレーム分の粒度。ShouldSeek が true でも、呼び出し側の追い越し判定（pending 等）は
    /// 従来どおり別に行う。
    /// </summary>
    public Result Observe(
        double deltaSeconds,
        double toleranceSeconds,
        double nowSeconds,
        double ltcGranularitySeconds)
    {
        Prune(nowSeconds);

        bool hasPrevious = double.IsFinite(_lastAcceptedAt) && double.IsFinite(_lastAcceptedDelta);
        double dt = hasPrevious ? nowSeconds - _lastAcceptedAt : double.NaN;
        double allowedChange = hasPrevious && double.IsFinite(dt) && dt >= 0
            ? dt * _maxPlaybackRate + Math.Max(0.0, ltcGranularitySeconds)
            : double.PositiveInfinity;
        double change = hasPrevious ? deltaSeconds - _lastAcceptedDelta : double.NaN;

        if (hasPrevious && double.IsFinite(change) && Math.Abs(change) > allowedChange)
        {
            // ありえない変化の後は、その前の系列もつながっていない可能性がある（着地・ロード・
            // 取りこぼし）。乱れの前後を混ぜず、次のサンプルから測り直す。弾いた回数は残す。
            double previousDelta = _lastAcceptedDelta;
            RejectedSamples++;
            double medianBeforeClear = Median();
            _samples.Clear();
            _lastAcceptedAt = double.NaN;
            _lastAcceptedDelta = double.NaN;
            return new Result(false, true, medianBeforeClear, _consecutiveExceeded, 0, RejectedSamples,
                deltaSeconds, previousDelta, change, allowedChange, dt);
        }

        _samples.Add((nowSeconds, deltaSeconds));
        _lastAcceptedAt = nowSeconds;
        _lastAcceptedDelta = deltaSeconds;
        _consecutiveExceeded = Math.Abs(deltaSeconds) > toleranceSeconds ? _consecutiveExceeded + 1 : 0;

        double median = Median();
        bool shouldSeek =
            (_samples.Count >= _minSamples && Math.Abs(median) > toleranceSeconds) ||
            _consecutiveExceeded >= _consecutiveLimit;
        return new Result(shouldSeek, false, median, _consecutiveExceeded, _samples.Count, RejectedSamples,
            deltaSeconds, double.NaN, double.NaN, allowedChange, dt);
    }

    /// <summary>系列を切る（シーク発行・手動移動・ロードの後）。弾いた回数は残す。</summary>
    public void Reset()
    {
        _samples.Clear();
        _consecutiveExceeded = 0;
        _lastAcceptedAt = double.NaN;
        _lastAcceptedDelta = double.NaN;
    }

    private void Prune(double nowSeconds)
    {
        _samples.RemoveAll(sample => nowSeconds - sample.At > _windowSeconds);
        if (_samples.Count == 0)
        {
            // 窓が空になったら区間の連続性も切る（次のサンプルは変化量の検査をしない）。
            _lastAcceptedAt = double.NaN;
            _lastAcceptedDelta = double.NaN;
        }
    }

    private double Median()
    {
        if (_samples.Count == 0)
            return 0.0;
        double[] sorted = _samples
            .Select(sample => sample.Delta)
            .Where(double.IsFinite)
            .Order()
            .ToArray();
        if (sorted.Length == 0)
            return 0.0;
        int middle = sorted.Length / 2;
        return sorted.Length % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) * 0.5;
    }
}
