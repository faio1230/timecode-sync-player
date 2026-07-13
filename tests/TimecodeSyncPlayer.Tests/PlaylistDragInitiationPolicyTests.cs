using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistDragInitiationPolicyTests
{
    [Fact]
    public void ShouldBeginDrag_ReturnsFalse_WhenDragStartPointIsMissing()
    {
        ShouldBeginDrag(hasDragStartPoint: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldBeginDrag_ReturnsFalse_WhenLeftButtonIsNotPressed()
    {
        ShouldBeginDrag(isLeftButtonPressed: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldBeginDrag_ReturnsFalse_WhenTrackIsNotSelected()
    {
        ShouldBeginDrag(hasSelectedTrack: false).Should().BeFalse();
    }

    [Fact]
    public void ShouldBeginDrag_ReturnsFalse_WhenBothDistancesAreBelowThresholds()
    {
        ShouldBeginDrag(deltaX: 3.9, deltaY: 3.9).Should().BeFalse();
    }

    [Theory]
    [InlineData(4.0, 3.9)]
    [InlineData(3.9, 4.0)]
    public void ShouldBeginDrag_ReturnsTrue_WhenEitherDistanceReachesThreshold(double deltaX, double deltaY)
    {
        ShouldBeginDrag(deltaX: deltaX, deltaY: deltaY).Should().BeTrue();
    }

    [Fact]
    public void ShouldBeginDrag_ReturnsTrue_WhenBothDistancesReachThresholds()
    {
        ShouldBeginDrag(deltaX: 4.0, deltaY: 4.0).Should().BeTrue();
    }

    private static bool ShouldBeginDrag(
        bool hasDragStartPoint = true,
        bool isLeftButtonPressed = true,
        bool hasSelectedTrack = true,
        double deltaX = 4.0,
        double deltaY = 4.0) =>
        PlaylistDragInitiationPolicy.ShouldBeginDrag(
            hasDragStartPoint,
            isLeftButtonPressed,
            hasSelectedTrack,
            deltaX,
            deltaY,
            minHorizontalDistance: 4.0,
            minVerticalDistance: 4.0);
}
