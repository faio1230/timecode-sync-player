namespace TimecodeSyncPlayer.Output;

/// <summary>
/// 合成 lead の動的更新。合成時間（compose.start → GPU 完了）の実測 p99 に余裕 1ms を足した値を
/// 1 秒ごとに再計算する（下限 1ms、上限 8ms）。fix lead 3ms より速い構成では締め、遅い構成では広げる。
/// </summary>
internal sealed class ComposeLeadController(long frequency, double initialLeadMs)
{
    public const double MinimumLeadMs = 1.0;
    public const double MaximumLeadMs = 8.0;
    public const double SafetyMarginMs = 1.0;
    private const int MinimumSamples = 10;

    private readonly List<long> samples = new();
    private long windowStartQpc;
    private bool windowStarted;
    public double CurrentLeadMs { get; private set; } = Math.Clamp(initialLeadMs, MinimumLeadMs, MaximumLeadMs);

    /// <summary>合成時間のサンプルを追加し、1 秒経過していれば次 lead を返す（変更なしなら false）。</summary>
    public bool Add(long durationTicks, long nowQpc, out double newLeadMs)
    {
        newLeadMs = CurrentLeadMs;
        if (durationTicks < 0 || frequency <= 0) return false;
        samples.Add(durationTicks);
        if (!windowStarted) { windowStarted = true; windowStartQpc = nowQpc; return false; }
        if (nowQpc - windowStartQpc < frequency) return false;
        windowStartQpc = nowQpc;
        if (samples.Count < MinimumSamples) { samples.Clear(); return false; }

        var sorted = samples.Order().ToArray();
        int index = Math.Min(sorted.Length - 1, (int)Math.Ceiling(sorted.Length * 0.99) - 1);
        double p99Ms = sorted[index] * 1000.0 / frequency;
        double next = Math.Clamp(p99Ms + SafetyMarginMs, MinimumLeadMs, MaximumLeadMs);
        samples.Clear();
        if (Math.Abs(next - CurrentLeadMs) < 0.05) return false;
        CurrentLeadMs = next;
        newLeadMs = next;
        return true;
    }
}
