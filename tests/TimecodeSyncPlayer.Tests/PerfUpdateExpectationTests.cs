using FluentAssertions;
using TimecodeSyncPlayer.Tests.Helpers;

namespace TimecodeSyncPlayer.Tests;

public sealed class PerfUpdateExpectationTests
{
    [Theory]
    [InlineData(30.0, 54, 66)]
    [InlineData(60.0, 108, 132)]
    [InlineData(24.0, 43, 53)]
    [InlineData(25.0, 45, 55)]
    [InlineData(30000.0 / 1001.0, 53, 66)]
    public void FrameUpdatesRange_ScalesWithSourceFps(double fps, int min, int max)
        => PerfUpdateExpectation.FrameUpdatesRange(fps, 2.0).Should().Be((min, max));

    [Fact]
    public void FrameUpdatesRange_UsesTheRequestedWindowLength()
        => PerfUpdateExpectation.FrameUpdatesRange(30.0, 1.0).Should().Be((27, 33));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void FrameUpdatesRange_FallsBackToThirtyFpsForInvalidFps(double fps)
        => PerfUpdateExpectation.FrameUpdatesRange(fps, 2.0).Should().Be((54, 66));

    [Fact]
    public void FrameUpdatesRange_KeepsThePreviousThirtyFpsRange()
    {
        // 色素材（30fps、実測 2 秒あたり約 60）は従来の 55〜65 を含む範囲で通る。
        (int min, int max) = PerfUpdateExpectation.FrameUpdatesRange(30.0, 2.0);
        min.Should().BeLessThanOrEqualTo(55);
        max.Should().BeGreaterThanOrEqualTo(65);
    }
}
