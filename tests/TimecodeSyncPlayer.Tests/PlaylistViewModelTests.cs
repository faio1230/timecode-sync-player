using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.ViewModels;

namespace TimecodeSyncPlayer.Tests;

public class PlaylistViewModelTests
{
    private sealed class FakeDurationReader : IMediaDurationReader
    {
        public TimeSpan? ReturnValue = TimeSpan.FromSeconds(10);
        public Task<TimeSpan?> ReadDurationAsync(string filePath) =>
            Task.FromResult(ReturnValue);
    }

    private static PlaylistState MakePlaylist(params string[] paths)
    {
        var pl = new PlaylistState();
        pl.AddFiles(paths, autoOffset: false);
        return pl;
    }

    [Fact]
    public async Task AddFilesAsync_AddsTracksToPlaylist()
    {
        var playlist = new PlaylistState();
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());

        await vm.AddFilesAsync(["a.mp4", "b.mp4"], CancellationToken.None);

        playlist.Tracks.Should().HaveCount(2);
    }

    [Fact]
    public async Task AddFilesAsync_UpdatesMediaDuration()
    {
        var playlist = new PlaylistState();
        var reader = new FakeDurationReader { ReturnValue = TimeSpan.FromSeconds(42) };
        var vm = new PlaylistViewModel(playlist, reader);

        await vm.AddFilesAsync(["test.mp4"], CancellationToken.None);

        playlist.Tracks[0].MediaDuration.TotalSeconds.Should().Be(42);
    }

    [Fact]
    public async Task AddFilesAsync_RespectsCancellation()
    {
        var playlist = new PlaylistState();
        var reader = new FakeDurationReader();
        var vm = new PlaylistViewModel(playlist, reader);
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => vm.AddFilesAsync(["a.mp4", "b.mp4"], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void RemoveTrackCommand_RemovesSelectedTrack()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 0;

        vm.RemoveTrackCommand.Execute(null);

        playlist.Tracks.Should().HaveCount(1);
        playlist.Tracks[0].Name.Should().Be("b");
    }

    [Fact]
    public void RemoveTrackCommand_CanExecute_FalseWhenNoSelection()
    {
        var playlist = MakePlaylist("a.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = -1;

        vm.RemoveTrackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void MoveUpCommand_MovesSelectedTrackUp()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 1;

        vm.MoveUpCommand.Execute(null);

        playlist.Tracks[0].Name.Should().Be("b");
        playlist.Tracks[1].Name.Should().Be("a");
    }

    [Fact]
    public void MoveDownCommand_MovesSelectedTrackDown()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 0;

        vm.MoveDownCommand.Execute(null);

        playlist.Tracks[0].Name.Should().Be("b");
        playlist.Tracks[1].Name.Should().Be("a");
    }

    [Fact]
    public void ClearTracksCommand_ClearsPlaylist()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());

        vm.ClearTracksCommand.Execute(null);

        playlist.Tracks.Should().BeEmpty();
    }

    [Fact]
    public void RemoveTrackCommand_FiresTrackRemovedEvent()
    {
        var playlist = MakePlaylist("a.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 0;
        bool fired = false;
        vm.TrackRemoved += () => fired = true;

        vm.RemoveTrackCommand.Execute(null);

        fired.Should().BeTrue();
    }

    [Fact]
    public void ClearTracksCommand_FiresTracksClearedEvent()
    {
        var playlist = MakePlaylist("a.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        bool fired = false;
        vm.TracksCleared += () => fired = true;

        vm.ClearTracksCommand.Execute(null);

        fired.Should().BeTrue();
    }

    [Fact]
    public void MoveUpCommand_UpdatesSelectedIndexBeforeFiringEvent()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4", "c.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 2;

        int capturedIndex = -99;
        vm.TrackMoved += () => capturedIndex = vm.SelectedIndex;

        vm.MoveUpCommand.Execute(null);

        capturedIndex.Should().Be(1);
        vm.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void MoveDownCommand_UpdatesSelectedIndexBeforeFiringEvent()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4", "c.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 0;

        int capturedIndex = -99;
        vm.TrackMoved += () => capturedIndex = vm.SelectedIndex;

        vm.MoveDownCommand.Execute(null);

        capturedIndex.Should().Be(1);
        vm.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void MoveUpCommand_RestoresFinalSelection_WhenCollectionChangeEchoClearsSelection()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4", "c.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 2;
        playlist.Tracks.CollectionChanged += (_, _) => vm.SelectedIndex = -1;

        vm.MoveUpCommand.Execute(null);

        playlist.Tracks.Select(track => track.Name).Should().Equal("a", "c", "b");
        vm.SelectedIndex.Should().Be(1);
    }

    [Fact]
    public void MoveDownCommand_RestoresFinalSelection_WhenCollectionChangeEchoClearsSelection()
    {
        var playlist = MakePlaylist("a.mp4", "b.mp4", "c.mp4");
        var vm = new PlaylistViewModel(playlist, new FakeDurationReader());
        vm.SelectedIndex = 1;
        playlist.Tracks.CollectionChanged += (_, _) => vm.SelectedIndex = -1;

        vm.MoveDownCommand.Execute(null);

        playlist.Tracks.Select(track => track.Name).Should().Equal("a", "c", "b");
        vm.SelectedIndex.Should().Be(2);
    }
}
