using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistTrackExtensionsTests
{
    [Fact]
    public void CalculateTimelineOut_WithNormalTrack_ReturnsTimelineInPlusDuration()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaDuration: TimeSpan.FromSeconds(5));

        var result = track.CalculateTimelineOut();

        result.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void CalculateTimelineOut_WithNoDuration_ReturnsTimelineIn()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaIn: TimeSpan.FromSeconds(5),
            mediaDuration: TimeSpan.FromSeconds(5));

        var result = track.CalculateTimelineOut();

        result.Should().Be(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void CalculateTimelineOut_WithSyncOffset_AdjustsCorrectly()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaDuration: TimeSpan.FromSeconds(5),
            syncOffset: TimeSpan.FromSeconds(7));

        var result = track.CalculateTimelineOut();

        result.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void GetTimelineRangeText_ReturnsFormattedRange()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(3),
            mediaIn: TimeSpan.FromSeconds(3),
            mediaDuration: TimeSpan.FromSeconds(15));

        var result = track.GetTimelineRangeText();

        result.Should().Be("00:03 → 00:15");
    }

    [Fact]
    public void GetTimelineRangeText_WithHourLongMedia_UsesCorrectFormat()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromHours(1),
            mediaDuration: TimeSpan.FromHours(1));

        var result = track.GetTimelineRangeText();

        result.Should().Be("60:00 → 120:00");
    }

    [Fact]
    public void GetTimelineRangeText_WithZeroDuration_SameStartAndEnd()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(5),
            mediaDuration: TimeSpan.Zero);

        var result = track.GetTimelineRangeText();

        result.Should().Be("00:05 → 00:05");
    }

    [Fact]
    public void CalculateTimelineOut_WithDisabledTrack_StillCalculates()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaDuration: TimeSpan.FromSeconds(15),
            isEnabled: false);

        var result = track.CalculateTimelineOut();

        result.Should().Be(TimeSpan.FromSeconds(25));
    }

    private static PlaylistTrack CreateTrack(
        TimeSpan? mediaIn = null,
        TimeSpan? mediaOut = null,
        TimeSpan? timelineOffset = null,
        TimeSpan? mediaDuration = null,
        TimeSpan? syncOffset = null,
        bool isEnabled = true)
    {
        var track = new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "Test",
            MediaIn: mediaIn ?? TimeSpan.Zero,
            MediaOut: mediaOut,
            TimelineOffset: timelineOffset ?? TimeSpan.Zero,
            MediaDuration: mediaDuration ?? TimeSpan.Zero,
            SyncOffset: syncOffset ?? TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: isEnabled);
        return track;
    }
}
