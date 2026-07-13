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
            loadedTrackId: null);

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
            loadedTrackId: null);

        label.Should().Be("No track");
    }

    [Fact]
    public void Format_ReturnsGapLabel_InContinueModeGap()
    {
        string label = PlaylistCurrentTrackLabelFormatter.Format(
            SyncMode.Continue,
            GapBehavior.Freeze,
            isGapInactive: false,
            tracks: [Track("A")],
            currentIndex: 0,
            loadedTrackId: null);

        label.Should().Be("Gap: Freeze");
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
            loadedTrackId: loaded.Id);

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
            loadedTrackId: Guid.NewGuid());

        label.Should().Be("No track");
    }

    private static PlaylistTrack Track(string name) =>
        new(
            Id: Guid.NewGuid(),
            FilePath: $"C:/media/{name}.mp4",
            Name: name,
            MediaIn: TimeSpan.Zero,
            MediaOut: null,
            TimelineOffset: TimeSpan.Zero,
            MediaDuration: TimeSpan.FromSeconds(10),
            SyncOffset: TimeSpan.Zero,
            FrameRate: null,
            IsEnabled: true);
}
