using System.Diagnostics;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// D7-a: 同期シーク／ロードの先行補償。シーク決定（seek.decide）またはロード発行から、
/// 新しい世代の最初のフレームが Ready になるまでの遅延 L を学習し、行き先だけを +L へ動かす
/// （シーク回数・デッドゾーンは変えない）。学習前・未観測トラックは L=0 で現行と同一の行き先になる。
///
/// L はトラック単位で保持し、トラック切替では捨てない（同じ素材が繰り返し呼ばれる運用に合わせる）。
/// 各トラックの最初の 1 標本は α=1.0（観測値をそのまま採用）、2 回目以降は α=0.25 の EMA。
/// 上限 400ms・下限 0 でクランプし、上限到達時は警告ログを出す。
/// </summary>
internal sealed class SeekLatencyCompensator
{
    public const double EmaAlpha = 0.25;
    public const double MaxCompensationSeconds = 0.4;

    /// <summary>
    /// T9: 補償を有効にする環境変数。既定は無効。`on`（大文字小文字不問）を指定したときだけ
    /// 有効になる（無効時は L は常に 0、測定も行わない）。
    /// </summary>
    public const string EnvironmentVariable = "TCS_SEEK_LATENCY_COMPENSATION";

    private readonly object gate = new();
    private readonly bool _enabled;
    private readonly Dictionary<Guid, double> _compensationByTrack = new();
    private readonly HashSet<Guid> _observedTracks = new();
    private Guid? _currentTrack;
    private bool _measurementArmed;
    private long _pendingDecisionQpc;
    private long _armedStartQpc;
    private int _lastReadyGeneration = int.MinValue;
    private long _lastReadySequence = long.MinValue;
    private int _generationAtArm;
    private long _sequenceAtArm;

    public SeekLatencyCompensator()
        : this(IsCompensationEnabled(Environment.GetEnvironmentVariable(EnvironmentVariable)))
    {
    }

    internal SeekLatencyCompensator(bool enabled)
    {
        _enabled = enabled;
        Log.Information(
            "Seek latency compensator: {State}（{Variable} は on のときだけ有効）",
            enabled ? "有効" : "無効", EnvironmentVariable);
    }

    /// <summary>環境変数値の解釈。null・空・on 以外は無効、"on"（大文字小文字不問）のときだけ有効。</summary>
    internal static bool IsCompensationEnabled(string? value)
        => value is not null && value.Trim().Equals("on", StringComparison.OrdinalIgnoreCase);

    /// <summary>現在のトラックの補償値（未学習は 0）。</summary>
    public double CompensationSeconds
    {
        get
        {
            if (!_enabled) return 0.0;
            lock (gate) return CompensationLocked(_currentTrack);
        }
    }

    /// <summary>指定トラックの補償値（未学習は 0）。</summary>
    public double CompensationForTrack(Guid? trackId)
    {
        if (!_enabled) return 0.0;
        lock (gate) return CompensationLocked(trackId);
    }

    /// <summary>補償・観測の対象トラックを切り替える（学習値は保持したまま引き当てる）。</summary>
    public void SelectTrack(Guid? trackId)
    {
        if (!_enabled) return;
        lock (gate) _currentTrack = trackId;
    }

    public bool IsMeasurementArmed
    {
        get
        {
            if (!_enabled) return false;
            lock (gate) return _measurementArmed;
        }
    }

    /// <summary>
    /// エンジンが Seek を決めた瞬間（seek.decide と同じ QPC）を記録する。まだ測定は始めない。
    /// シーク保留中に判定が再評価されても測定中の決定は動かさず、最新の決定を次の MarkSeekSent が採用する。
    /// </summary>
    public void MarkSeekDecision(long qpc)
    {
        if (!_enabled) return;
        lock (gate) _pendingDecisionQpc = qpc;
    }

    /// <summary>実際にシークを発行したときに測定を開始する（抑止・デバウンスされた決定で L を汚さない）。</summary>
    public void MarkSeekSent()
    {
        if (!_enabled) return;
        lock (gate)
        {
            ArmLocked(_pendingDecisionQpc != 0 ? _pendingDecisionQpc : Stopwatch.GetTimestamp());
            _pendingDecisionQpc = 0;
        }
    }

    /// <summary>
    /// トラック切替の LoadFile を発行したときに測定を開始する。issuedQpc は LoadFile 発行直前の
    /// QPC（0 のときは現在時刻）。着地は新しい世代の最初の Ready フレームで観測する。
    /// </summary>
    public void MarkLoadSent(long issuedQpc)
    {
        if (!_enabled) return;
        lock (gate) ArmLocked(issuedQpc != 0 ? issuedQpc : Stopwatch.GetTimestamp());
    }

    private void ArmLocked(long startQpc)
    {
        _armedStartQpc = startQpc;
        _measurementArmed = true;
        _generationAtArm = _lastReadyGeneration;
        _sequenceAtArm = _lastReadySequence;
    }

    /// <summary>
    /// GPU worker。source.acquire が Ready になったときの QPC・世代・ソース sequence を受け取る。
    /// arm 時点より新しい世代、または同一世代で新しい sequence の最初のフレームだけを着地とみなす
    /// （旧位置・旧ファイルのフレームを拾わない）。
    /// </summary>
    public void ObserveFrameReady(long qpc, int generation, long sourceSequence)
    {
        if (!_enabled) return;
        bool clampedAtUpper;
        double latencySeconds, compensationSeconds;
        Guid? track;
        lock (gate)
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

            if (!_measurementArmed) return;
            bool isNewFrame = generation > _generationAtArm
                || (generation == _generationAtArm && sourceSequence > _sequenceAtArm);
            if (!isNewFrame) return;

            _measurementArmed = false;
            latencySeconds = (qpc - _armedStartQpc) / (double)Stopwatch.Frequency;
            if (!double.IsFinite(latencySeconds) || latencySeconds < 0) return;

            track = _currentTrack;
            Guid key = track ?? Guid.Empty; // トラック未選択（テスト・初期化直後）は空 GUID に集約する
            bool firstSample = !_observedTracks.Contains(key);
            double previous = CompensationLocked(track);
            double updated = firstSample ? latencySeconds : previous + (EmaAlpha * (latencySeconds - previous));
            clampedAtUpper = updated > MaxCompensationSeconds;
            _compensationByTrack[key] = Math.Clamp(updated, 0.0, MaxCompensationSeconds);
            _observedTracks.Add(key);
            compensationSeconds = _compensationByTrack[key];
        }

        if (clampedAtUpper)
        {
            Log.Warning(
                "Seek latency compensator: 学習値が上限 {Max:F3}s に張り付きました latency={Latency:F1}ms track={Track}",
                MaxCompensationSeconds, latencySeconds * 1000.0, track);
        }
        Log.Information(
            "Seek latency compensator: latency={Latency:F1}ms compensation={Compensation:F1}ms track={Track}",
            latencySeconds * 1000.0, compensationSeconds * 1000.0, track);
    }

    /// <summary>補償後のターゲット。L=0 のとき現行と同一（ltc を 0〜duration にクランプ）。</summary>
    public double CompensateTarget(double ltcSeconds, double durationSeconds) =>
        Math.Clamp(ltcSeconds + CompensationSeconds, 0.0, durationSeconds);

    private double CompensationLocked(Guid? trackId) =>
        _compensationByTrack.TryGetValue(trackId ?? Guid.Empty, out double value) ? value : 0.0;
}
