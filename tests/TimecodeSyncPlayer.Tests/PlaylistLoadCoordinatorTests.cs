using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistLoadCoordinatorTests
{
    [Fact]
    public void ReplaceWithFiles_ClearsExistingTracksAndAddsNewFiles()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\old.mp4"]);
        var coordinator = new PlaylistLoadCoordinator(playlist);

        PlaylistLoadResult result = coordinator.ReplaceWithFiles(
            ["C:\\Videos\\a.mp4", "C:\\Videos\\b.mp4"],
            autoOffset: false);

        playlist.Tracks.Should().HaveCount(2);
        playlist.Tracks[0].FilePath.Should().Be("C:\\Videos\\a.mp4");
        playlist.Tracks[1].FilePath.Should().Be("C:\\Videos\\b.mp4");
        playlist.CurrentIndex.Should().Be(0);
        result.ShouldLoadCurrentTrack.Should().BeTrue();
        result.Paths.Should().Equal("C:\\Videos\\a.mp4", "C:\\Videos\\b.mp4");
    }

    [Fact]
    public void ReplaceWithFiles_ReturnsFalse_WhenNoFilesAdded()
    {
        var playlist = new PlaylistState();
        playlist.AddFiles(["C:\\Videos\\old.mp4"]);
        var coordinator = new PlaylistLoadCoordinator(playlist);

        PlaylistLoadResult result = coordinator.ReplaceWithFiles([], autoOffset: true);

        playlist.Tracks.Should().BeEmpty();
        playlist.CurrentIndex.Should().Be(-1);
        result.ShouldLoadCurrentTrack.Should().BeFalse();
        result.Paths.Should().BeEmpty();
    }
}
