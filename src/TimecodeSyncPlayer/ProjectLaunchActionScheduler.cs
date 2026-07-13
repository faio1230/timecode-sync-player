namespace TimecodeSyncPlayer;

internal sealed class ProjectLaunchActionScheduler
{
    private readonly Action<Func<Task>> _scheduleStartup;
    private readonly Action<Func<Task>> _scheduleSave;
    private readonly Func<TimeSpan, Task> _delayAsync;
    private readonly Action<Exception, ProjectLaunchActionPlan> _logStartupFailure;
    private readonly Action<string> _logSaveCompleted;
    private readonly Action<Exception, string> _logSaveFailure;

    public ProjectLaunchActionScheduler(
        Action<Func<Task>> scheduleStartup,
        Action<Func<Task>> scheduleSave,
        Func<TimeSpan, Task> delayAsync,
        Action<Exception, ProjectLaunchActionPlan> logStartupFailure,
        Action<string> logSaveCompleted,
        Action<Exception, string> logSaveFailure)
    {
        _scheduleStartup = scheduleStartup;
        _scheduleSave = scheduleSave;
        _delayAsync = delayAsync;
        _logStartupFailure = logStartupFailure;
        _logSaveCompleted = logSaveCompleted;
        _logSaveFailure = logSaveFailure;
    }

    public void Schedule(
        ProjectLaunchActionPlan plan,
        ProjectLaunchActionExecutor executor,
        TimeSpan saveDelay)
    {
        if (plan.StartupAction != ProjectLaunchStartupAction.None)
            _scheduleStartup(() => ExecuteStartupAsync(plan, executor));

        if (plan.SaveProjectPath != null)
            _scheduleSave(() => ExecuteSaveAsync(plan, executor, saveDelay));
    }

    private async Task ExecuteStartupAsync(
        ProjectLaunchActionPlan plan,
        ProjectLaunchActionExecutor executor)
    {
        try
        {
            await executor.ExecuteStartupAsync(plan);
        }
        catch (Exception ex)
        {
            _logStartupFailure(ex, plan);
        }
    }

    private async Task ExecuteSaveAsync(
        ProjectLaunchActionPlan plan,
        ProjectLaunchActionExecutor executor,
        TimeSpan saveDelay)
    {
        string saveProjectPath = plan.SaveProjectPath!;
        await _delayAsync(saveDelay);

        try
        {
            await executor.ExecuteSaveAsync(plan);
            _logSaveCompleted(saveProjectPath);
        }
        catch (Exception ex)
        {
            _logSaveFailure(ex, saveProjectPath);
        }
    }
}
