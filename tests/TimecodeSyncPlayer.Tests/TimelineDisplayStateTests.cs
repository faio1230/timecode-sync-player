using FluentAssertions;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

public class TimelineDisplayStateTests
{
    [Fact]
    public void DefaultValues_ShouldBeCorrect()
    {
        var state = new TimelineDisplayState();
        state.HorizontalScrollSeconds.Should().Be(0);
        state.VerticalScrollOffset.Should().Be(0);
        state.SecondsPerPixel.Should().Be(1.0);
        state.TrackHeight.Should().Be(24);
        state.IsVisible.Should().BeFalse();
    }

    [Fact]
    public void HorizontalScrollSeconds_ShouldNotBeNegative()
    {
        var state = new TimelineDisplayState();
        state.HorizontalScrollSeconds = -10;
        state.HorizontalScrollSeconds.Should().Be(0);
    }

    [Fact]
    public void VerticalScrollOffset_ShouldNotBeNegative()
    {
        var state = new TimelineDisplayState();
        state.VerticalScrollOffset = -5;
        state.VerticalScrollOffset.Should().Be(0);
    }

    [Theory]
    [InlineData(0.001, 0.01)]
    [InlineData(100.0, 60.0)]
    [InlineData(0.5, 0.5)]
    public void SecondsPerPixel_ShouldClamp(double input, double expected)
    {
        var state = new TimelineDisplayState();
        state.SecondsPerPixel = input;
        state.SecondsPerPixel.Should().Be(expected);
    }

    [Theory]
    [InlineData(5, 12)]
    [InlineData(100, 80)]
    [InlineData(30, 30)]
    public void TrackHeight_ShouldClamp(double input, double expected)
    {
        var state = new TimelineDisplayState();
        state.TrackHeight = input;
        state.TrackHeight.Should().Be(expected);
    }

    [Fact]
    public void VisibleSeconds_ShouldCalculateCorrectly()
    {
        var state = new TimelineDisplayState();
        state.SecondsPerPixel = 0.5;
        state.VisibleSeconds(800).Should().Be(400);
    }

    [Fact]
    public void ZoomIn_ShouldDecreaseSecondsPerPixel()
    {
        var state = new TimelineDisplayState();
        double before = state.SecondsPerPixel;
        state.ZoomIn(10);
        state.SecondsPerPixel.Should().BeLessThan(before);
    }

    [Fact]
    public void ZoomOut_ShouldIncreaseSecondsPerPixel()
    {
        var state = new TimelineDisplayState();
        double before = state.SecondsPerPixel;
        state.ZoomOut(10);
        state.SecondsPerPixel.Should().BeGreaterThan(before);
    }

    [Fact]
    public void ZoomIn_AtMinimum_ShouldStayAtMinimum()
    {
        var state = new TimelineDisplayState();
        state.SecondsPerPixel = 0.01;
        state.ZoomIn(10);
        state.SecondsPerPixel.Should().Be(0.01);
    }

    [Fact]
    public void ZoomOut_AtMaximum_ShouldStayAtMaximum()
    {
        var state = new TimelineDisplayState();
        state.SecondsPerPixel = 60.0;
        state.ZoomOut(10);
        state.SecondsPerPixel.Should().Be(60.0);
    }

    [Fact]
    public void ScrollHorizontal_Positive_ShouldIncreaseScroll()
    {
        var state = new TimelineDisplayState();
        state.ScrollHorizontal(10);
        state.HorizontalScrollSeconds.Should().Be(10);
    }

    [Fact]
    public void ScrollHorizontal_Negative_ShouldNotGoBelowZero()
    {
        var state = new TimelineDisplayState();
        state.ScrollHorizontal(-10);
        state.HorizontalScrollSeconds.Should().Be(0);
    }

    [Fact]
    public void ScrollVertical_Positive_ShouldIncreaseOffset()
    {
        var state = new TimelineDisplayState();
        state.ScrollVertical(3);
        state.VerticalScrollOffset.Should().Be(3);
    }

    [Fact]
    public void ScrollVertical_Negative_ShouldNotGoBelowZero()
    {
        var state = new TimelineDisplayState();
        state.ScrollVertical(-3);
        state.VerticalScrollOffset.Should().Be(0);
    }
}
