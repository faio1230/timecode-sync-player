namespace TimecodeSyncPlayer;

/// <summary>
/// duration 読込と UI スレッド上のプレイリスト更新を調整する。
/// </summary>
internal sealed class PlaylistDurationBackfillCoordinator
{
    private readonly PlaylistDurationBackfillService _service;
    private readonly PlaylistDurationBackfillEffects _effects;

    internal PlaylistDurationBackfillCoordinator(
        PlaylistDurationBackfillService service,
        PlaylistDurationBackfillEffects effects)
    {
        _service = service;
        _effects = effects;
    }

    internal async Task BackfillAsync(
        IReadOnlyList<string> paths,
        int startIndex = 0,
        bool recalculateTimeline = true)
    {
        try
        {
            await _service.BackfillAsync(
                _effects.GetTracks(),
                paths,
                startIndex,
                (trackId, duration) =>
                    _effects.ApplyDurationOnUiAsync(trackId, duration, recalculateTimeline));
        }
        catch (Exception ex)
        {
            _effects.HandleFailure(ex);
        }
    }
}

internal sealed record PlaylistDurationBackfillEffects(
    Func<IReadOnlyList<PlaylistTrack>> GetTracks,
    Func<Guid, TimeSpan, bool, Task> ApplyDurationOnUiAsync,
    Action<Exception> HandleFailure);
