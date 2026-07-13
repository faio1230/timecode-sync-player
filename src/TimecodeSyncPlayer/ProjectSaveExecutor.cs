namespace TimecodeSyncPlayer;

public sealed class ProjectSaveExecutor
{
    private readonly Func<string, SyncMode, GapBehavior, Task> _saveAsync;

    public ProjectSaveExecutor(Func<string, SyncMode, GapBehavior, Task> saveAsync)
    {
        _saveAsync = saveAsync;
    }

    public Task SaveAsync(string path, SyncMode syncMode, GapBehavior gapBehavior)
    {
        return _saveAsync(path, syncMode, gapBehavior);
    }
}
