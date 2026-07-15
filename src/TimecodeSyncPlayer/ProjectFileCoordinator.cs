namespace TimecodeSyncPlayer;

/// <summary>
/// MainWindow のプロジェクト保存・読込フローを副作用デリゲートへ委譲する。
/// </summary>
internal sealed class ProjectFileCoordinator
{
    private readonly ProjectFileActionRunner _runner;
    private readonly ProjectFileEffects _effects;

    internal ProjectFileCoordinator(ProjectFileActionRunner runner, ProjectFileEffects effects)
    {
        _runner = runner;
        _effects = effects;
    }

    internal Task SaveAsync(string? selectedPath)
    {
        return _runner.SaveAsync(
            selectedPath,
            _effects.SaveAsync,
            _effects.LogSaved,
            _effects.HandleSaveFailure);
    }

    internal Task LoadAsync(string? selectedPath)
    {
        return _runner.LoadAsync(
            selectedPath,
            _effects.LoadAsync,
            _effects.ApplyProject,
            _effects.LogLoaded,
            _effects.HandleInvalidProject,
            _effects.HandleLoadFailure);
    }
}

internal sealed record ProjectFileEffects(
    Func<string, Task> SaveAsync,
    Action<string> LogSaved,
    Action<Exception> HandleSaveFailure,
    Func<string, Task<ProjectData?>> LoadAsync,
    Action<ProjectData> ApplyProject,
    Action<string> LogLoaded,
    Action HandleInvalidProject,
    Action<Exception> HandleLoadFailure);
