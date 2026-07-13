using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistTrackFormatterTests
{
    [Fact]
    public void FormatTimecode_FormatsCorrectly()
    {
        var ts = TimeSpan.FromSeconds(90.5);
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("00:01:30:15");
    }

    [Fact]
    public void FormatTimecode_Zero_ReturnsZeros()
    {
        var ts = TimeSpan.Zero;
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("00:00:00:00");
    }

    [Fact]
    public void FormatTimecode_Hours_ReturnsCorrectHours()
    {
        var ts = TimeSpan.FromHours(1) + TimeSpan.FromMinutes(30);
        var result = PlaylistTrackFormatter.FormatTimecode(ts, 30);
        result.Should().Be("01:30:00:00");
    }

    [Theory]
    [InlineData("00:00:30:00", 30, 30.0)]
    [InlineData("00:01:00:00", 30, 60.0)]
    [InlineData("00:00:00:15", 30, 0.5)]
    [InlineData("01:00:00:00", 30, 3600.0)]
    [InlineData("00:00:00:00", 30, 0.0)]
    public void TryParseTimecode_ValidInputs_ReturnsCorrectTimeSpan(string input, int fps, double expectedSeconds)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, fps, out var result);
        success.Should().BeTrue();
        result.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.001);
    }

    [Theory]
    [InlineData("")]
    [InlineData("00:00:00")]
    [InlineData("00:00:00:00:00")]
    [InlineData("invalid")]
    [InlineData("00:00:00:30")]
    [InlineData("00:00:60:00")]
    [InlineData("00:60:00:00")]
    [InlineData("-01:00:00:00")]
    public void TryParseTimecode_InvalidInputs_ReturnsFalse(string input)
    {
        var success = PlaylistTrackFormatter.TryParseTimecode(input, 30, out _);
        success.Should().BeFalse();
    }

    [Fact]
    public void FormatTimelineOffset_UsesTrackFrameRate()
    {
        var track = CreateTrack(timelineOffset: TimeSpan.FromSeconds(10.5), frameRate: 24);
        var result = PlaylistTrackFormatter.FormatTimelineOffset(track);
        result.Should().Be("00:00:10:12");
    }

    [Fact]
    public void FormatMediaDuration_UsesTrackFrameRate()
    {
        var track = CreateTrack(mediaDuration: TimeSpan.FromSeconds(120), frameRate: 25);
        var result = PlaylistTrackFormatter.FormatMediaDuration(track);
        result.Should().Be("00:02:00:00");
    }

    [Fact]
    public void FormatEffectiveDuration_UsesTrackFrameRate()
    {
        var track = CreateTrack(mediaIn: TimeSpan.FromSeconds(10), mediaDuration: TimeSpan.FromSeconds(70), frameRate: 30);
        var result = PlaylistTrackFormatter.FormatEffectiveDuration(track);
        result.Should().Be("00:01:00:00");
    }

    [Fact]
    public void FormatTimelineRange_UsesActualPositions()
    {
        var track = CreateTrack(
            timelineOffset: TimeSpan.FromSeconds(10),
            mediaDuration: TimeSpan.FromSeconds(60),
            frameRate: 30);

        var result = PlaylistTrackFormatter.FormatTimelineRange(track);
        result.Should().Be("00:00:10:00 → 00:01:10:00");
    }

    private static PlaylistTrack CreateTrack(
        TimeSpan? timelineOffset = null,
        TimeSpan? mediaDuration = null,
        TimeSpan? mediaIn = null,
        double? frameRate = null)
    {
        return new PlaylistTrack(
            Id: Guid.NewGuid(),
            FilePath: "test.mp4",
            Name: "test",
            MediaIn: mediaIn ?? TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: timelineOffset ?? TimeSpan.Zero,
            MediaDuration: mediaDuration ?? TimeSpan.Zero,
            SyncOffset: TimeSpan.Zero,
            FrameRate: frameRate ?? 30,
            IsEnabled: true);
    }
}
