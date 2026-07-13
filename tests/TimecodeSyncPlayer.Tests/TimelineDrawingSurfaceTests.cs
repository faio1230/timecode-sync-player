using FluentAssertions;
using System.Windows.Media;

namespace TimecodeSyncPlayer.Tests;

public class TimelineDrawingSurfaceTests
{
    [Theory]
    [InlineData(0, 50, 50, 191, 63, 63)]
    [InlineData(120, 50, 50, 63, 191, 63)]
    [InlineData(240, 50, 50, 63, 63, 191)]
    [InlineData(60, 50, 50, 191, 191, 63)]
    [InlineData(180, 50, 50, 63, 191, 191)]
    [InlineData(300, 50, 50, 191, 63, 191)]
    [InlineData(360, 50, 50, 191, 63, 63)]
    public void HslToColor_ReturnsExpectedRgb(double hue, double saturation, double lightness, byte expectedR, byte expectedG, byte expectedB)
    {
        Color result = TimelineDrawingSurface.HslToColor(hue, saturation, lightness);

        result.R.Should().Be(expectedR);
        result.G.Should().Be(expectedG);
        result.B.Should().Be(expectedB);
    }

    [Fact]
    public void GetTrackColor_Index0_ReturnsFixedBlue()
    {
        Color result = TimelineDrawingSurface.GetTrackColor(0);

        result.R.Should().Be(0x3B);
        result.G.Should().Be(0x82);
        result.B.Should().Be(0xF6);
    }

    [Fact]
    public void GetTrackColor_Index1_ReturnsFixedGreen()
    {
        Color result = TimelineDrawingSurface.GetTrackColor(1);

        result.R.Should().Be(0x10);
        result.G.Should().Be(0xB9);
        result.B.Should().Be(0x81);
    }

    [Fact]
    public void GetTrackColor_Index2_ReturnsFixedPurple()
    {
        Color result = TimelineDrawingSurface.GetTrackColor(2);

        result.R.Should().Be(0x8B);
        result.G.Should().Be(0x5C);
        result.B.Should().Be(0xF6);
    }

    [Fact]
    public void GetTrackColor_Index3Plus_UsesHslCalculation()
    {
        Color result0 = TimelineDrawingSurface.GetTrackColor(3);
        Color result1 = TimelineDrawingSurface.GetTrackColor(4);

        Color expected0 = TimelineDrawingSurface.HslToColor(90, 50, 50);
        Color expected1 = TimelineDrawingSurface.HslToColor(120, 50, 50);

        result0.Should().Be(expected0);
        result1.Should().Be(expected1);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(30, "00:30")]
    [InlineData(599, "09:59")]
    [InlineData(3599, "59:59")]
    public void FormatTimeLabel_SubHour_ReturnsMmSs(double seconds, string expected)
    {
        string result = TimelineDrawingSurface.FormatTimeLabel(seconds);

        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(3600, "01:00:00")]
    [InlineData(3661, "01:01:01")]
    [InlineData(7200, "02:00:00")]
    [InlineData(86399, "23:59:59")]
    public void FormatTimeLabel_SuperHour_ReturnsHhMmSs(double seconds, string expected)
    {
        string result = TimelineDrawingSurface.FormatTimeLabel(seconds);

        result.Should().Be(expected);
    }

    [Fact]
    public void FormatTimeLabel_Exactly3600Seconds_ReturnsHhMmSs()
    {
        string result = TimelineDrawingSurface.FormatTimeLabel(3600);

        result.Should().Be("01:00:00");
    }
}
