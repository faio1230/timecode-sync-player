using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class SeekBarUpdateStateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ToSliderValue_KeepsFallback_WhenDurationIsNotUsable(double duration)
    {
        double value = SeekBarUpdateState.ToSliderValue(
            positionSeconds: 7.0,
            durationSeconds: duration,
            fallbackValue: 0.7);

        value.Should().BeApproximately(0.7, 0.001);
    }

    [Theory]
    [InlineData(-1, 10, 0)]
    [InlineData(7, 10, 0.7)]
    [InlineData(12, 10, 1)]
    public void ToSliderValue_ClampsPositionIntoSliderRange(double position, double duration, double expected)
    {
        double value = SeekBarUpdateState.ToSliderValue(position, duration, fallbackValue: 0.3);

        value.Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData(-10, 100, 0)]
    [InlineData(70, 100, 0.7)]
    [InlineData(120, 100, 1)]
    public void ToSliderValueFromPointer_ClampsPointerIntoSliderRange(double pointerX, double width, double expected)
    {
        double value = SeekBarUpdateState.ToSliderValueFromPointer(pointerX, width, fallbackValue: 0.3);

        value.Should().BeApproximately(expected, 0.001);
    }

    [Theory]
    [InlineData(70, 0)]
    [InlineData(double.NaN, 100)]
    [InlineData(70, double.PositiveInfinity)]
    public void ToSliderValueFromPointer_KeepsFallback_WhenPointerOrWidthIsNotUsable(double pointerX, double width)
    {
        double value = SeekBarUpdateState.ToSliderValueFromPointer(pointerX, width, fallbackValue: 0.3);

        value.Should().BeApproximately(0.3, 0.001);
    }
}
