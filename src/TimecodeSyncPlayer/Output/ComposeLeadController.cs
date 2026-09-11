namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 合成 lead の動的更新。合成時間（compose.start → GPU 完了）の実測 p99 に余裕 1ms を足した値を
/// 1 秒ごとに再計算する（下限 1ms、上限 8ms）。
/// ヒステリシス: 上げる方向は即時。下げる方向は 5 秒連続で p99+1ms が現行 lead を 0.5ms 以上
/// 下回った場合だけ、0.5ms 刻みで下げる（往復による表示落ちを避ける）。
/// 実際の位相は compose.align の slew（0.5ms/tick）で滑らかに反映される。
/// </summary>
internal sealed class ComposeLeadController(long frequency, double initialLeadMs)
{
    public const double MinimumLeadMs = 1.0;
    public const double MaximumLeadMs = 8.0;
    public const double SafetyMarginMs = 1.0;
    public const double DecreaseThresholdMs = 0.5;
    public const double DecreaseStepMs = 0.5;
    public const int DecreaseConfirmations = 5;
    public const double WarmupSeconds = 3.0;
    private const int MinimumSamples = 10;
    private const double ChangeEpsilonMs = 0.05;

    private readonly List<long> samples = new();
    private long windowStartQpc;
    private long firstSampleQpc;
    private bool warmupStarted;
    private bool windowStarted;
    private int belowWindows;
    public double CurrentLeadMs { get; private set; } = Math.Clamp(initialLeadMs, MinimumLeadMs, MaximumLeadMs);

    /// <summary>合成時間のサンプルを追加し、1 秒経過していれば次 lead を返す（変更なしなら false）。</summary>
    public bool Add(long durationTicks, long nowQpc, out double newLeadMs)
    {
        newLeadMs = CurrentLeadMs;
        if (durationTicks < 0 || frequency <= 0) return false;
        // 起動直後はデコーダ起動・シェーダ初期化で合成が長く、lead を過大に学習する。
        // 最初の 3 秒は標本に入れない（段階 3）。
        if (!warmupStarted) { warmupStarted = true; firstSampleQpc = nowQpc; return false; }
        if (nowQpc - firstSampleQpc < (long)Math.Round(WarmupSeconds * frequency)) return false;
        samples.Add(durationTicks);
        if (!windowStarted) { windowStarted = true; windowStartQpc = nowQpc; return false; }
        if (nowQpc - windowStartQpc < frequency) return false;
        windowStartQpc = nowQpc;
        if (samples.Count < MinimumSamples) { samples.Clear(); return false; }

        var sorted = samples.Order().ToArray();
        int index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1);
        double p99Ms = sorted[index] * 1000.0 / frequency;
        samples.Clear();
        double desired = Math.Clamp(p99Ms + SafetyMarginMs, MinimumLeadMs, MaximumLeadMs);

        if (desired > CurrentLeadMs + ChangeEpsilonMs)
        {
            // 上げる方向は即時。
            belowWindows = 0;
            CurrentLeadMs = desired;
            newLeadMs = desired;
            return true;
        }
        if (desired <= CurrentLeadMs - DecreaseThresholdMs)
        {
            // 下げる方向は 5 秒連続でのみ、0.5ms 刻み。
            if (++belowWindows < DecreaseConfirmations) return false;
            belowWindows = 0;
            double decreased = Math.Max(MinimumLeadMs, CurrentLeadMs - DecreaseStepMs);
            if (Math.Abs(decreased - CurrentLeadMs) < ChangeEpsilonMs) return false;
            CurrentLeadMs = decreased;
            newLeadMs = decreased;
            return true;
        }
        belowWindows = 0;
        return false;
    }
}
