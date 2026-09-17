namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// S-1: 着地後に採った (LTC, 位置) 系列が Continue の写像へ追従しているかの判定。
/// LTC はタイムライン秒、位置は素材秒なので、タイムライン開始と MediaIn で写像してから比べる
/// （LTC 17.92・位置 13.0・開始 5 秒は正しい追従）。空系列や非有限値は追従なしとして扱う。
/// </summary>
internal static class LtcFollowSeries
{
    /// <summary>Continue のタイムライン→素材位置写像。</summary>
    public static double ToMediaSeconds(double timelineSeconds, double timelineStartSeconds, double mediaInSeconds) =>
        timelineSeconds - timelineStartSeconds + mediaInSeconds;

    public static double MaxErrorSeconds(
        IReadOnlyList<(double Ltc, double Position)> samples,
        double timelineStartSeconds, double mediaInSeconds)
    {
        double max = 0;
        foreach ((double ltc, double position) in samples)
        {
            if (!double.IsFinite(ltc) || !double.IsFinite(position)) return double.PositiveInfinity;
            max = Math.Max(max, Math.Abs(position - ToMediaSeconds(ltc, timelineStartSeconds, mediaInSeconds)));
        }
        return max;
    }

    public static bool IsFollowing(
        IReadOnlyList<(double Ltc, double Position)> samples,
        double timelineStartSeconds, double mediaInSeconds, double toleranceSeconds) =>
        samples.Count > 0 && MaxErrorSeconds(samples, timelineStartSeconds, mediaInSeconds) <= toleranceSeconds;
}
