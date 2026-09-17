using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// S-1: 着地後の位置系列が LTC の Continue 写像（タイムライン → 素材位置）に対して
/// 追従しているかの判定。「一度でも窓内に入った」ではなく、2 秒間の全サンプルを許容内で見る。
/// </summary>
public class LtcFollowSeriesTests
{
    // 既定プロジェクトの A（開始 5 秒、MediaIn 0）。
    private const double Start = 5.0;
    private const double MediaIn = 0.0;

    [Fact]
    public void AllSamplesWithinTolerance_IsFollowing()
    {
        var samples = new List<(double Ltc, double Position)>
        {
            (8.0, 3.0),
            (8.5, 3.48),
            (8.25, 3.0), // 写像 3.25 に対して誤差 0.25
        };

        LtcFollowSeries.MaxErrorSeconds(samples, Start, MediaIn).Should().BeApproximately(0.25, 1e-9);
        LtcFollowSeries.IsFollowing(samples, Start, MediaIn, toleranceSeconds: 0.3).Should().BeTrue();
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

        LtcFollowSeries.IsFollowing(samples, Start, MediaIn, toleranceSeconds: 0.3).Should().BeFalse();
        LtcFollowSeries.MaxErrorSeconds(samples, Start, MediaIn).Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void EmptySeries_IsNotFollowing()
    {
        LtcFollowSeries.IsFollowing([], Start, MediaIn, toleranceSeconds: 0.3).Should().BeFalse();
        LtcFollowSeries.MaxErrorSeconds([], Start, MediaIn).Should().Be(0);
    }

    [Fact]
    public void ToleranceBoundary_IsInclusive()
    {
        var samples = new List<(double Ltc, double Position)> { (8.0, 3.3) };

        LtcFollowSeries.IsFollowing(samples, Start, MediaIn, toleranceSeconds: 0.3).Should().BeTrue("境界は許容内");
    }

    [Fact]
    public void TimelineOffset_IsApplied()
    {
        // LTC 17.92 に対し位置 13.0 は、A の開始 5 秒を引いた正しい追従（誤差 0.08）。
        var samples = new List<(double Ltc, double Position)> { (17.92, 13.0) };

        LtcFollowSeries.ToMediaSeconds(17.92, Start, MediaIn).Should().BeApproximately(12.92, 1e-9);
        LtcFollowSeries.MaxErrorSeconds(samples, Start, MediaIn).Should().BeApproximately(0.08, 1e-9);
        LtcFollowSeries.IsFollowing(samples, Start, MediaIn, toleranceSeconds: 0.3).Should().BeTrue();
    }

    [Fact]
    public void MissingTimelineOffset_IsNotFollowing()
    {
        // 写像を忘れて LTC と素材位置を直接比べると 4.92 ずれる。
        var samples = new List<(double Ltc, double Position)> { (17.92, 17.92) };

        LtcFollowSeries.IsFollowing(samples, Start, MediaIn, toleranceSeconds: 0.3).Should().BeFalse();
    }

    [Fact]
    public void MediaInOffset_IsApplied()
    {
        var samples = new List<(double Ltc, double Position)> { (17.92, 13.92) };

        LtcFollowSeries.IsFollowing(samples, Start, mediaInSeconds: 1.0, toleranceSeconds: 0.3).Should().BeTrue();
        LtcFollowSeries.IsFollowing(samples, Start, mediaInSeconds: 0.0, toleranceSeconds: 0.3).Should().BeFalse();
    }
}
