namespace TimecodeSyncPlayer;

public sealed class ProjectLaunchActionExecutor
{
    private readonly Func<string, Task> _loadProjectAsync;
    private readonly Func<IReadOnlyList<string>, Task> _loadPlaylistAsync;
    private readonly Func<string, Task> _saveProjectAsync;

    public ProjectLaunchActionExecutor(
        Func<string, Task> loadProjectAsync,
        Func<IReadOnlyList<string>, Task> loadPlaylistAsync,
        Func<string, Task> saveProjectAsync)
    {
        _loadProjectAsync = loadProjectAsync;
        _loadPlaylistAsync = loadPlaylistAsync;
        _saveProjectAsync = saveProjectAsync;
    }

    public Task ExecuteStartupAsync(ProjectLaunchActionPlan plan)
    {
        return plan.StartupAction switch
        {
            ProjectLaunchStartupAction.LoadProject when plan.LoadProjectPath != null =>
                _loadProjectAsync(plan.LoadProjectPath),
            ProjectLaunchStartupAction.LoadPlaylist =>
                _loadPlaylistAsync(plan.InitialPlaylistPaths),
            _ => Task.CompletedTask
        };
    }

    public Task ExecuteSaveAsync(ProjectLaunchActionPlan plan)
    {
        return plan.SaveProjectPath != null
            ? _saveProjectAsync(plan.SaveProjectPath)
            : Task.CompletedTask;
    }
}
