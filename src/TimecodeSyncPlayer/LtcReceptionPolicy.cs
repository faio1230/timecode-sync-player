using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

/// <summary>v0.6.6 R-13: LTC の受信の表示の 3 段。</summary>
internal enum LtcReceptionState
{
    /// <summary>ピークが <see cref="LtcReceptionPolicy.SilenceThresholdDbfs"/> 未満で、LTC も読めていない。</summary>
    Silent,

    /// <summary>音はあるが、直近 1 秒に読めた LTC のフレームが fps の半分に届かない。</summary>
    SignalWithoutLtc,

    /// <summary>直近 1 秒に読めた LTC のフレームが fps の半分以上。</summary>
    Receiving,
}

/// <summary>v0.6.6 R-13: 受信の表示 1 回分（文字・色・メーターの値）。</summary>
internal sealed record LtcReceptionDisplay(
    LtcReceptionState State,
    string Text,
    string Foreground,
    double MeterPercent);

/// <summary>
/// v0.6.6 R-13: LTC の入力のメーターと受信の表示の判定。表示だけに使い、LTC のデコード・同期の規則・
/// ゲート・定数には触らない（同期の側はこの判定を読まない）。
/// </summary>
internal static class LtcReceptionPolicy
{
    /// <summary>これ未満のピークを「無音」とする（ちょうど -60 dBFS は無音にしない）。</summary>
    public const double SilenceThresholdDbfs = -60.0;

    /// <summary>直近 1 秒に読めたフレームが fps × この比 以上なら「LTC 受信中」。</summary>
    public const double ReceivingFrameRatio = 0.5;

    /// <summary>Auto で fps が未確定のときにみなす fps。</summary>
    public const double UnresolvedAutoFps = 24.0;

    /// <summary>メーターの左端（右端は 0 dBFS）。</summary>
    public const double MeterFloorDbfs = -60.0;

    public const string ReceivingText = "LTC 受信中";
    public const string SignalWithoutLtcText = "信号あり・LTC なし";
    public const string SilentText = "無音";

    public const string ReceivingForeground = "#56D364";
    public const string SignalWithoutLtcForeground = "#D7A24B";
    public const string SilentForeground = "#888888";

    /// <summary>
    /// 判定に使う fps。固定ならその値、Auto なら確定した fps（<paramref name="resolvedFps"/>）、
    /// 未確定（0 以下・数でない）なら <see cref="UnresolvedAutoFps"/>。
    /// </summary>
    public static double ReceptionFps(TimecodeFpsMode mode, double resolvedFps)
    {
        if (mode != TimecodeFpsMode.Auto)
            return mode.ToFps();
        return double.IsFinite(resolvedFps) && resolvedFps > 0 ? resolvedFps : UnresolvedAutoFps;
    }

    public static LtcReceptionState Classify(LtcInputLevel level, double fps)
    {
        if (level.DecodedFramesLastSecond >= fps * ReceivingFrameRatio)
            return LtcReceptionState.Receiving;
        return level.PeakDbfs < SilenceThresholdDbfs
            ? LtcReceptionState.Silent
            : LtcReceptionState.SignalWithoutLtc;
    }

    /// <summary>ピークをメーターの 0〜100 にする（-60 dBFS 以下は 0、0 dBFS 以上は 100）。</summary>
    public static double MeterPercent(double peakDbfs)
    {
        if (double.IsNaN(peakDbfs))
            return 0;
        double ratio = (peakDbfs - MeterFloorDbfs) / (0.0 - MeterFloorDbfs);
        return Math.Clamp(ratio, 0.0, 1.0) * 100.0;
    }

    public static LtcReceptionDisplay Describe(LtcInputLevel level, double fps)
    {
        LtcReceptionState state = Classify(level, fps);
        return Describe(state, level.PeakDbfs);
    }

    /// <summary>LTC を始めた直後（まだ知らせが来ていない）の表示。</summary>
    public static LtcReceptionDisplay Initial { get; } =
        Describe(LtcReceptionState.Silent, LtcInputLevelMeter.FloorDbfs);

    private static LtcReceptionDisplay Describe(LtcReceptionState state, double peakDbfs) =>
        state switch
        {
            LtcReceptionState.Receiving =>
                new(state, ReceivingText, ReceivingForeground, MeterPercent(peakDbfs)),
            LtcReceptionState.SignalWithoutLtc =>
                new(state, SignalWithoutLtcText, SignalWithoutLtcForeground, MeterPercent(peakDbfs)),
            _ => new(state, SilentText, SilentForeground, MeterPercent(peakDbfs)),
        };
}

/// <summary>
/// v0.6.6 R-13: 最新の値だけを UI へ渡す受け箱。音声のスレッドが <see cref="Offer"/> で置き、true が返ったときだけ
/// UI へ 1 回投げる。UI は <see cref="Take"/> で最新を取る。UI が遅れても投げた分が積み上がらない（ロックなし）。
/// </summary>
internal sealed class LatestValueMailbox<T> where T : class
{
    private T? _latest;
    private int _posted;

    /// <summary>値を置く。UI へ投げる必要があるとき（まだ投げていないとき）だけ true。</summary>
    public bool Offer(T value)
    {
        Volatile.Write(ref _latest, value);
        return Interlocked.CompareExchange(ref _posted, 1, 0) == 0;
    }

    /// <summary>最新の値を取り出す（無ければ null）。取り出した後に置かれた値は、次の Offer が true を返す。</summary>
    public T? Take()
    {
        Interlocked.Exchange(ref _posted, 0);
        return Interlocked.Exchange(ref _latest, null);
    }
}
