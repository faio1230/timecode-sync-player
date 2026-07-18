using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistCurrentTrackLabelFormatterTests
{
    [Fact]
    public void Format_ReturnsIndexedCurrentTrackLabel_InSingleMode()
    {
        List<PlaylistTrack> tracks = [Track("A"), Track("B")];

        string label = PlaylistCurrentTrackLabelFormatter.Format(
            syncMode: SyncMode.Single,
            gapBehavior: GapBehavior.Freeze,
            isGapInactive: true,
            tracks,
            currentIndex: 1,
            loadedTrackId: null,
            timelineSeconds: 0);

        label.Should().Be("2/2  B");
    }

    [Fact]
    public void Format_ReturnsNoTrack_WhenSingleModeHasNoCurrentTrack()
    {
        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Single,
            GapBehavior.Black,
            isGapInactive: true,
            tracks: [],
            currentIndex: -1,
            loadedTrackId: null,
            timelineSeconds: 0);

        label.Should().Be("No track");
    }

    [Fact]
    public void Format_ReturnsGapLabelWithNextTrack_InContinueModeGap()
    {
        PlaylistTrack current = Track("track1", timelineOffset: TimeSpan.Zero);
        PlaylistTrack next = Track("track2", timelineOffset: TimeSpan.FromMinutes(13));

        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Black,
            isGapInactive: false,
            tracks: [current, next],
            currentIndex: 0,
            loadedTrackId: current.Id,
            timelineSeconds: TimeSpan.FromMinutes(12).TotalSeconds);

        label.Should().Be("Gap: Black → track2 @ 00:13:00");
    }

    [Fact]
    public void Format_ReturnsFreezeGapLabelWithNextEnabledTrack()
    {
        PlaylistTrack disabled = Track(
            "disabled",
            timelineOffset: TimeSpan.FromSeconds(7),
            isEnabled: false);
        PlaylistTrack next = Track("next", timelineOffset: TimeSpan.FromSeconds(8));

        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Freeze,
            isGapInactive: false,
            tracks: [disabled, next],
            currentIndex: 0,
            loadedTrackId: null,
            timelineSeconds: 6);

        label.Should().Be("Gap: Freeze → next @ 00:00:08");
    }

    [Fact]
    public void Format_ReturnsNoFollowingTrack_InContinueModeGap()
    {
        PlaylistTrack current = Track("last", timelineOffset: TimeSpan.Zero);

        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Black,
            isGapInactive: false,
            tracks: [current],
            currentIndex: 0,
            loadedTrackId: current.Id,
            timelineSeconds: 20);

        label.Should().Be("Gap: Black (以降なし)");
    }

    [Fact]
    public void Format_ReturnsNoFollowingTrack_WhenPlaylistHasNoEnabledTracks()
    {
        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Freeze,
            isGapInactive: false,
            tracks: [],
            currentIndex: -1,
            loadedTrackId: null,
            timelineSeconds: 0);

        label.Should().Be("Gap: Freeze (以降なし)");
    }

    [Fact]
    public void Format_ReturnsLoadedTrackLabel_InContinueMode()
    {
        PlaylistTrack loaded = Track("Loaded");

        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Black,
            isGapInactive: true,
            tracks: [Track("Other"), loaded],
            currentIndex: 0,
            loadedTrackId: loaded.Id,
            timelineSeconds: 0);

        label.Should().Be("Sync: Loaded");
    }

    [Fact]
    public void Format_ReturnsNoTrack_WhenContinueLoadedTrackIsUnknown()
    {
        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Black,
            isGapInactive: true,
            tracks: [Track("Other")],
            currentIndex: 0,
            loadedTrackId: Guid.NewGuid(),
            timelineSeconds: 0);

        label.Should().Be("No track");
    }

    private static PlaylistTrack Track(
        string name,
        TimeSpan? timelineOffset = null,
        bool isEnabled = true) =>
        new(
            Id: Guid.NewGuid(),
            FilePath: $"C:/media/{name}.mp4",
            Name: name,
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: timelineOffset ?? TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(10),
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: isEnabled);
}
