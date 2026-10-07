namespace TimecodeSyncPlayer;

/// <summary>
/// v0.6.6 R-4: 1 フレーム送り・戻しの目標の計算。UI に依存しない純粋な部品。
/// 「今のフレーム」は配信フレームの PTS から求め、目標は「今のフレームの頭 ± N フレーム + 半フレーム」に置く
/// （フレームの中に置き、格子の線ちょうどの丸めで隣のフレームに落ちるのを避ける。v0.6.4 の棚卸し #9・#28 の型）。
/// </summary>
internal static class FrameStepMath
{
    /// <summary>
    /// PTS を何フレーム目とみなすかの許容（フレームの割合）。配信の PTS は ns に丸めた格子の線の上にあり、
    /// 秒×fps が整数のわずかに下に出ることがある（例 29.97 の 1001/30000 秒）。その分だけ上へ寄せて床を取る。
    /// </summary>
    internal const double IndexTolerance = 1e-3;

    /// <summary>秒の位置が何フレーム目か（0 始まり、負は 0）。</summary>
    public static long FrameIndexAt(double seconds, double fps)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return 0;
        return Math.Max(0L, (long)Math.Floor((seconds * fps) + IndexTolerance));
    }

    /// <summary>フレームの中（頭 + 半フレーム）の秒。</summary>
    public static double TargetSeconds(long frameIndex, double fps) => (frameIndex + 0.5) / fps;

    /// <summary>最後のフレームの番号。長さが分からなければ上限なし。</summary>
    public static long LastFrameIndex(double durationSeconds, double fps)
    {
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0) return long.MaxValue;
        long frames = (long)Math.Ceiling((durationSeconds * fps) - IndexTolerance);
        return Math.Max(0L, frames - 1);
    }

    /// <summary>0 から最後のフレームまでに収める。</summary>
    public static long Clamp(long frameIndex, double durationSeconds, double fps) =>
        Math.Clamp(frameIndex, 0L, LastFrameIndex(durationSeconds, fps));
}

/// <summary>最後に届いた配信フレーム（PTS と世代）。</summary>
internal readonly record struct FrameStepDelivered(double Seconds, ulong Generation);

internal enum FrameStepOutcomeKind
{
    /// <summary>着地を待つものが無い・まだ着地していない（既定値）。</summary>
    None = 0,
    /// <summary>シークを送った。</summary>
    Sent,
    /// <summary>飛行中なので押した数を目標に積んだ。</summary>
    Queued,
    /// <summary>配信フレームがまだ無い。何もしない。</summary>
    NoDelivery,
    /// <summary>素材の fps が分からない。何もしない。</summary>
    NoFps,
    /// <summary>端（先頭で戻す・末尾で送る）で動かない。何もしない。</summary>
    AtEdge,
    /// <summary>シークが失敗した。</summary>
    SendFailed,
    /// <summary>着地した。積んだ分が無い（または端で動かない）ので、次は送らない。</summary>
    Landed,
}

internal readonly record struct FrameStepOutcome(
    FrameStepOutcomeKind Kind,
    int Steps,
    long BaseFrame,
    long TargetFrame,
    double TargetSeconds,
    double Fps,
    double DeliveredSeconds);

/// <summary>
/// v0.6.6 R-4: 1 フレーム送り・戻しの外への作用。経路は今の相対シーク（SeekRelative）と同じものを MainWindow が渡す。
/// </summary>
internal sealed record FrameStepEffects(
    Func<bool> IsPaused,
    Action PauseAsUser,
    Func<FrameStepDelivered?> ReadDelivered,
    Func<double> Fps,
    Func<double> Duration,
    Func<double, bool> Seek,
    Func<ulong?> ReadCurrentGeneration);

/// <summary>
/// v0.6.6 R-4: 1 フレーム送り・戻し。飛行中のシークは 1 本だけで、飛行中に押した数は目標に積み上げ
/// （10 回押せば 10 フレーム）、着地の後に 1 本送る。着地の判定はスクラブ（<see cref="ScrubSeekThrottle.IsLanded"/>）と
/// 同じ世代の比べ。再生中に押したら、先に再生/一時停止ボタンと同じ経路で一時停止する。
/// 同期の規則・ゲート・定数には触れない。
/// </summary>
internal sealed class FrameStepper(FrameStepEffects effects)
{
    private readonly FrameStepEffects _effects = effects;
    private bool _inFlight;
    private bool _generationKnown;
    private ulong _seekGeneration;
    private int _pendingSteps;
    private long _lastTargetFrame;
    private double _fps;

    public bool InFlight => _inFlight;
    public bool GenerationKnown => _inFlight && _generationKnown;
    public ulong SeekGeneration => _seekGeneration;
    public int PendingSteps => _pendingSteps;
    public long LastTargetFrame => _lastTargetFrame;

    /// <summary>ボタンを押した（steps は +1 で送る、-1 で戻す）。</summary>
    public FrameStepOutcome Press(int steps)
    {
        if (_inFlight)
        {
            _pendingSteps += steps;
            return new(FrameStepOutcomeKind.Queued, _pendingSteps, _lastTargetFrame, _lastTargetFrame, double.NaN, _fps, double.NaN);
        }

        double fps = _effects.Fps();
        if (!double.IsFinite(fps) || fps <= 0)
            return new(FrameStepOutcomeKind.NoFps, steps, -1, -1, double.NaN, fps, double.NaN);

        if (_effects.ReadDelivered() is not FrameStepDelivered delivered)
            return new(FrameStepOutcomeKind.NoDelivery, steps, -1, -1, double.NaN, fps, double.NaN);

        if (!_effects.IsPaused())
        {
            _effects.PauseAsUser();
            // 止めた後の配信フレームを今のフレームにする（読めなければ止める前の 1 枚）。
            if (_effects.ReadDelivered() is FrameStepDelivered afterPause)
                delivered = afterPause;
        }

        long baseFrame = FrameStepMath.FrameIndexAt(delivered.Seconds, fps);
        return SendFrom(baseFrame, steps, fps, delivered.Seconds);
    }

    /// <summary>描画の更新ごとに呼ぶ。配信の世代が送ったシークの世代に追いついたら着地とし、積んだ分があれば 1 本送る。</summary>
    public FrameStepOutcome ObserveLanding(ulong currentGeneration, ulong deliveredGeneration, double deliveredSeconds)
    {
        if (!_inFlight) return default;
        if (!_generationKnown && currentGeneration != 0)
        {
            _seekGeneration = currentGeneration;
            _generationKnown = true;
        }
        if (!_generationKnown || !ScrubSeekThrottle.IsLanded(deliveredGeneration, _seekGeneration))
            return default;
        return Landed(deliveredSeconds);
    }

    /// <summary>EOF（新しいフレームが来ない）。着地として扱う。</summary>
    public FrameStepOutcome Ended() => _inFlight ? Landed(double.NaN) : default;

    private FrameStepOutcome Landed(double deliveredSeconds)
    {
        _inFlight = false;
        _generationKnown = false;
        int pending = _pendingSteps;
        _pendingSteps = 0;
        if (pending == 0)
            return new(FrameStepOutcomeKind.Landed, 0, _lastTargetFrame, _lastTargetFrame, double.NaN, _fps, deliveredSeconds);
        FrameStepOutcome next = SendFrom(_lastTargetFrame, pending, _fps, deliveredSeconds);
        return next.Kind == FrameStepOutcomeKind.AtEdge
            ? next with { Kind = FrameStepOutcomeKind.Landed }
            : next;
    }

    private FrameStepOutcome SendFrom(long baseFrame, int steps, double fps, double deliveredSeconds)
    {
        long target = FrameStepMath.Clamp(baseFrame + steps, _effects.Duration(), fps);
        if (target == baseFrame)
            return new(FrameStepOutcomeKind.AtEdge, steps, baseFrame, target, double.NaN, fps, deliveredSeconds);

        double seconds = FrameStepMath.TargetSeconds(target, fps);
        if (!_effects.Seek(seconds))
            return new(FrameStepOutcomeKind.SendFailed, steps, baseFrame, target, seconds, fps, deliveredSeconds);

        _inFlight = true;
        _generationKnown = false;
        _lastTargetFrame = target;
        _fps = fps;
        // シークの世代は送った後の照会の現在世代（スクラブと同じ）。取れなければ描画の更新で最初に取れた照会で覚える。
        if (_effects.ReadCurrentGeneration() is ulong generation && generation != 0)
        {
            _seekGeneration = generation;
            _generationKnown = true;
        }
        return new(FrameStepOutcomeKind.Sent, steps, baseFrame, target, seconds, fps, deliveredSeconds);
    }
}
