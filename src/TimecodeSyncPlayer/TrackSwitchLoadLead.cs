using System.Diagnostics;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.1 項目 4: Continue のトラック切替で、読み込みにかかる時間ぶん先から読み込む。
///
/// 切替の読み込みは、発行から新しいトラックの最初のフレームまでの所要ぶんタイムコードより手前から始まる
/// （検証機の実測で、ずれは所要と 1 対 1 で一致）。所要は素材ごとにほぼ一定で（RTX、7 本 × 20 回で
/// 同じトラックの 2 回目以降の中央値からの差はおおむね ±40ms）、最初の 1 回（冷えた状態）だけ大きい。
/// そこで、トラックごとに**切替の読み込みの実測値**を覚え、2 回目以降の直近 <see cref="WarmWindow"/> 回の
/// 中央値だけ先から読み込む。
///
/// 0.4.5 の D37-h（キーフレーム間隔からの見積もりで先を狙い、行き過ぎた）とは、見積もりの出どころが違う:
/// 同じトラック・同じ切替の読み込みを実際に測った値だけを使う。1 回目は冷えた状態なので学習にも使わない。
/// シークの補償（<see cref="SeekLatencyCompensator"/>、既定で無効）とは別物で、シークには使わない。
/// </summary>
internal sealed class TrackSwitchLoadLead
{
    /// <summary>中央値を取る直近の標本数。</summary>
    public const int WarmWindow = 5;

    /// <summary>
    /// これを超える学習値は使わない（先回りしない）。検証機で最も重い素材（4K60 ProRes）が約 1.25 秒。
    /// 読み込みが異常に遅い状態で大きく先へ飛ばさないための上限。
    /// </summary>
    public const double MaxLeadSeconds = 3.0;

    /// <summary>止めるための環境変数。`off`（大文字小文字不問）のときだけ無効。</summary>
    public const string EnvironmentVariable = "TCS_SWITCH_LOAD_LEAD";

    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly Dictionary<Guid, TrackSamples> _byTrack = new();
    private bool _armed;
    private Guid _armedTrack;
    private long _armedQpc;
    private int _generationAtArm;
    private long _sequenceAtArm;
    private int _lastReadyGeneration = int.MinValue;
    private long _lastReadySequence = long.MinValue;

    public TrackSwitchLoadLead()
        : this(IsEnabledValue(Environment.GetEnvironmentVariable(EnvironmentVariable)))
    {
    }

    internal TrackSwitchLoadLead(bool enabled)
    {
        _enabled = enabled;
        Log.Information(
            "Track switch load lead: {State}（{Variable}=off で無効）",
            enabled ? "有効" : "無効", EnvironmentVariable);
    }

    internal static bool IsEnabledValue(string? value)
        => value is null || !value.Trim().Equals("off", StringComparison.OrdinalIgnoreCase);

    public bool IsEnabled => _enabled;

    /// <summary>このトラックへ切り替えるときに先回りする秒数（学習前・無効時は 0）。</summary>
    public double LeadForTrack(Guid trackId)
    {
        if (!_enabled) return 0.0;
        lock (_gate)
        {
            if (!_byTrack.TryGetValue(trackId, out TrackSamples? samples) || samples.Warm.Count == 0)
                return 0.0;
            double lead = Median(samples.Warm);
            return lead <= MaxLeadSeconds ? lead : 0.0;
        }
    }

    /// <summary>切替の読み込みを発行したときに呼ぶ（issuedQpc は発行直前の QPC）。</summary>
    public void MarkLoadSent(Guid trackId, long issuedQpc)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            _armed = true;
            _armedTrack = trackId;
            _armedQpc = issuedQpc != 0 ? issuedQpc : Stopwatch.GetTimestamp();
            _generationAtArm = _lastReadyGeneration;
            _sequenceAtArm = _lastReadySequence;
        }
    }

    /// <summary>切替以外の読み込み（手動の読み込みなど）を発行したとき。測定中の切替を捨てる。</summary>
    public void CancelMeasurement()
    {
        if (!_enabled) return;
        lock (_gate) _armed = false;
    }

    /// <summary>
    /// GPU worker。source.acquire が Ready になったときの QPC・世代・ソース sequence を受け取る。
    /// 発行時点より新しいフレームの最初の 1 枚を「絵が出た」とみなす。
    /// </summary>
    public void ObserveFrameReady(long qpc, int generation, long sourceSequence)
    {
        if (!_enabled) return;
        double seconds;
        Guid track;
        bool cold;
        double lead;
        lock (_gate)
        {
            if (generation > _lastReadyGeneration)
            {
                _lastReadyGeneration = generation;
                _lastReadySequence = sourceSequence;
            }
            else if (generation == _lastReadyGeneration && sourceSequence > _lastReadySequence)
            {
                _lastReadySequence = sourceSequence;
            }

            if (!_armed) return;
            bool isNewFrame = generation > _generationAtArm
                || (generation == _generationAtArm && sourceSequence > _sequenceAtArm);
            if (!isNewFrame) return;

            _armed = false;
            seconds = (qpc - _armedQpc) / (double)Stopwatch.Frequency;
            if (!double.IsFinite(seconds) || seconds < 0) return;

            track = _armedTrack;
            if (!_byTrack.TryGetValue(track, out TrackSamples? samples))
            {
                samples = new TrackSamples();
                _byTrack[track] = samples;
            }
            cold = !samples.SawFirstLoad;
            if (cold)
            {
                samples.SawFirstLoad = true;
            }
            else
            {
                samples.Warm.Add(seconds);
                if (samples.Warm.Count > WarmWindow)
                    samples.Warm.RemoveAt(0);
            }
            lead = samples.Warm.Count == 0 ? 0.0 : Median(samples.Warm);
        }

        Log.Information(
            "Track switch load lead: loadMs={LoadMs:F1} cold={Cold} leadMs={LeadMs:F1} track={Track}",
            seconds * 1000.0, cold, lead * 1000.0, track);
    }

    private static double Median(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    private sealed class TrackSamples
    {
        public bool SawFirstLoad;
        public readonly List<double> Warm = new();
    }
}
