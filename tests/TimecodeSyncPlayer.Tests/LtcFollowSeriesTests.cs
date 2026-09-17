using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// S-1: 着地後の位置系列が LTC 写像に対して追従しているかの判定。
/// 「一度でも窓内に入った」ではなく、2 秒間の全サンプルを許容内で見る。
/// </summary>
public class LtcFollowSeriesTests
{
    // Continue のタイムライン→メディア写像の代わり（開始 5 秒、メディアは 0 起点）。
    private static double MapToMedia(double timelineSeconds) => timelineSeconds - 5.0;

    [Fact]
    public void AllSamplesWithinTolerance_IsFollowing()
    {
        var samples = new List<(double Ltc, double Position)>
        {
            (8.0, 3.0),
            (8.5, 3.48),
            (8.25, 3.0), // 写像 3.25 に対して誤差 0.25
        };

        LtcFollowSeries.MaxErrorSeconds(samples, MapToMedia).Should().BeApproximately(0.25, 1e-9);
        LtcFollowSeries.IsFollowing(samples, MapToMedia, toleranceSeconds: 0.3).Should().BeTrue();
    }

    [Fact]
    public void OneSampleOutsideTolerance_IsNotFollowing()
    {
        var samples = new List<(double Ltc, double Position)>
        {
            (8.0, 3.0),
            (8.5, 3.9), // 写像 3.5 に対して誤差 0.4
            (9.0, 4.0),
        };

        LtcFollowSeries.IsFollowing(samples, MapToMedia, toleranceSeconds: 0.3).Should().BeFalse();
        LtcFollowSeries.MaxErrorSeconds(samples, MapToMedia).Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void EmptySeries_IsNotFollowing()
    {
        LtcFollowSeries.IsFollowing([], MapToMedia, toleranceSeconds: 0.3).Should().BeFalse();
        LtcFollowSeries.MaxErrorSeconds([], MapToMedia).Should().Be(0);
    }

    [Fact]
    public void ToleranceBoundary_IsInclusive()
    {
        var samples = new List<(double Ltc, double Position)> { (8.0, 3.3) };

        LtcFollowSeries.IsFollowing(samples, MapToMedia, toleranceSeconds: 0.3).Should().BeTrue("境界は許容内");
    }
}
