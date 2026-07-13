namespace TimecodeSyncPlayer;

internal sealed class ProjectFileActionRunner
{
    public async Task SaveAsync(
        string? selectedPath,
        Func<string, Task> saveAsync,
        Action<string> logSaved,
        Action<Exception> handleFailure)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        try
        {
            await saveAsync(selectedPath);
            logSaved(selectedPath);
        }
        catch (Exception ex)
        {
            handleFailure(ex);
        }
    }

    public async Task LoadAsync(
        string? selectedPath,
        Func<string, Task<ProjectData?>> loadAsync,
        Action<ProjectData> applyProject,
        Action<string> logLoaded,
        Action handleInvalidProject,
        Action<Exception> handleFailure)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
            return;

        try
        {
            ProjectData? project = await loadAsync(selectedPath);
            if (project == null)
            {
                handleInvalidProject();
                return;
            }

            applyProject(project);
            logLoaded(selectedPath);
        }
        catch (Exception ex)
        {
            handleFailure(ex);
        }
    }
}
