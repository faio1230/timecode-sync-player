namespace TimecodeSyncPlayer.Tests.Helpers;

/// <summary>
/// S-1: 着地後に採った (LTC, 位置) 系列が LTC の写像へ追従しているかの判定。
/// 写像はモード依存（Continue はタイムライン→素材位置）。空系列や非有限値は追従なしとして扱う。
/// </summary>
internal static class LtcFollowSeries
{
    public static double MaxErrorSeconds(
        IReadOnlyList<(double Ltc, double Position)> samples, Func<double, double> mapToMedia)
    {
        double max = 0;
        foreach ((double ltc, double position) in samples)
        {
            if (!double.IsFinite(ltc) || !double.IsFinite(position)) return double.PositiveInfinity;
            max = Math.Max(max, Math.Abs(position - mapToMedia(ltc)));
        }
        return max;
    }

    public static bool IsFollowing(
        IReadOnlyList<(double Ltc, double Position)> samples, Func<double, double> mapToMedia, double toleranceSeconds) =>
        samples.Count > 0 && MaxErrorSeconds(samples, mapToMedia) <= toleranceSeconds;
}
