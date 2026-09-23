namespace TimecodeSyncPlayer;

/// <summary>
/// D37-a: 粗い同期シークの判定を瞬間値で出さないためのゲート。
/// 直近の窓（既定 250ms、LTC 25fps で 5 サンプル前後）の残差の中央値が許容を超えたときだけ
/// Seek を許す。サンプルが疎でも本物のずれを取り逃さないよう、許容超えが連続した場合も許す。
/// 前の採用サンプルからの変化が「経過時間 × 最大再生レート + LTC の粒度」を超えるサンプルは、
/// 再生位置が物理的に動けない量なので測定の乱れとして採用しない（弾いた回数を数える）。
/// 位置が飛ぶ操作（シーク発行・手動移動・ロード）の後は <see cref="Reset"/> で系列を切る。
/// 0.4.8: 弾いた直後の 1 サンプルを無条件に採用しない。弾く前の系列とつながれば系列を続け、
/// 弾いたサンプルとつながれば（本物の跳び）そこから新しい系列を始める。どちらでもなければ
/// また弾く。以前は弾くたびに系列を消していたため、真値と外れ値が交互に来ると外れ値が
/// 1 つおきに採用され、補正が 0.9 倍と 1.1 倍を往復した（UIA 50ms 監査の失敗で実測）。
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
    // 0.4.8: 直前に弾いたサンプル（本物の跳びなら次のサンプルがこれとつながる）。
    private double _candidateAt = double.NaN;
    private double _candidateDelta = double.NaN;

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
            ? AllowedChange(dt, ltcGranularitySeconds)
            : double.PositiveInfinity;
        double change = hasPrevious ? deltaSeconds - _lastAcceptedDelta : double.NaN;

        if (hasPrevious && double.IsFinite(change) && Math.Abs(change) > allowedChange)
        {
            bool confirmsCandidate = double.IsFinite(_candidateAt) && double.IsFinite(_candidateDelta) &&
                nowSeconds >= _candidateAt &&
                Math.Abs(deltaSeconds - _candidateDelta) <=
                    AllowedChange(nowSeconds - _candidateAt, ltcGranularitySeconds);
            if (!confirmsCandidate)
            {
                // 弾く。弾く前の系列は残し、このサンプルを「跳びの候補」として覚える。
                // 0.4.6: 連続回数は切る（瞬間値でシークしない守りを、弾いた直後にすり抜けさせない）。
                double previousDelta = _lastAcceptedDelta;
                RejectedSamples++;
                _candidateAt = nowSeconds;
                _candidateDelta = deltaSeconds;
                _consecutiveExceeded = 0;
                return new Result(false, true, Median(), _consecutiveExceeded, _samples.Count, RejectedSamples,
                    deltaSeconds, previousDelta, change, allowedChange, dt);
            }

            // 候補とつながった＝本物の跳び（着地・ロード・取りこぼし）。乱れの前後を混ぜず、
            // このサンプルから新しい系列を始める（以前の「弾いた次から測り直す」と同じ時機）。
            _samples.Clear();
            _consecutiveExceeded = 0;
        }
        _candidateAt = double.NaN;
        _candidateDelta = double.NaN;

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
        _candidateAt = double.NaN;
        _candidateDelta = double.NaN;
    }

    private double AllowedChange(double dtSeconds, double ltcGranularitySeconds) =>
        dtSeconds * _maxPlaybackRate + Math.Max(0.0, ltcGranularitySeconds);

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
