using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistTimelineOffsetEditorTests
{
    [Fact]
    public void Apply_UpdatesTimelineOffset_WhenInputIsValid()
    {
        var state = CreatePlaylist();
        Guid trackId = state.Tracks[0].Id;

        var result = PlaylistTimelineOffsetEditor.Apply(
            state,
            trackId,
            "00:00:10:00",
            autoOffset: false,
            fallbackFps: 30.0);

        result.Status.Should().Be(PlaylistTimelineOffsetEditStatus.Applied);
        result.Index.Should().Be(0);
        result.Fps.Should().Be(30);
        state.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(10));
        state.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Apply_RecalculatesFollowingTracks_WhenAutoOffsetIsEnabled()
    {
        var state = CreatePlaylist();
        Guid trackId = state.Tracks[0].Id;

        var result = PlaylistTimelineOffsetEditor.Apply(
            state,
            trackId,
            "00:00:10:00",
            autoOffset: true,
            fallbackFps: 30.0);

        result.Status.Should().Be(PlaylistTimelineOffsetEditStatus.Applied);
        state.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(10));
        state.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(70));
    }

    [Fact]
    public void Apply_UsesTrackFrameRate_WhenAvailable()
    {
        var state = CreatePlaylist();
        Guid trackId = state.Tracks[0].Id;
        state.Tracks[0] = state.Tracks[0] with { FrameRate = 24.0 };

        var result = PlaylistTimelineOffsetEditor.Apply(
            state,
            trackId,
            "00:00:01:12",
            autoOffset: false,
            fallbackFps: 30.0);

        result.Status.Should().Be(PlaylistTimelineOffsetEditStatus.Applied);
        result.Fps.Should().Be(24);
        state.Tracks[0].TimelineOffset.Should().Be(TimeSpan.FromSeconds(1.5));
    }

    [Fact]
    public void Apply_DoesNotUpdatePlaylist_WhenInputIsInvalid()
    {
        var state = CreatePlaylist();
        Guid trackId = state.Tracks[0].Id;
        TimeSpan originalOffset = state.Tracks[0].TimelineOffset;

        var result = PlaylistTimelineOffsetEditor.Apply(
            state,
            trackId,
            "not-timecode",
            autoOffset: true,
            fallbackFps: 30.0);

        result.Status.Should().Be(PlaylistTimelineOffsetEditStatus.ParseFailed);
        result.OriginalTrack.Should().Be(state.Tracks[0]);
        state.Tracks[0].TimelineOffset.Should().Be(originalOffset);
        state.Tracks[1].TimelineOffset.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void Apply_ReturnsTrackNotFound_WhenTrackIdIsUnknown()
    {
        var state = CreatePlaylist();

        var result = PlaylistTimelineOffsetEditor.Apply(
            state,
            Guid.NewGuid(),
            "00:00:10:00",
            autoOffset: true,
            fallbackFps: 30.0);

        result.Status.Should().Be(PlaylistTimelineOffsetEditStatus.TrackNotFound);
        result.Index.Should().Be(-1);
        result.OriginalTrack.Should().BeNull();
    }

    private static PlaylistState CreatePlaylist()
    {
        var state = new PlaylistState();
        state.AddFiles(["C:\\Videos\\clip1.mp4", "C:\\Videos\\clip2.mp4"]);
        state.UpdateMediaDuration(state.Tracks[0].Id, TimeSpan.FromSeconds(60));
        state.UpdateMediaDuration(state.Tracks[1].Id, TimeSpan.FromSeconds(30));
        return state;
    }
}
