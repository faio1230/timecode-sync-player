using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class HoldLandingExpectationTests
{
    [Theory]
    [InlineData(true, 3.0, 2.0, 0.3, 0.04, 2.7, 5.34)]
    [InlineData(true, 3.0, 0.0, 0.3, 0.04, 2.7, 3.34)]
    [InlineData(true, 8.0, 1.5, 0.3, 0.04, 7.7, 9.84)]
    [InlineData(false, 3.0, 2.0, 0.3, 0.04, 2.7, 3.3)]
    [InlineData(false, 8.0, 0.0, 0.3, 0.04, 7.7, 8.3)]
    public void LandingRange_RunThroughClosesAtTargetPlusElapsedAndStopStaysFixed(
        bool runThrough, double mappedTarget, double elapsedSeconds, double toleranceSeconds,
        double frameSeconds, double expectedMin, double expectedMax)
    {
        (double min, double max) = HoldLandingExpectation.LandingRange(
            mappedTarget, elapsedSeconds, runThrough, toleranceSeconds, frameSeconds);

        min.Should().BeApproximately(expectedMin, 1e-9);
        max.Should().BeApproximately(expectedMax, 1e-9);
    }

    [Fact]
    public void IsLanded_AcceptsBothBoundsAndRejectsOutside()
    {
        HoldLandingExpectation.IsLanded(2.7, 2.7, 3.4).Should().BeTrue();
        HoldLandingExpectation.IsLanded(3.4, 2.7, 3.4).Should().BeTrue();
        HoldLandingExpectation.IsLanded(2.699, 2.7, 3.4).Should().BeFalse();
        HoldLandingExpectation.IsLanded(3.401, 2.7, 3.4).Should().BeFalse();
        HoldLandingExpectation.IsLanded(double.NaN, 2.7, 3.4).Should().BeFalse();
    }

    [Theory]
    [InlineData(true, 3.0, 2.0, 5.0)]
    [InlineData(true, 3.0, 0.0, 3.0)]
    [InlineData(false, 3.0, 2.0, 3.0)]
    [InlineData(false, 8.0, 5.0, 8.0)]
    public void ExpectedPosition_MovesWithElapsedOnlyInRunThrough(
        bool runThrough, double mappedTarget, double elapsedSeconds, double expected) =>
        HoldLandingExpectation.ExpectedPosition(mappedTarget, elapsedSeconds, runThrough).Should().Be(expected);
}
