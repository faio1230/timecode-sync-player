using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class HoldLandingExpectationTests
{
    [Theory]
    [InlineData(true, 3.0, 2.0, 5.0)]
    [InlineData(true, 3.0, 0.0, 3.0)]
    [InlineData(false, 3.0, 2.0, 3.0)]
    [InlineData(false, 8.0, 5.0, 8.0)]
    public void ExpectedPosition_MovesWithElapsedOnlyInRunThrough(
        bool runThrough, double mappedTarget, double elapsedSeconds, double expected) =>
        HoldLandingExpectation.ExpectedPosition(mappedTarget, elapsedSeconds, runThrough).Should().Be(expected);

    [Fact]
    public void IsLanded_UsesTheToleranceAndRejectsNonFiniteObservations()
    {
        HoldLandingExpectation.IsLanded(5.34, 5.0, 0.34).Should().BeTrue();
        HoldLandingExpectation.IsLanded(5.35, 5.0, 0.34).Should().BeFalse();
        HoldLandingExpectation.IsLanded(double.NaN, 5.0, 0.34).Should().BeFalse();
    }
}
