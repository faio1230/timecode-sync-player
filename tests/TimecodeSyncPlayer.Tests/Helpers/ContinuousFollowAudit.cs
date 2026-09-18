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
/// LtcAdvance と Samples は、位置が進まない窓の切り分け用。位置と LTC はどちらも画面の
/// ラベルから読むので、両方が同時に止まっていれば表示側、LTC だけ進んでいれば再生側、
/// と読み分けられる。IntervalSeconds は進みを測った実測区間（前の窓の最後のサンプルから
/// この窓の最後のサンプルまで、最大 1.5 窓）。Sparse はその区間が窓長の半分未満で、
/// 進みの判定に使えない窓（サンプル 0 の窓も含む。判定から外し、報告には残す）。
/// </summary>
internal readonly record struct FollowWindow(
    int Index, double StartSeconds, int FrameUpdates, double PositionAdvance, double MaxAbsError,
    bool Settling, double LtcAdvance, int Samples, double IntervalSeconds, bool Sparse);

/// <summary>
/// L-1: 全窓の集計。Windows は Settling を含む全窓。判定に使う数値（Stall*、MaxAbsError、
/// MeanFrameUpdates、Worst*）は Settling を除いた窓だけから作る。StallAdvanceWindows は
/// 「LTC が窓長の半分以上進んだのに位置がその半分も進まなかった」再生側の停滞だけを数え、
/// 表示側が同時に止まった窓（LTC も止まる）や Sparse な窓は数えない。
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
    double SettlingMaxAbsError,
    int SparseWindowCount);

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
        double sparseThreshold = windowSeconds * 0.5;
        double intervalCap = windowSeconds * 1.5;
        var windows = new List<FollowWindow>(windowCount);
        // 窓の進みは「前の窓の最後の値」を起点にする。画面ラベルの読み取りは 1 サンプルあたり
        // 100ms 以上かかることがあり、2 秒窓に 1〜3 サンプルしか入らない場合がある。窓の中だけで
        // 差を取ると、再生が正常でも進みが 0 に見えてしまう。
        double carryPosition = double.NaN;
        double carryLtc = double.NaN;
        double carryWall = double.NaN;
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
            double firstWall = double.NaN;
            double lastWall = double.NaN;
            double firstLtc = double.NaN;
            double lastLtc = double.NaN;
            double maxError = 0.0;
            bool hasPosition = false;
            bool hasLtc = false;
            int sampleCount = 0;
            foreach (FollowSample sample in samples)
            {
                if (sample.WallSeconds < start || sample.WallSeconds >= end)
                    continue;
                sampleCount++;
                if (double.IsFinite(sample.LtcSeconds))
                {
                    if (!hasLtc)
                    {
                        firstLtc = double.IsFinite(carryLtc) ? carryLtc : sample.LtcSeconds;
                        hasLtc = true;
                    }
                    lastLtc = sample.LtcSeconds;
                }
                if (!double.IsFinite(sample.PositionSeconds))
                    continue;
                if (!hasPosition)
                {
                    firstPosition = double.IsFinite(carryPosition) ? carryPosition : sample.PositionSeconds;
                    firstWall = double.IsFinite(carryWall) ? carryWall : sample.WallSeconds;
                    hasPosition = true;
                }
                lastPosition = sample.PositionSeconds;
                lastWall = sample.WallSeconds;
                if (double.IsFinite(sample.LtcSeconds))
                {
                    double error = Math.Abs(sample.PositionSeconds - expectedPosition(sample.LtcSeconds));
                    if (double.IsFinite(error))
                        maxError = Math.Max(maxError, error);
                }
            }

            double advance = hasPosition ? lastPosition - firstPosition : 0.0;
            double ltcAdvance = hasLtc ? lastLtc - firstLtc : 0.0;
            // 実測区間（前の窓の最後のサンプルからこの窓の最後のサンプルまで）。窓をまたいで
            // 空窓が続いた場合は最大 1.5 窓で頭打ちにし、帰属をこの窓に寄せる。
            double intervalSeconds = hasPosition
                ? Math.Min(Math.Max(0.0, lastWall - firstWall), intervalCap)
                : 0.0;
            bool sparse = !hasPosition || intervalSeconds < sparseThreshold;
            if (hasPosition)
            {
                carryPosition = lastPosition;
                carryWall = lastWall;
            }
            if (hasLtc)
                carryLtc = lastLtc;
            windows.Add(new FollowWindow(index, start, frameUpdates, advance, maxError,
                start < settlingSeconds, ltcAdvance, sampleCount, intervalSeconds, sparse));
        }

        List<FollowWindow> audited = windows.Where(window => !window.Settling).ToList();
        List<FollowWindow> settling = windows.Where(window => window.Settling).ToList();
        List<FollowWindow> measurable = audited.Where(window => !window.Sparse).ToList();

        int stallUpdates = 0;
        int stallAdvance = 0;
        foreach (FollowWindow window in audited)
        {
            if (window.FrameUpdates == 0)
                stallUpdates++;
        }
        // 案 3: LTC は窓長の半分以上進んだのに、位置がその半分も進まない窓だけを
        // 再生側の停滞とする（表示側が同時に止まった窓は数えない）。Sparse は判定しない。
        foreach (FollowWindow window in measurable)
        {
            if (window.LtcAdvance >= sparseThreshold && window.PositionAdvance < window.LtcAdvance * minAdvanceRatio)
                stallAdvance++;
        }

        List<FollowWindow> worstAdvancePool = measurable.Count > 0 ? measurable : audited;
        return new ContinuousFollowSummary(
            windows,
            audited.Count == 0 ? 0.0 : audited.Average(window => (double)window.FrameUpdates),
            stallUpdates,
            stallAdvance,
            audited.Count == 0 ? 0.0 : audited.Max(window => window.MaxAbsError),
            audited.Count == 0 ? null : audited.MinBy(window => window.FrameUpdates),
            worstAdvancePool.Count == 0 ? null : worstAdvancePool.MinBy(window => window.PositionAdvance),
            audited.Count == 0 ? null : audited.MaxBy(window => window.MaxAbsError),
            settling.Count,
            settling.Count == 0 ? 0.0 : settling.Max(window => window.MaxAbsError),
            audited.Count(window => window.Sparse));
    }
}
