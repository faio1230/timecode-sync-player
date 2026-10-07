using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>v0.6.6 F-2: タイムラインのドラッグは押した行に固定。別の行の上で離したらクリックと同じ。</summary>
public sealed class TimelineScrubGestureTests
{
    private const double Threshold = 4;

    [Fact]
    public void Click_WithoutMoving_IsTheCurrentClick()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(1, 100);

        TimelineScrubRelease release = gesture.Release(1);

        release.EndsDrag.Should().BeFalse("a plain click does not start a drag");
        release.TrackIndex.Should().Be(1);
    }

    [Fact]
    public void SmallMovement_BelowTheDragDistance_IsStillAClick()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(1, 100);

        gesture.Move(102, Threshold).Moved.Should().BeFalse();
        gesture.Release(2).Should().Be(new TimelineScrubRelease(false, 2, false),
            "a click released on another row keeps the current click behaviour");
    }

    [Fact]
    public void Drag_IsPinnedToThePressedRow()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(2, 100);

        TimelineScrubMove first = gesture.Move(110, Threshold);
        TimelineScrubMove second = gesture.Move(150, Threshold);

        first.Should().Be(new TimelineScrubMove(true, true, 2));
        second.Should().Be(new TimelineScrubMove(true, false, 2), "the row stays the pressed one while dragging");
        gesture.IsDragging.Should().BeTrue();
        gesture.PinnedTrackIndex.Should().Be(2);
    }

    [Fact]
    public void ReleaseOnTheSameRow_EndsTheDragOnThePinnedRow()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(2, 100);
        gesture.Move(150, Threshold);

        gesture.Release(2).Should().Be(new TimelineScrubRelease(true, 2, true));
        gesture.IsDragging.Should().BeFalse();
    }

    [Fact]
    public void ReleaseOutsideAnyRow_EndsTheDragOnThePinnedRow()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(0, 100);
        gesture.Move(150, Threshold);

        gesture.Release(null).Should().Be(new TimelineScrubRelease(true, 0, true));
    }

    [Fact]
    public void ReleaseOnAnotherRow_BehavesLikeTheCurrentClickOnThatRow()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(0, 100);
        gesture.Move(150, Threshold);

        TimelineScrubRelease release = gesture.Release(3);

        release.EndsDrag.Should().BeTrue("the drag still ends (its throttle closes)");
        release.TrackIndex.Should().Be(3, "the seek is requested for the row under the pointer, like a click");
        release.UsesPinnedTrack.Should().BeFalse();
    }

    [Fact]
    public void PressOutsideAnyRow_NeverDrags()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(null, 100);

        gesture.Move(300, Threshold).Moved.Should().BeFalse();
        gesture.Release(null).Should().Be(new TimelineScrubRelease(false, null, false));
    }

    [Fact]
    public void Cancel_DropsTheDrag()
    {
        var gesture = new TimelineScrubGesture();
        gesture.Press(1, 100);
        gesture.Move(150, Threshold);

        gesture.Cancel();

        gesture.IsDragging.Should().BeFalse();
        gesture.Move(200, Threshold).Moved.Should().BeFalse();
    }
}
