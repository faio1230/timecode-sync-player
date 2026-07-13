using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer;

public sealed class PlaylistDurationBackfillService
{
    private readonly IMediaDurationReader _durationReader;

    public PlaylistDurationBackfillService(IMediaDurationReader durationReader)
    {
        _durationReader = durationReader;
    }

    public async Task BackfillAsync(
        IReadOnlyList<PlaylistTrack> tracks,
        IReadOnlyList<string> paths,
        int startIndex = 0,
        Func<Guid, TimeSpan, Task>? applyDurationAsync = null)
    {
        var trackSnapshot = paths
            .Zip(tracks.Skip(startIndex), (path, track) => (Path: path, Track: track))
            .ToList();

        foreach (var (path, track) in trackSnapshot)
        {
            TimeSpan? duration = await _durationReader.ReadDurationAsync(path);
            if (!duration.HasValue)
                continue;

            if (applyDurationAsync != null)
                await applyDurationAsync(track.Id, duration.Value);
        }
    }
}
