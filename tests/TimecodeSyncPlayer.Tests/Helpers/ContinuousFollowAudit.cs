namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>L-1: 連続追従の 1 サンプル（WallSeconds は監査開始からの経過、PositionSeconds は画面の位置ラベル）。</summary>
internal readonly record struct FollowSample(double WallSeconds, double LtcSeconds, double PositionSeconds);

/// <summary>
/// L-1: アプリの `Playback perf` 行 1 本。AtSeconds はログ時刻（監査開始からの経過）、
/// SpanSeconds はその行が集計した窓の長さ、FrameUpdates はその窓の更新数。
/// </summary>
internal readonly record struct FollowPerfSegment(double AtSeconds, double SpanSeconds, int FrameUpdates)
{
    /// <summary>窓の中心。アプリ側の窓（2 秒）と L-1 の窓（可変）を対応付けるのに使う。</summary>
    public double MidpointSeconds => AtSeconds - SpanSeconds * 0.5;
}

/// <summary>
/// L-1: 1 窓の集計結果。Settling は追従直後の過渡として判定から除外した窓（集計と報告には残す）。
/// </summary>
internal readonly record struct FollowWindow(
    int Index, double StartSeconds, int FrameUpdates, double PositionAdvance, double MaxAbsError,
    bool Settling);

/// <summary>
/// L-1: 全窓の集計。Windows は Settling を含む全窓。判定に使う数値（Stall*、MaxAbsError、
/// MeanFrameUpdates、Worst*）は Settling を除いた窓だけから作る。
/// </summary>
internal sealed record ContinuousFollowSummary(
    IReadOnlyList<FollowWindow> Windows,
    double MeanFrameUpdates,
    int StallUpdateWindows,
    int StallAdvanceWindows,
    double MaxAbsError,
    FollowWindow? WorstUpdates,
    FollowWindow? WorstAdvance,
    FollowWindow? WorstError,
    int SettlingWindowCount,
    double SettlingMaxAbsError);

/// <summary>
/// L-1: 連続追従（Single・1 トラック内）の詰まり監査。UI に依存しない純関数で、
/// 「更新 0 の窓」「位置が進まない窓」「窓ごとの最大誤差」を数える。単体で固定する。
/// 追従開始直後の過渡（settlingSeconds）は判定から除外し、集計値としては報告する。
/// </summary>
internal static class ContinuousFollowAudit
{
    public static ContinuousFollowSummary Summarize(
        IReadOnlyList<FollowSample> samples,
        IReadOnlyList<FollowPerfSegment> perf,
        double durationSeconds,
        double windowSeconds,
        Func<double, double> expectedPosition,
        double settlingSeconds = 0.0,
        double minAdvanceRatio = 0.5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(expectedPosition);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));
        if (!double.IsFinite(settlingSeconds) || settlingSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(settlingSeconds));

        int windowCount = (int)Math.Floor(durationSeconds / windowSeconds);
        var windows = new List<FollowWindow>(windowCount);
        for (int index = 0; index < windowCount; index++)
        {
            double start = index * windowSeconds;
            double end = start + windowSeconds;

            int frameUpdates = 0;
            foreach (FollowPerfSegment segment in perf)
            {
                if (segment.MidpointSeconds >= start && segment.MidpointSeconds < end)
                    frameUpdates += segment.FrameUpdates;
            }

            double firstPosition = double.NaN;
            double lastPosition = double.NaN;
            double maxError = 0.0;
            bool hasPosition = false;
            foreach (FollowSample sample in samples)
            {
                if (sample.WallSeconds < start || sample.WallSeconds >= end)
                    continue;
                if (!double.IsFinite(sample.PositionSeconds))
                    continue;
                if (!hasPosition)
                {
                    firstPosition = sample.PositionSeconds;
                    hasPosition = true;
                }
                lastPosition = sample.PositionSeconds;
                if (double.IsFinite(sample.LtcSeconds))
                {
                    double error = Math.Abs(sample.PositionSeconds - expectedPosition(sample.LtcSeconds));
                    if (double.IsFinite(error))
                        maxError = Math.Max(maxError, error);
                }
            }

            double advance = hasPosition ? lastPosition - firstPosition : 0.0;
            windows.Add(new FollowWindow(index, start, frameUpdates, advance, maxError, start < settlingSeconds));
        }

        List<FollowWindow> audited = windows.Where(window => !window.Settling).ToList();
        List<FollowWindow> settling = windows.Where(window => window.Settling).ToList();

        int stallUpdates = 0;
        int stallAdvance = 0;
        foreach (FollowWindow window in audited)
        {
            if (window.FrameUpdates == 0)
                stallUpdates++;
            if (window.PositionAdvance < windowSeconds * minAdvanceRatio)
                stallAdvance++;
        }

        return new ContinuousFollowSummary(
            windows,
            audited.Count == 0 ? 0.0 : audited.Average(window => (double)window.FrameUpdates),
            stallUpdates,
            stallAdvance,
            audited.Count == 0 ? 0.0 : audited.Max(window => window.MaxAbsError),
            audited.Count == 0 ? null : audited.MinBy(window => window.FrameUpdates),
            audited.Count == 0 ? null : audited.MinBy(window => window.PositionAdvance),
            audited.Count == 0 ? null : audited.MaxBy(window => window.MaxAbsError),
            settling.Count,
            settling.Count == 0 ? 0.0 : settling.Max(window => window.MaxAbsError));
    }
}
