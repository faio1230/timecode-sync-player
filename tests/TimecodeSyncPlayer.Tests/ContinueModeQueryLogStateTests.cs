using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ContinueModeQueryLogStateTests
{
    [Fact]
    public void ShouldLog_ReturnsTrue_OnFirstQuery()
    {
        var state = new ContinueModeQueryLogState(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);

        bool result = state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.0, DateTime.UtcNow);

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldLog_ReturnsFalse_ForSameQueryWithinInterval()
    {
        var state = new ContinueModeQueryLogState(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.0, now);

        bool result = state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.2, now.AddMilliseconds(500));

        result.Should().BeFalse();
    }

    [Fact]
    public void ShouldLog_ReturnsTrue_WhenStatusChanges()
    {
        var state = new ContinueModeQueryLogState(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.0, now);

        bool result = state.ShouldLog(TimelineQueryStatus.Gap, null, 0.0, now.AddMilliseconds(100));

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldLog_ReturnsTrue_WhenTrackChanges()
    {
        var state = new ContinueModeQueryLogState(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.0, now);

        bool result = state.ShouldLog(TimelineQueryStatus.OnTrack, "Track B", 10.1, now.AddMilliseconds(100));

        result.Should().BeTrue();
    }

    [Fact]
    public void ShouldLog_ReturnsTrue_AfterInterval()
    {
        var state = new ContinueModeQueryLogState(TimeSpan.FromSeconds(1), mediaPositionToleranceSeconds: 0.5);
        DateTime now = DateTime.UtcNow;
        state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.0, now);

        bool result = state.ShouldLog(TimelineQueryStatus.OnTrack, "Track A", 10.1, now.AddSeconds(1));

        result.Should().BeTrue();
    }
}
