namespace TimecodeSyncPlayer;

internal sealed class PlaylistAddFilesActionRunner
{
    public async Task RunAsync(
        bool wasEmpty,
        Func<Task> addFilesAsync,
        Action<Exception> logError,
        Action showError,
        Action syncSelection,
        Action updateTimelineDisplay,
        Func<bool> hasCurrentTrack,
        Action loadCurrentTrack)
    {
        try
        {
            await addFilesAsync();
        }
        catch (Exception ex)
        {
            logError(ex);
            showError();
            return;
        }

        syncSelection();
        updateTimelineDisplay();
        if (wasEmpty && hasCurrentTrack())
            loadCurrentTrack();
    }
}
