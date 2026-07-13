namespace TimecodeSyncPlayer;

public enum ProjectLaunchStartupAction
{
    None,
    LoadProject,
    LoadPlaylist
}

public sealed record ProjectLaunchActionPlan(
    ProjectLaunchStartupAction StartupAction,
    string? LoadProjectPath,
    IReadOnlyList<string> InitialPlaylistPaths,
    string? SaveProjectPath);

public static class ProjectLaunchActionPlanner
{
    public static ProjectLaunchActionPlan Decide(AppLaunchArguments arguments)
    {
        if (arguments.LoadProjectPath != null)
        {
            return new ProjectLaunchActionPlan(
                ProjectLaunchStartupAction.LoadProject,
                arguments.LoadProjectPath,
                [],
                arguments.SaveProjectPath);
        }

        if (arguments.InitialPlaylistPaths.Count > 0)
        {
            return new ProjectLaunchActionPlan(
                ProjectLaunchStartupAction.LoadPlaylist,
                null,
                arguments.InitialPlaylistPaths,
                arguments.SaveProjectPath);
        }

        return new ProjectLaunchActionPlan(
            ProjectLaunchStartupAction.None,
            null,
            [],
            arguments.SaveProjectPath);
    }
}
