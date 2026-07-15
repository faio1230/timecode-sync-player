namespace TimecodeSyncPlayer;

/// <summary>
/// Coordinates the remaining Window.Loaded startup sequence while UI and native
/// operations stay in MainWindow through invocation-time effects.
/// </summary>
internal sealed class WindowLoadedCoordinator
{
    private readonly WindowLoadedEffects _effects;

    public WindowLoadedCoordinator(WindowLoadedEffects effects)
    {
        _effects = effects;
    }

    public void Initialize()
    {
        _effects.InitializeUi();
        AppLaunchArguments launchArguments = _effects.ParseLaunchArguments();

        if (!_effects.InitializeSession())
            return;

        ProjectLaunchActionPlan launchActionPlan = ProjectLaunchActionPlanner.Decide(launchArguments);
        _effects.ScheduleLaunchAction(launchActionPlan);
    }
}

internal sealed record WindowLoadedEffects(
    Action InitializeUi,
    Func<AppLaunchArguments> ParseLaunchArguments,
    Func<bool> InitializeSession,
    Action<ProjectLaunchActionPlan> ScheduleLaunchAction);
