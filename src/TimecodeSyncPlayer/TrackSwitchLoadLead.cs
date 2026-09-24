using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// v0.5.1 項目 4: Continue のトラック切替で、読み込みにかかる時間ぶん先から読み込む。
///
/// 切替の読み込みは、発行から再生が始まるまでの所要ぶんタイムコードより手前から始まる
/// （検証機の実測で、切替直後のずれは所要と 1 対 1 で一致）。所要は素材ごとにほぼ一定で、
/// 最初の 1 回（冷えた状態）だけ大きい。そこでトラックごとに「その切替で本当に必要だった先回り」を覚え、
/// 2 回目以降の直近 <see cref="WarmWindow"/> 回の中央値だけ先から読み込む。
///
/// 必要だった先回り = 使った先回り + 読み込み後の最初の評価で残ったずれ（素材位置 − 再生位置）。
/// 候補 1 では「発行 → 最初の絵が合成に届く」を測って使ったが、これは再生の開始より 0.1 秒ほど遅く、
/// 軽い素材で 0.13〜0.17 秒先回りし過ぎた。また先から読み込むと長 GOP の素材では読み込みそのものが
/// 伸びる。残ったずれを直接足し込めば、何が所要に含まれるかに関係なく、使った位置での実際の必要量を学べる。
///
/// 0.4.5 の D37-h（キーフレーム間隔からの見積もりで先を狙い、行き過ぎた）とは、見積もりの出どころが違う:
/// 同じトラック・同じ切替の実測だけを使う。1 回目は冷えた状態なので学習にも使わない。
/// シークの補償（<see cref="SeekLatencyCompensator"/>、既定で無効）とは別物で、シークには使わない。
/// </summary>
internal sealed class TrackSwitchLoadLead
{
    /// <summary>中央値を取る直近の標本数。</summary>
    public const int WarmWindow = 5;

    /// <summary>
    /// これを超える学習値は使わない（先回りしない）。検証機で最も重い素材（4K60 ProRes）が約 1.25 秒。
    /// 読み込みが異常に遅い状態で大きく先へ飛ばさないための上限。標本もこの範囲の外は捨てる
    /// （読み込み中に LTC が飛んだなど、切替の所要と関係のないずれ）。
    /// </summary>
    public const double MaxLeadSeconds = 3.0;

    /// <summary>止めるための環境変数。`off`（大文字小文字不問）のときだけ無効。</summary>
    public const string EnvironmentVariable = "TCS_SWITCH_LOAD_LEAD";

    private readonly object _gate = new();
    private readonly bool _enabled;
    private readonly Dictionary<Guid, TrackSamples> _byTrack = new();
    private bool _armed;
    private Guid _armedTrack;
    private double _armedLeadSeconds;

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
            return Median(samples.Warm);
        }
    }

    /// <summary>切替の読み込みを発行したときに呼ぶ（leadSeconds は実際に使った先回り）。</summary>
    public void MarkLoadSent(Guid trackId, double leadSeconds)
    {
        if (!_enabled) return;
        lock (_gate)
        {
            _armed = true;
            _armedTrack = trackId;
            _armedLeadSeconds = leadSeconds;
        }
    }

    /// <summary>切替以外の読み込み（手動の読み込みなど）を発行したとき。測定中の切替を捨てる。</summary>
    public void CancelMeasurement()
    {
        if (!_enabled) return;
        lock (_gate) _armed = false;
    }

    /// <summary>
    /// 読み込みが安定した後の最初の評価で呼ぶ（UI スレッド）。residualSeconds は素材位置 − 再生位置
    /// （正 = 映像がタイムコードより手前）。測定中の切替と同じトラックのときだけ 1 回数える。
    /// </summary>
    public void ObserveFirstResidual(Guid loadedTrackId, double residualSeconds)
    {
        if (!_enabled) return;
        double used, needed, lead;
        bool cold, accepted;
        lock (_gate)
        {
            if (!_armed) return;
            _armed = false;
            if (loadedTrackId != _armedTrack || !double.IsFinite(residualSeconds)) return;

            used = _armedLeadSeconds;
            needed = used + residualSeconds;
            if (!_byTrack.TryGetValue(_armedTrack, out TrackSamples? samples))
            {
                samples = new TrackSamples();
                _byTrack[_armedTrack] = samples;
            }
            cold = !samples.SawFirstLoad;
            samples.SawFirstLoad = true;
            accepted = !cold && needed >= 0.0 && needed <= MaxLeadSeconds;
            if (accepted)
            {
                samples.Warm.Add(needed);
                if (samples.Warm.Count > WarmWindow)
                    samples.Warm.RemoveAt(0);
            }
            lead = samples.Warm.Count == 0 ? 0.0 : Median(samples.Warm);
        }

        Log.Information(
            "Track switch load lead: usedMs={UsedMs:F1} residualMs={ResidualMs:F1} neededMs={NeededMs:F1} cold={Cold} accepted={Accepted} leadMs={LeadMs:F1} track={Track}",
            used * 1000.0, residualSeconds * 1000.0, needed * 1000.0, cold, accepted, lead * 1000.0, loadedTrackId);
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
