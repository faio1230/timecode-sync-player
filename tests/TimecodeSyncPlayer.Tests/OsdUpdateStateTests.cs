using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class OsdUpdateStateTests
{
    [Fact]
    public void ShouldUpdate_ReturnsTrueForFirstFrame()
    {
        var state = new OsdUpdateState(TimeSpan.FromMilliseconds(250));

        state.ShouldUpdate(frame: 10, now: DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public void ShouldUpdate_SkipsChangedFrameBeforeMinimumInterval()
    {
        var state = new OsdUpdateState(TimeSpan.FromMilliseconds(250));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        state.ShouldUpdate(frame: 10, now: start).Should().BeTrue();

        state.ShouldUpdate(frame: 11, now: start.AddMilliseconds(100)).Should().BeFalse();
    }

    [Fact]
    public void ShouldUpdate_ReturnsTrueAfterMinimumIntervalWhenFrameChanged()
    {
        var state = new OsdUpdateState(TimeSpan.FromMilliseconds(250));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        state.ShouldUpdate(frame: 10, now: start).Should().BeTrue();

        state.ShouldUpdate(frame: 20, now: start.AddMilliseconds(250)).Should().BeTrue();
    }

    [Fact]
    public void ShouldUpdate_SkipsSameFrameEvenAfterMinimumInterval()
    {
        var state = new OsdUpdateState(TimeSpan.FromMilliseconds(250));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        state.ShouldUpdate(frame: 10, now: start).Should().BeTrue();

        state.ShouldUpdate(frame: 10, now: start.AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public void Reset_AllowsImmediateUpdate()
    {
        var state = new OsdUpdateState(TimeSpan.FromMilliseconds(250));
        DateTime start = new(2026, 5, 8, 12, 0, 0, DateTimeKind.Utc);

        state.ShouldUpdate(frame: 10, now: start).Should().BeTrue();
        state.Reset();

        state.ShouldUpdate(frame: 10, now: start.AddMilliseconds(50)).Should().BeTrue();
    }
}
