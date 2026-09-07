using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RealProjectTimelineBoundaryTests
{
    // Anonymous reproduction of the discovered project's exact timings. No personal media required.
    [Theory]
    [InlineData(29.96, null)]
    [InlineData(30, "A")]
    [InlineData(267.76, "A")]
    [InlineData(267.7723359, "A")]
    [InlineData(267.772336, null)]
    [InlineData(267.8, null)]
    [InlineData(290.76, null)]
    [InlineData(290.8, "B")]
    [InlineData(440.32, "B")]
    [InlineData(473.28, "B")]
    [InlineData(473.32, "C")]
    [InlineData(686.64, "C")]
    [InlineData(686.68, null)]
    public void GapEdges_UseHalfOpenTimelineIntervals_AndPlaylistPriority(double seconds, string? expected)
    {
        var playlist = new PlaylistState();
        playlist.Tracks.Add(Track("A", "00:00:30", "00:03:57.7723360", 24));
        playlist.Tracks.Add(Track("B", "00:04:50.7666666", "00:03:02.5320630", 60));
        playlist.Tracks.Add(Track("C", "00:07:20.3000000", "00:04:06.3637190", 30000d / 1001));

        var result = playlist.FindTrackAtTimelinePosition(seconds);

        result.Track?.Name.Should().Be(expected);
        result.Status.Should().Be(expected == null ? TimelineQueryStatus.Gap : TimelineQueryStatus.OnTrack);
    }

    [Theory]
    [InlineData(1, "A", 1)]
    [InlineData(179.96, "A", 179.96)]
    [InlineData(180, "B", 0)]
    [InlineData(300, "B", 120)]
    [InlineData(362.56, "C", 62.56)]
    [InlineData(546.4, null, 0)]
    public void ReorderedOverlappingTracks_PreserveFirstRowPriority(double seconds, string? expected, double mediaPosition)
    {
        var playlist = new PlaylistState();
        playlist.Tracks.Add(Track("B", "00:03:00", "00:03:02.5320630", 60));
        playlist.Tracks.Add(Track("A", "00:00:00", "00:03:57.7723360", 24));
        playlist.Tracks.Add(Track("C", "00:05:00", "00:04:06.3637190", 30000d / 1001));

        var result = playlist.FindTrackAtTimelinePosition(seconds);

        result.Track?.Name.Should().Be(expected);
        result.MediaPositionSeconds.Should().BeApproximately(mediaPosition, 0.000001);
    }

    private static PlaylistTrack Track(string name, string offset, string duration, double fps) => new(
        Guid.NewGuid(), name + ".mp4", name, TimeSpan.Zero, null,
        TimeSpan.Parse(offset), TimeSpan.Parse(duration), TimeSpan.Zero, fps, true);
}
