namespace TimecodeSyncPlayer;

/// <summary>2 秒ごとの再生の数え上げ（<see cref="PlaybackPerformanceStats"/> の窓）を 1 つ評価した結果。</summary>
internal enum DecodeWindowVerdict
{
    /// <summary>判定しない（シーク・一時停止・読み込みをまたいだ、終端付近、fps 不明など）。</summary>
    Skipped,
    /// <summary>届いたフレームが足りている。</summary>
    Ok,
    /// <summary>届いたフレームが期待より明らかに少ない＝復号が追いついていない。</summary>
    Behind,
}

/// <summary>
/// 0.4.7: 「デコードが追いついていない」表示。<b>表示するだけで、同期の制御は一切変えない。</b>
///
/// 背景（検証機の実測、2026-09-19）: 実素材で、2 秒ほど復号が追いつかなくなる（2 秒で 120 枚
/// 届くはずが 74 枚、0.6 倍速）ことがあり、その後の取り戻しで同期が 0.3〜0.7 秒ずれる。
/// VP9 4K60 では 2 秒の窓の約 18%、H.264 4K60・103 Mbps では約 0.9%、推奨どおりの素材では 0%。
/// 利用者の判断で「まず表示を付けて、現場で実際に出るかを見てから直すか決める」。
///
/// 判定: 窓の中で届いたフレーム数が、期待値（素材の fps × 経過秒 × 指示した速度の平均）の
/// <see cref="BehindRatio"/> 未満なら Behind。次の窓は判定しない（Skipped）:
/// <list type="bullet">
/// <item>シーク・一時停止・読み込みなどをまたいだ窓と、その後の <see cref="SettleWindows"/> 窓
///   （<see cref="PlaybackActivityLedger"/> で分かる）。シークは着地と再開の遅れで数秒フレームが
///   減る（実測で最大 2.8 秒）。それを復号の遅れと取り違えない</item>
/// <item>止まっている、ギャップの中（フレームが来ないのが正常）</item>
/// <item>素材の終端付近（フレームが尽きるのは正常）、fps が分からない、経過が短すぎる</item>
/// </list>
/// </summary>
internal sealed class DecodeHealthMonitor
{
    /// <summary>
    /// 期待値に対してこの割合を下回ったら Behind。検証機の「落ち込み窓」の定義（0.95 倍速未満）より
    /// 緩めにしてある。表示は操作者の目に入るので、誤って出るほうが困る。実測の落ち込みは
    /// 0.60〜0.86 で、失敗した回の最小は 0.72〜0.78 だったので、0.9 でそれらは拾える。
    /// </summary>
    internal const double BehindRatio = 0.90;

    /// <summary>最後に Behind だった窓から、表示を残しておく時間。</summary>
    internal static readonly TimeSpan HoldAfterLast = TimeSpan.FromSeconds(10);

    /// <summary>終端からこの秒数以内は判定しない（フレームが尽きるのは正常）。</summary>
    internal const double EndMarginSeconds = 3.0;

    /// <summary>乱れ（シークなど）をまたいだ窓のあと、判定しない窓の数（2 秒 × 2）。</summary>
    internal const int SettleWindows = 2;

    private const double MinimumElapsedSeconds = 1.0;

    private int _settleRemaining;
    // いまの perf の窓が始まった時点の基準。窓の中の乱れと、指示した速度の平均はここからの差で見る。
    private long? _windowStartDisturbances;
    private double _windowStartRateIntegral;
    private DateTime _lastBehindAt = DateTime.MinValue;

    /// <summary>このセッション（読み込み以後）で Behind だった窓の数。</summary>
    public int BehindCount { get; private set; }

    /// <summary>直近の Behind の窓で、届いた枚数と期待した枚数。</summary>
    public (int Delivered, int Expected) LastBehind { get; private set; }

    /// <summary>
    /// perf の窓が snapshot 無しで作り直されたとき（位置が戻ったとき）に呼ぶ。窓の基準
    /// （乱れの数・速度の積分）をここで取り直す。snapshot と同時に始まる窓は <see cref="Observe"/> が取り直す。
    ///
    /// 基準を窓に合わせないと、作り直しで捨てた部分の積分まで窓の期待値に入る。検証機（M3 ×10、
    /// 047cand1）で表示 11 件のうち 10 件がこれによる誤検知だった（前回の perf 行から 5.57 秒の積分を
    /// 2.01 秒の窓で割り、「123 / 334」）。
    ///
    /// 捨てた部分に乱れがあったら（シークで位置が戻って作り直された場合など）、着地の遅れはこれからの
    /// 窓に出るので、ここから <see cref="SettleWindows"/> 窓は判定しない。
    /// </summary>
    public void BeginWindow(long disturbances, double rateIntegralSeconds)
    {
        if (_windowStartDisturbances is long previous && disturbances != previous)
            _settleRemaining = SettleWindows;
        _windowStartDisturbances = disturbances;
        _windowStartRateIntegral = rateIntegralSeconds;
    }

    /// <summary>新しい素材を読み込んだとき。数え直す。</summary>
    public void Reset()
    {
        BehindCount = 0;
        _lastBehindAt = DateTime.MinValue;
        LastBehind = default;
    }

    /// <summary>
    /// 1 つの窓を評価する。<paramref name="disturbances"/> と <paramref name="rateIntegralSeconds"/> は
    /// <see cref="PlaybackActivityLedger"/> の窓の終わりの値（窓の始まりの基準との差を内部で取る）。
    /// perf の窓は snapshot と同時に切り替わるので、ここでの値が次の窓の基準になる。
    /// </summary>
    public DecodeWindowVerdict Observe(int deliveredFrames, double elapsedSeconds, double fps,
        long disturbances, double rateIntegralSeconds,
        double positionSeconds, double durationSeconds, bool pausedOrInGap, DateTime now)
    {
        long? startDisturbances = _windowStartDisturbances;
        double startRateIntegral = _windowStartRateIntegral;
        _windowStartDisturbances = disturbances;
        _windowStartRateIntegral = rateIntegralSeconds;

        if (startDisturbances is null || disturbances != startDisturbances)
        {
            _settleRemaining = SettleWindows;
            return DecodeWindowVerdict.Skipped;
        }
        if (_settleRemaining > 0)
        {
            _settleRemaining--;
            return DecodeWindowVerdict.Skipped;
        }
        if (pausedOrInGap) return DecodeWindowVerdict.Skipped;
        if (!double.IsFinite(fps) || fps <= 0) return DecodeWindowVerdict.Skipped;
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < MinimumElapsedSeconds) return DecodeWindowVerdict.Skipped;
        if (double.IsFinite(durationSeconds) && durationSeconds > 0
            && positionSeconds >= durationSeconds - EndMarginSeconds)
            return DecodeWindowVerdict.Skipped;

        double averageRate = (rateIntegralSeconds - startRateIntegral) / elapsedSeconds;
        if (!double.IsFinite(averageRate) || averageRate <= 0) averageRate = 1.0;
        double expected = fps * elapsedSeconds * averageRate;
        if (deliveredFrames >= expected * BehindRatio) return DecodeWindowVerdict.Ok;

        BehindCount++;
        _lastBehindAt = now;
        LastBehind = (deliveredFrames, (int)Math.Round(expected));
        return DecodeWindowVerdict.Behind;
    }

    /// <summary>ステータス行の文言。表示しないときは空。</summary>
    public string StatusText(DateTime now)
    {
        if (_lastBehindAt == DateTime.MinValue || now - _lastBehindAt > HoldAfterLast) return string.Empty;
        (int delivered, int expected) = LastBehind;
        return $"⚠ デコードが追いついていません（2 秒で {delivered} / {expected} 枚、この素材で {BehindCount} 回）";
    }
}
