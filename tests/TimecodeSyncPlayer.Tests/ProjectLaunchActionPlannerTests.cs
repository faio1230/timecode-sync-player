using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectLaunchActionPlannerTests
{
    [Fact]
    public void Decide_ReturnsLoadProject_WhenLoadProjectPathIsPresent()
    {
        var args = new AppLaunchArguments(null, [], @"C:\projects\show.tsp", null);

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.StartupAction.Should().Be(ProjectLaunchStartupAction.LoadProject);
        plan.LoadProjectPath.Should().Be(@"C:\projects\show.tsp");
        plan.InitialPlaylistPaths.Should().BeEmpty();
    }

    [Fact]
    public void Decide_ReturnsLoadPlaylist_WhenInitialPlaylistPathsArePresent()
    {
        var args = new AppLaunchArguments(@"C:\media\one.mp4", [@"C:\media\two.mp4"], null, null);

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.StartupAction.Should().Be(ProjectLaunchStartupAction.LoadPlaylist);
        plan.InitialPlaylistPaths.Should().Equal(@"C:\media\one.mp4", @"C:\media\two.mp4");
    }

    [Fact]
    public void Decide_ReturnsLoadProject_WhenOpenPathIsTspProject()
    {
        var args = new AppLaunchArguments(@"C:\projects\show.tsp", [], null, null);

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.StartupAction.Should().Be(ProjectLaunchStartupAction.LoadProject);
        plan.LoadProjectPath.Should().Be(@"C:\projects\show.tsp");
        plan.InitialPlaylistPaths.Should().BeEmpty();
    }

    [Fact]
    public void Decide_ReturnsNone_WhenNoStartupInputIsPresent()
    {
        var args = new AppLaunchArguments(null, [], null, null);

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.StartupAction.Should().Be(ProjectLaunchStartupAction.None);
    }

    [Fact]
    public void Decide_PreservesSaveProjectPath()
    {
        var args = new AppLaunchArguments(null, [], null, @"C:\projects\out.tsp");

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.SaveProjectPath.Should().Be(@"C:\projects\out.tsp");
    }
}
