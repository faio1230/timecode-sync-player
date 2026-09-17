using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

/// <summary>L-1: シーク着地時間の集計（純関数）の固定。</summary>
public sealed class SeekLandingStatsTests
{
    [Fact]
    public void Summarize_Empty_ReturnsZeros()
    {
        SeekLandingSummary summary = SeekLandingStats.Summarize([]);

        summary.Count.Should().Be(0);
        summary.MedianSeconds.Should().Be(0.0);
        summary.MaxSeconds.Should().Be(0.0);
    }

    [Fact]
    public void Summarize_OddCount_MedianIsMiddleValue()
    {
        SeekLandingSummary summary = SeekLandingStats.Summarize([0.46, 1.75, 0.60]);

        summary.Count.Should().Be(3);
        summary.MedianSeconds.Should().BeApproximately(0.60, 0.0001);
        summary.MaxSeconds.Should().BeApproximately(1.75, 0.0001);
    }

    [Fact]
    public void Summarize_EvenCount_MedianAveragesTwoMiddleValues()
    {
        SeekLandingSummary summary = SeekLandingStats.Summarize([0.2, 0.4, 0.6, 1.8]);

        summary.Count.Should().Be(4);
        summary.MedianSeconds.Should().BeApproximately(0.5, 0.0001);
        summary.MaxSeconds.Should().BeApproximately(1.8, 0.0001);
    }

    [Fact]
    public void Summarize_IgnoresNonFiniteAndNegative()
    {
        SeekLandingSummary summary = SeekLandingStats.Summarize([double.NaN, -1.0, 0.5, double.PositiveInfinity, 0.7]);

        summary.Count.Should().Be(2);
        summary.MedianSeconds.Should().BeApproximately(0.6, 0.0001);
        summary.MaxSeconds.Should().BeApproximately(0.7, 0.0001);
    }
}
