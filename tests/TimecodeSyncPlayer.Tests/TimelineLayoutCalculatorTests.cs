using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimelineLayoutCalculatorTests
{
    [Theory]
    [InlineData(50, 10, 200, 40)]
    [InlineData(10, 10, 200, 100)]
    [InlineData(5, 10, 200, 100)]
    public void CalculateZoomPivot_UsesPlaybackWhenItIsRightOfScrollOtherwiseCenter(
        double playback,
        double scroll,
        double visible,
        double expected)
    {
        TimelineLayoutCalculator.CalculateZoomPivot(playback, scroll, visible)
            .Should().Be(expected);
    }

    [Fact]
    public void CalculateViewport_ScalesAxisAndRowsAndClampsEndToTrackCount()
    {
        TimelineViewportLayout result = TimelineLayoutCalculator.CalculateViewport(
            width: 800,
            height: 220,
            dpiScaleY: 2,
            secondsPerPixel: 0.5,
            horizontalScrollSeconds: 15,
            trackHeight: 20,
            verticalScrollOffset: 2,
            trackCount: 8);

        result.Should().Be(new TimelineViewportLayout(
            TimeAxisHeight: 40,
            TimelineWidth: 800,
            TimelineHeight: 180,
            VisibleSeconds: 400,
            ScrollSeconds: 15,
            TrackHeight: 40,
            StartTrack: 2,
            EndTrack: 7));
    }

    [Fact]
    public void CalculateViewport_EndTrackDoesNotExceedTrackCount()
    {
        TimelineViewportLayout result = TimelineLayoutCalculator.CalculateViewport(
            width: 100,
            height: 1000,
            dpiScaleY: 1,
            secondsPerPixel: 1,
            horizontalScrollSeconds: 0,
            trackHeight: 20,
            verticalScrollOffset: 3,
            trackCount: 5);

        result.EndTrack.Should().Be(5);
    }

    [Fact]
    public void CalculateTrackY_OffsetsFromVisibleStartBelowTimeAxis()
    {
        TimelineLayoutCalculator.CalculateTrackY(5, 3, 20, 24).Should().Be(68);
    }

    [Theory]
    [InlineData(-20, 5, 0, 10)]
    [InlineData(11, 5, 0, 10)]
    public void CalculateClip_OutsideVisibleRangeReturnsNull(
        double clipStart,
        double duration,
        double scroll,
        double visible)
    {
        TimelineLayoutCalculator.CalculateClip(
            clipStart,
            duration,
            hasKnownMediaDuration: true,
            scroll,
            visible,
            secondsPerPixel: 0.5,
            dpiScaleX: 1).Should().BeNull();
    }

    [Fact]
    public void CalculateClip_KnownDurationReturnsUnchangedCoordinates()
    {
        TimelineClipLayout? result = TimelineLayoutCalculator.CalculateClip(
            clipStart: 15,
            clipDuration: 4,
            hasKnownMediaDuration: true,
            scrollSeconds: 10,
            visibleSeconds: 30,
            secondsPerPixel: 0.5,
            dpiScaleX: 2);

        result.Should().Be(new TimelineClipLayout(X: 20, Width: 16));
    }

    [Fact]
    public void CalculateClip_UnknownDurationAppliesDpiScaledMinimumWidth()
    {
        TimelineClipLayout? result = TimelineLayoutCalculator.CalculateClip(
            clipStart: 0,
            clipDuration: 1,
            hasKnownMediaDuration: false,
            scrollSeconds: 0,
            visibleSeconds: 30,
            secondsPerPixel: 1,
            dpiScaleX: 1.5);

        result.Should().Be(new TimelineClipLayout(X: 0, Width: 30));
    }

    [Fact]
    public void CalculateClip_UnknownDurationKeepsWidthWhenItExceedsMinimum()
    {
        TimelineClipLayout? result = TimelineLayoutCalculator.CalculateClip(
            clipStart: 0,
            clipDuration: 30,
            hasKnownMediaDuration: false,
            scrollSeconds: 0,
            visibleSeconds: 30,
            secondsPerPixel: 1,
            dpiScaleX: 1);

        result.Should().Be(new TimelineClipLayout(X: 0, Width: 30));
    }

    [Fact]
    public void SecondsToX_AppliesScrollScaleAndDpiInOriginalOrder()
    {
        TimelineLayoutCalculator.SecondsToX(18, 10, 0.5, 1.25).Should().Be(20);
    }

    [Theory]
    [InlineData(0.01, 1)]
    [InlineData(0.03, 2)]
    [InlineData(0.1, 5)]
    [InlineData(100, 3600)]
    public void GetTimeAxisInterval_SelectsFirstIntervalAtLeastFiftyPixels(
        double secondsPerPixel,
        double expected)
    {
        TimelineLayoutCalculator.GetTimeAxisInterval(secondsPerPixel).Should().Be(expected);
    }

    [Fact]
    public void CalculateTimeTicks_StartsAtFlooredIntervalAndIncludesVisibleEnd()
    {
        IReadOnlyList<TimelineTimeTick> result = TimelineLayoutCalculator.CalculateTimeTicks(
            scrollSeconds: 12,
            visibleSeconds: 28,
            secondsPerPixel: 0.1,
            dpiScaleX: 2);

        result.Should().Equal(
            new TimelineTimeTick(10, -40),
            new TimelineTimeTick(15, 60),
            new TimelineTimeTick(20, 160),
            new TimelineTimeTick(25, 260),
            new TimelineTimeTick(30, 360),
            new TimelineTimeTick(35, 460),
            new TimelineTimeTick(40, 560));
    }

    [Theory]
    [InlineData(50, 0, 1, 1, 100, null)]
    [InlineData(10, 0, 1, 1, 100, -40.0)]
    [InlineData(81, 0, 1, 1, 100, 31.0)]
    [InlineData(20, 0, 1, 1, 100, null)]
    [InlineData(80, 0, 1, 1, 100, null)]
    public void CalculateHorizontalFollowScroll_UsesStrictTwentyPixelMargins(
        double playback,
        double scroll,
        double secondsPerPixel,
        double dpiScaleX,
        double width,
        double? expected)
    {
        TimelineLayoutCalculator.CalculateHorizontalFollowScroll(
            playback,
            scroll,
            secondsPerPixel,
            dpiScaleX,
            width).Should().Be(expected);
    }

    [Theory]
    [InlineData(4, 220, 1, 20, 2, null)]
    [InlineData(1, 220, 1, 20, 2, 0)]
    [InlineData(12, 220, 1, 20, 2, 7)]
    public void CalculateVerticalFollowOffset_CentersOnlyTracksOutsideVisibleRange(
        int trackIndex,
        double height,
        double dpiScaleY,
        double trackHeight,
        int startTrack,
        int? expected)
    {
        TimelineLayoutCalculator.CalculateVerticalFollowOffset(
            trackIndex,
            height,
            dpiScaleY,
            trackHeight,
            startTrack).Should().Be(expected);
    }

    [Theory]
    [InlineData(-1, 30, 1, 24, 0, 3, null)]
    [InlineData(1, 19, 1, 24, 0, 3, null)]
    [InlineData(1, 20, 1, 24, 0, 3, 0)]
    [InlineData(1, 44, 1, 24, 2, 5, 3)]
    [InlineData(1, 200, 1, 24, 0, 3, null)]
    public void CalculateClickedTrackIndex_RejectsOutsideAndMapsRows(
        double x,
        double pointY,
        double dpiScaleY,
        double trackHeight,
        int scrollOffset,
        int trackCount,
        int? expected)
    {
        TimelineLayoutCalculator.CalculateClickedTrackIndex(
            x,
            pointY,
            dpiScaleY,
            trackHeight,
            scrollOffset,
            trackCount).Should().Be(expected);
    }

    [Fact]
    public void CalculateSeekTargetSeconds_PreservesOriginalTrackRelativeExpression()
    {
        TimelineLayoutCalculator.CalculateSeekTargetSeconds(
            x: 50,
            dpiScaleX: 2,
            secondsPerPixel: 0.5,
            horizontalScrollSeconds: 10,
            trackTimelineInSeconds: 100).Should().Be(22.5);
    }
}
