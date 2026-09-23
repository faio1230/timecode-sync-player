using System.Diagnostics;

namespace TimecodeSyncPlayer;

/// <summary>
/// 0.4.8: 再生位置の連続性を見張り、同じ世代の中では位置を後退させない。
///
/// 復号が一時的に追いつかないと、オーディオシンクが詰まってパイプラインの位置照会が
/// 「止まったオーディオ位置」と「クロックから補間した位置」を行き来し、1 照会ごとに 100〜200ms
/// 前後に跳ねる（UIA 50ms 監査の失敗で実測）。この跳ねを同期に渡すと、補正が 0.9 倍と 1.1 倍を
/// 往復して揺れが続き、性能監視の窓も作り直され続ける。
///
/// 規則:
/// <list type="bullet">
/// <item>同じ系列（乱れの回数と shim の世代が同じ）の中では、直前までの最大値より小さい値は最大値で返す。</item>
/// <item><see cref="AnomalyThresholdSeconds"/> を超える後退は「不安定」として数え、最後の後退から
///   <see cref="UnstableHoldSeconds"/> の間は <see cref="IsUnstable"/> が true になる。</item>
/// <item><see cref="MaxHoldBackSeconds"/> を超える後退は、前の値のほうが誤りとみなして新しい値を受け入れる
///   （一度の外れ値で位置を長く止めない）。これも不安定として数える。</item>
/// <item>系列が変わったら（ロード・シーク・停止・一時停止・速度切替、または世代の変化）最大値を捨てる。</item>
/// </list>
/// スレッド安全（UI スレッド以外からの照会もあり得るため、短いロックで守る）。
/// </summary>
internal sealed class PlaybackPositionContinuity
{
    /// <summary>これを超える後退を「不安定」として数える（オーディオの 1024 サンプル刻みより小さい揺れは数えない）。</summary>
    internal const double AnomalyThresholdSeconds = 0.010;

    /// <summary>後退を最大値で抑える上限。これより大きい後退は新しい値を受け入れる。</summary>
    internal const double MaxHoldBackSeconds = 0.5;

    /// <summary>最後の後退から不安定とみなす長さ（観測した跳ねの間隔 50〜400ms を覆う）。</summary>
    internal const double UnstableHoldSeconds = 1.0;

    private readonly object _gate = new();
    private readonly Func<long> _getTimestamp;
    private (long Disturbances, ulong Generation)? _series;
    private double _max = double.NaN;
    private long _lastAnomalyTimestamp;
    private bool _hasAnomaly;
    private int _episodeCount;
    private double _episodeMaxBackSeconds;

    public PlaybackPositionContinuity(Func<long>? getTimestamp = null)
    {
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
    }

    /// <summary>これまでに数えた後退の総数（診断用）。</summary>
    public long BackwardSamples { get; private set; }

    /// <summary>
    /// 照会で得た位置を 1 つ通し、同期・表示に使う値を返す。<paramref name="generation"/> が
    /// 分からない照会（旧経路）は 0 を渡す（乱れの回数だけで系列を分ける）。
    /// </summary>
    public double Observe(double seconds, long disturbances, ulong generation)
    {
        if (!double.IsFinite(seconds))
            return seconds;
        lock (_gate)
        {
            var series = (disturbances, generation);
            if (_series != series)
            {
                // 世代が分からない照会（0）は、世代の分かる照会と同じ系列に数える。
                bool sameExceptUnknownGeneration = _series is { } previous &&
                    previous.Disturbances == disturbances &&
                    (previous.Generation == 0 || generation == 0);
                if (!sameExceptUnknownGeneration)
                {
                    _series = series;
                    _max = seconds;
                    return seconds;
                }
                if (generation != 0)
                    _series = series;
            }

            if (double.IsNaN(_max) || seconds >= _max)
            {
                _max = seconds;
                return seconds;
            }

            double back = _max - seconds;
            if (back > AnomalyThresholdSeconds)
                NoteAnomaly(back);
            if (back > MaxHoldBackSeconds)
            {
                _max = seconds;
                return seconds;
            }
            return _max;
        }
    }

    /// <summary>最後の後退から <see cref="UnstableHoldSeconds"/> 以内か。</summary>
    public bool IsUnstable()
    {
        lock (_gate)
        {
            return _hasAnomaly &&
                   (_getTimestamp() - _lastAnomalyTimestamp) < UnstableHoldSeconds * Stopwatch.Frequency;
        }
    }

    /// <summary>
    /// 不安定な区間が終わっていたら、その区間の後退回数と最大幅を返して区間を閉じる（ログ用）。
    /// 区間が続いている・無かったときは null。
    /// </summary>
    public (int Count, double MaxBackSeconds)? TakeEndedEpisode()
    {
        lock (_gate)
        {
            if (_episodeCount == 0)
                return null;
            if ((_getTimestamp() - _lastAnomalyTimestamp) < UnstableHoldSeconds * Stopwatch.Frequency)
                return null;
            var ended = (_episodeCount, _episodeMaxBackSeconds);
            _episodeCount = 0;
            _episodeMaxBackSeconds = 0;
            return ended;
        }
    }

    private void NoteAnomaly(double back)
    {
        BackwardSamples++;
        _episodeCount++;
        _episodeMaxBackSeconds = Math.Max(_episodeMaxBackSeconds, back);
        _lastAnomalyTimestamp = _getTimestamp();
        _hasAnomaly = true;
    }
}
