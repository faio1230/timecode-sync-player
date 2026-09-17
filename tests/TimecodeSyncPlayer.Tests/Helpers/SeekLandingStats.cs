namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>L-1: 補正シークの発行から着地（pending セトル／タイムアウト）までの集計。</summary>
internal readonly record struct SeekLandingSummary(int Count, double MedianSeconds, double MaxSeconds);

/// <summary>
/// L-1: シーク着地時間の集計（純関数）。非有限・負の値は捨てる。
/// </summary>
internal static class SeekLandingStats
{
    public static SeekLandingSummary Summarize(IReadOnlyList<double> durations)
    {
        ArgumentNullException.ThrowIfNull(durations);
        double[] finite = durations
            .Where(duration => double.IsFinite(duration) && duration >= 0)
            .Order()
            .ToArray();
        if (finite.Length == 0)
            return new SeekLandingSummary(0, 0.0, 0.0);

        double median = finite.Length % 2 == 1
            ? finite[finite.Length / 2]
            : (finite[finite.Length / 2 - 1] + finite[finite.Length / 2]) / 2.0;
        return new SeekLandingSummary(finite.Length, median, finite[^1]);
    }
}
