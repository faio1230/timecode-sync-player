using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimecodeDisplayFormatterTests
{
    [Theory]
    [InlineData(24.0, false, "24 fps")]
    [InlineData(25.0, false, "25 fps")]
    [InlineData(30.0, false, "30 fps")]
    [InlineData(30.0, true, "29.97 fps (DF)")]
    public void FpsLabel_FormatsStandardRates(double fps, bool dropFrame, string expected)
    {
        TimecodeDisplayFormatter.FpsLabel(fps, dropFrame).Should().Be(expected);
    }

    [Fact]
    public void RealTime_FormatsSecondsWithThreeDecimals()
    {
        TimecodeDisplayFormatter.RealTime(12.3456).Should().Be("12.346 s");
    }

    [Fact]
    public void FpsLabel_With29_97Fps_ReturnsDropFrameLabel()
    {
        TimecodeDisplayFormatter.FpsLabel(29.97, true).Should().Be("29.97 fps (DF)");
    }

    [Fact]
    public void FpsLabel_With29_97FpsNoDropFrame_Returns30FpsLabel()
    {
        TimecodeDisplayFormatter.FpsLabel(29.97, false).Should().Be("30 fps");
    }

    [Fact]
    public void FpsLabel_With23_976Fps_ReturnsGeneric30()
    {
        TimecodeDisplayFormatter.FpsLabel(23.976, false).Should().Be("30 fps");
    }

    [Fact]
    public void FpsLabel_With59_94Fps_ReturnsGeneric30()
    {
        TimecodeDisplayFormatter.FpsLabel(59.94, true).Should().Be("29.97 fps (DF)");
    }

    [Fact]
    public void FpsLabel_WithZeroFps_ReturnsGeneric30()
    {
        TimecodeDisplayFormatter.FpsLabel(0, false).Should().Be("30 fps");
    }

    [Fact]
    public void RealTime_WithMaxValue_DoesNotOverflow()
    {
        TimecodeDisplayFormatter.RealTime(double.MaxValue).Should().NotBeNull();
    }

    [Fact]
    public void RealTime_WithNegativeValue_ClampsToZero()
    {
        TimecodeDisplayFormatter.RealTime(-5.0).Should().Be("0.000 s");
    }

    [Fact]
    public void RealTime_WithVerySmallValue_FormatsCorrectly()
    {
        TimecodeDisplayFormatter.RealTime(0.001).Should().Be("0.001 s");
    }
}
