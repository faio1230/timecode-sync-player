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

/// <summary>L-1: 1 窓の集計結果。</summary>
internal readonly record struct FollowWindow(
    int Index, double StartSeconds, int FrameUpdates, double PositionAdvance, double MaxAbsError);

/// <summary>L-1: 全窓の集計。</summary>
internal sealed record ContinuousFollowSummary(
    IReadOnlyList<FollowWindow> Windows,
    double MeanFrameUpdates,
    int StallUpdateWindows,
    int StallAdvanceWindows,
    double MaxAbsError,
    FollowWindow? WorstUpdates,
    FollowWindow? WorstAdvance,
    FollowWindow? WorstError);

/// <summary>
/// L-1: 連続追従（Single・1 トラック内）の詰まり監査。UI に依存しない純関数で、
/// 「更新 0 の窓」「位置が進まない窓」「窓ごとの最大誤差」を数える。単体で固定する。
/// </summary>
internal static class ContinuousFollowAudit
{
    public static ContinuousFollowSummary Summarize(
        IReadOnlyList<FollowSample> samples,
        IReadOnlyList<FollowPerfSegment> perf,
        double durationSeconds,
        double windowSeconds,
        Func<double, double> expectedPosition,
        double minAdvanceRatio = 0.5)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(expectedPosition);
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));

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
            windows.Add(new FollowWindow(index, start, frameUpdates, advance, maxError));
        }

        int stallUpdates = 0;
        int stallAdvance = 0;
        foreach (FollowWindow window in windows)
        {
            if (window.FrameUpdates == 0)
                stallUpdates++;
            if (window.PositionAdvance < windowSeconds * minAdvanceRatio)
                stallAdvance++;
        }

        return new ContinuousFollowSummary(
            windows,
            windows.Count == 0 ? 0.0 : windows.Average(window => (double)window.FrameUpdates),
            stallUpdates,
            stallAdvance,
            windows.Count == 0 ? 0.0 : windows.Max(window => window.MaxAbsError),
            windows.Count == 0 ? null : windows.MinBy(window => window.FrameUpdates),
            windows.Count == 0 ? null : windows.MinBy(window => window.PositionAdvance),
            windows.Count == 0 ? null : windows.MaxBy(window => window.MaxAbsError));
    }
}
