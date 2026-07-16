using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class TimelineQueryTests
{
    [Fact]
    public void FindTrackAtTimelinePosition_WithThousandTracks_ReturnsDeterministicTracksAndEndGap()
    {
        var state = new PlaylistState();
        state.AddFiles(
            Enumerable.Range(0, 1000).Select(index => $"C:\\Videos\\clip-{index:D4}.mp4"),
            autoOffset: false);
        for (int index = 0; index < state.Tracks.Count; index++)
        {
            state.Tracks[index] = state.Tracks[index] with
            {
                TimelineOffset = TimeSpan.FromSeconds(index),
                MediaDuration = TimeSpan.FromSeconds(1),
            };
        }

        TimelineQueryResult first = state.FindTrackAtTimelinePosition(0);
        TimelineQueryResult middle = state.FindTrackAtTimelinePosition(500.5);
        TimelineQueryResult last = state.FindTrackAtTimelinePosition(999.999);
        TimelineQueryResult afterEnd = state.FindTrackAtTimelinePosition(1000);

        first.Track.Should().BeSameAs(state.Tracks[0]);
        middle.Track.Should().BeSameAs(state.Tracks[500]);
        middle.MediaPositionSeconds.Should().BeApproximately(0.5, 0.000001);
        last.Track.Should().BeSameAs(state.Tracks[999]);
        afterEnd.Status.Should().Be(TimelineQueryStatus.Gap);
        afterEnd.PreviousTrack.Should().BeSameAs(state.Tracks[999]);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_WithNegativeOffset_QueriesTheNegativeRange()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\negative.mp4"], autoOffset: false);
        state.Tracks[0] = state.Tracks[0] with
        {
            TimelineOffset = TimeSpan.FromSeconds(-10),
            MediaDuration = TimeSpan.FromSeconds(10),
        };

        TimelineQueryResult result = state.FindTrackAtTimelinePosition(-5);

        result.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result.Track.Should().BeSameAs(state.Tracks[0]);
        result.MediaPositionSeconds.Should().Be(5);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_NearMaximumOffset_RemainsQueryable()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\far-future.mp4"], autoOffset: false);
        TimeSpan offset = TimeSpan.MaxValue - TimeSpan.FromSeconds(2);
        state.Tracks[0] = state.Tracks[0] with
        {
            TimelineOffset = offset,
            MediaDuration = TimeSpan.FromSeconds(1),
        };

        TimelineQueryResult result = state.FindTrackAtTimelinePosition(offset.TotalSeconds + 0.5);

        result.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result.Track.Should().BeSameAs(state.Tracks[0]);
        result.MediaPositionSeconds.Should().BeApproximately(0.5, 0.001);
    }

    [Theory]
    [InlineData(double.NaN, false)]
    [InlineData(double.NegativeInfinity, false)]
    [InlineData(double.PositiveInfinity, true)]
    [InlineData(double.MaxValue, true)]
    public void FindTrackAtTimelinePosition_WithNonFiniteOrHugePosition_ReturnsGapDeterministically(
        double position,
        bool expectsPreviousTrack)
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip.mp4"]);
        state.UpdateMediaDuration(state.Tracks[0].Id, TimeSpan.FromSeconds(10));

        TimelineQueryResult result = state.FindTrackAtTimelinePosition(position);

        result.Status.Should().Be(TimelineQueryStatus.Gap);
        result.Track.Should().BeNull();
        if (expectsPreviousTrack)
            result.PreviousTrack.Should().BeSameAs(state.Tracks[0]);
        else
            result.PreviousTrack.Should().BeNull();
    }

    [Fact]
    public void FindTrackAtTimelinePosition_WithUnknownDurationMixedWithKnownTrack_UsesStrictBoundaries()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\unknown.mp4", "C:\\Videos\\known.mp4"], autoOffset: false);
        state.Tracks[1] = state.Tracks[1] with
        {
            TimelineOffset = TimeSpan.FromSeconds(10),
            MediaDuration = TimeSpan.FromSeconds(5),
        };

        TimelineQueryResult atUnknownStart = state.FindTrackAtTimelinePosition(0);
        TimelineQueryResult atKnownStart = state.FindTrackAtTimelinePosition(10);
        TimelineQueryResult atKnownEnd = state.FindTrackAtTimelinePosition(15);

        atUnknownStart.Status.Should().Be(TimelineQueryStatus.Gap);
        atUnknownStart.PreviousTrack.Should().BeSameAs(state.Tracks[0]);
        atKnownStart.Status.Should().Be(TimelineQueryStatus.OnTrack);
        atKnownStart.Track.Should().BeSameAs(state.Tracks[1]);
        atKnownEnd.Status.Should().Be(TimelineQueryStatus.Gap);
        atKnownEnd.PreviousTrack.Should().BeSameAs(state.Tracks[1]);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ReturnsNoTracks_WhenPlaylistEmpty()
    {
        var state = new PlaylistState();
        var result = state.FindTrackAtTimelinePosition(0);
        result.Status.Should().Be(TimelineQueryStatus.NoTracks);
        result.Track.Should().BeNull();
        result.PreviousTrack.Should().BeNull();
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ReturnsOnTrack_ForPositionWithinTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var track = state.Tracks[0];
        state.UpdateMediaDuration(track.Id, TimeSpan.FromSeconds(60));

        var result = state.FindTrackAtTimelinePosition(30);
        result.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result.Track.Should().BeSameAs(state.Tracks[0]);
        result.MediaPositionSeconds.Should().Be(30);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ReturnsGap_ForPositionBetweenTracks()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4"]);
        var track1 = state.Tracks[0];
        var track2 = state.Tracks[1];
        state.UpdateMediaDuration(track1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(track2.Id, TimeSpan.FromSeconds(60));

        var updatedTrack2 = track2 with { TimelineOffset = TimeSpan.FromSeconds(70) };
        state.Tracks[1] = updatedTrack2;

        var result = state.FindTrackAtTimelinePosition(65);
        result.Status.Should().Be(TimelineQueryStatus.Gap);
        result.PreviousTrack.Should().BeSameAs(state.Tracks[0]);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ReturnsGapWithoutPreviousTrack_ForPositionBeforeFirstTrack()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"], autoOffset: false);
        var track = state.Tracks[0];
        state.Tracks[0] = track with
        {
            TimelineOffset = TimeSpan.FromSeconds(10),
            MediaDuration = TimeSpan.FromSeconds(60)
        };

        var result = state.FindTrackAtTimelinePosition(5);

        result.Status.Should().Be(TimelineQueryStatus.Gap);
        result.PreviousTrack.Should().BeNull();
    }

    [Fact]
    public void FindTrackAtTimelinePosition_PrioritizesLowerPlaylistIndex_WhenTracksOverlap()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4"]);
        var track1 = state.Tracks[0];
        var track2 = state.Tracks[1];
        state.UpdateMediaDuration(track1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(track2.Id, TimeSpan.FromSeconds(60));

        var updatedTrack2 = state.Tracks[1] with { TimelineOffset = TimeSpan.FromSeconds(30) };
        state.Tracks[1] = updatedTrack2;

        var result = state.FindTrackAtTimelinePosition(40);
        result.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result.Track.Should().BeSameAs(state.Tracks[0]);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_CalculatesMediaPosition_Correctly()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var track = state.Tracks[0];
        var updatedTrack = track with { MediaIn = TimeSpan.FromSeconds(10), MediaDuration = TimeSpan.FromSeconds(100) };
        state.Tracks[0] = updatedTrack;
        state.RecalculateTimelineFrom(0);

        var result = state.FindTrackAtTimelinePosition(30);
        result.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result.MediaPositionSeconds.Should().Be(40);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ClampsMediaPosition_ToMediaOut()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var track = state.Tracks[0];
        var updatedTrack = track with { MediaIn = TimeSpan.Zero, MediaOut = TimeSpan.FromSeconds(50), MediaDuration = TimeSpan.FromSeconds(100) };
        state.Tracks[0] = updatedTrack;
        state.RecalculateTimelineFrom(0);

        var result = state.FindTrackAtTimelinePosition(49);
        result.MediaPositionSeconds.Should().Be(49);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_AppliesSyncOffset()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var track = state.Tracks[0];
        var updatedTrack = track with { SyncOffset = TimeSpan.FromSeconds(5), MediaDuration = TimeSpan.FromSeconds(60) };
        state.Tracks[0] = updatedTrack;
        state.RecalculateTimelineFrom(0);

        var result = state.FindTrackAtTimelinePosition(10);
        result.MediaPositionSeconds.Should().Be(15);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_SkipsDisabledTracks()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4"]);
        var track1 = state.Tracks[0];
        var track2 = state.Tracks[1];
        state.UpdateMediaDuration(track1.Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(track2.Id, TimeSpan.FromSeconds(60));

        var disabledTrack1 = track1 with { IsEnabled = false };
        state.Tracks[0] = disabledTrack1;

        var result = state.FindTrackAtTimelinePosition(30);
        result.Status.Should().Be(TimelineQueryStatus.Gap);

        var result2 = state.FindTrackAtTimelinePosition(60);
        result2.Status.Should().Be(TimelineQueryStatus.OnTrack);
        result2.Track.Should().BeSameAs(state.Tracks[1]);
    }

    [Fact]
    public void FindTrackAtTimelinePosition_ReturnsPreviousTrack_ForGapAfterAllTracks()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4"]);
        var track = state.Tracks[0];
        state.UpdateMediaDuration(track.Id, TimeSpan.FromSeconds(60));

        var result = state.FindTrackAtTimelinePosition(120);
        result.Status.Should().Be(TimelineQueryStatus.Gap);
        result.PreviousTrack.Should().BeSameAs(state.Tracks[0]);
    }
}
