namespace TimecodeSyncPlayer;

public sealed record PlaylistLoadResult(
    IReadOnlyList<string> Paths,
    bool ShouldLoadCurrentTrack);

public sealed class PlaylistLoadCoordinator
{
    private readonly PlaylistState _playlist;

    public PlaylistLoadCoordinator(PlaylistState playlist)
    {
        _playlist = playlist;
    }

    public PlaylistLoadResult ReplaceWithFiles(IEnumerable<string> paths, bool autoOffset)
    {
        List<string> pathList = paths.ToList();
        _playlist.Clear();
        _playlist.AddFiles(pathList, autoOffset);

        return new PlaylistLoadResult(
            pathList,
            _playlist.Current != null);
    }
}
