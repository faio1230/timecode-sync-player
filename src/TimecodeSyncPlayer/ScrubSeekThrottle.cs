namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 F-3・R-3・F-2: つかんだまま動かす間のシーク（スクラブ）の間引き。UI に依存しない純粋な部品。
/// 固定の間隔ではなく「前のシークが着地するまで次を送らない」。飛行中のシークは常に 1 本だけで、
/// 飛行中に来た目標は最新の 1 つだけ覚え、着地したときに 1 本送る。離したときの 1 本は飛行中でも必ず送り、
/// それが最後の 1 本になる（離した後に古い目標を送らない）。
/// 着地は呼び出し側が世代で判定する（<see cref="HasLanded"/>: 配信の世代が送ったシークの世代に追いつく。
/// <see cref="PlaybackPositionFeedback"/> と同じ比べ）。着地の時間切れは置かない（離したときの 1 本が必ず締める）。
/// 値の単位は問わない（シークバーはスライダーの値、タイムラインは秒）。
/// </summary>
internal sealed class ScrubSeekThrottle
{
    private bool _active;
    private bool _inFlight;
    private bool _generationKnown;
    private ulong _seekGeneration;
    private double? _pending;
    private double? _lastSent;

    /// <summary>つかんでいる間（<see cref="Begin"/> から <see cref="Release"/> まで）。</summary>
    public bool IsActive => _active;

    /// <summary>送ったシークがまだ着地していない。</summary>
    public bool InFlight => _inFlight;

    /// <summary>飛行中のシークの世代（<see cref="SetSeekGeneration"/> で決まる）。</summary>
    public ulong SeekGeneration => _seekGeneration;

    /// <summary>飛行中に覚えている最新の目標（無ければ null）。</summary>
    public double? Pending => _pending;

    /// <summary>
    /// つかんだ。前のつかみで送ったシークが飛行中なら、それはそのまま飛行中として扱う
    /// （飛行中は常に 1 本だけ。新しいつかみの最初の目標はその着地を待つ）。
    /// </summary>
    public void Begin()
    {
        _active = true;
        _pending = null;
        _lastSent = null;
    }

    /// <summary>動かした。飛行中でなければ送る目標を返す。飛行中なら最新の目標だけ覚えて null。</summary>
    public double? Move(double target)
    {
        if (!_active)
            return null;
        if (_inFlight)
        {
            _pending = target;
            return null;
        }
        if (_lastSent == target)
            return null;
        return Send(target);
    }

    /// <summary>
    /// 着地した。覚えた目標があり、最後に送った値と違えば、送る目標を返す。
    /// 離した後（<see cref="Release"/> の後）は覚えた目標を捨て、何も送らない。
    /// </summary>
    public double? Landed()
    {
        if (!_inFlight)
            return null;
        _inFlight = false;
        _generationKnown = false;
        double? pending = _pending;
        _pending = null;
        if (!_active || pending is not double next)
            return null;
        if (_lastSent == next)
            return null;
        return Send(next);
    }

    /// <summary>離した。飛行中でも必ず送る（最後の 1 本）。以後の着地では何も送らない。</summary>
    public double Release(double target)
    {
        _active = false;
        _pending = null;
        return Send(target);
    }

    /// <summary>飛行中のシークの世代が分かっている。</summary>
    public bool GenerationKnown => _inFlight && _generationKnown;

    /// <summary>
    /// 送ったシークの世代（送った後の最初に取れた照会の現在世代）を覚える。shim はシークの準備で世代を
    /// 進めるので、送った後の現在世代はシークの世代以上になる。シークの直後は照会が失敗しうるので
    /// （新しい世代の位置がまだ無い）、取れなかったときは次に取れた照会で覚える。
    /// </summary>
    public void SetSeekGeneration(ulong generation)
    {
        if (!_inFlight || generation == 0)
            return;
        _seekGeneration = generation;
        _generationKnown = true;
    }

    /// <summary>送れなかった（シークの失敗・世代が取れない）。飛行中を解く。覚えた目標は残す。</summary>
    public void SendFailed()
    {
        _inFlight = false;
        _generationKnown = false;
    }

    /// <summary>配信の世代が、飛行中のシークの世代に追いついたか（追いついたら着地）。</summary>
    public bool HasLanded(ulong deliveredGeneration) =>
        _inFlight && _generationKnown && IsLanded(deliveredGeneration, _seekGeneration);

    /// <summary>
    /// 世代での着地の判定（<see cref="PlaybackPositionFeedback"/> の着地の確認と同じ比べ）。
    /// 配信が無い（世代 0）ときは着地していない。
    /// </summary>
    public static bool IsLanded(ulong deliveredGeneration, ulong seekGeneration) =>
        deliveredGeneration > 0 && deliveredGeneration >= seekGeneration;

    private double Send(double target)
    {
        _inFlight = true;
        _generationKnown = false;
        _lastSent = target;
        return target;
    }
}

/// <summary>
/// v0.6.6 スクラブの計測（記録だけ）: 1 回のつかみで送った本数と、飛行の所要（送ってから着地まで）。
/// 判断には使わない。
/// </summary>
internal sealed class ScrubFlightStats
{
    private readonly List<double> _flightsMs = [];

    public int Sent { get; private set; }

    public IReadOnlyList<double> FlightsMs => _flightsMs;

    public void Reset()
    {
        Sent = 0;
        _flightsMs.Clear();
    }

    public void NoteSent() => Sent++;

    public void NoteFlight(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds >= 0)
            _flightsMs.Add(milliseconds);
    }

    public double MedianMs()
    {
        if (_flightsMs.Count == 0) return double.NaN;
        double[] sorted = _flightsMs.Order().ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    public double MaxMs() => _flightsMs.Count == 0 ? double.NaN : _flightsMs.Max();

    /// <summary>1 秒あたりの送った本数（つかんでいた時間で割る）。</summary>
    public double SentPerSecond(double heldSeconds) =>
        heldSeconds > 0 ? Sent / heldSeconds : double.NaN;
}
