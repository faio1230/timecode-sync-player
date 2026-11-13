using FluentAssertions;

namespace Erytheia.Tests;

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

    [Theory]
    [InlineData(@"C:\projects\show.tsp")]
    [InlineData(@"C:\projects\show.erytheia")]
    [InlineData(@"C:\projects\show.ERYTHEIA")]
    public void Decide_ReturnsLoadProject_WhenOpenPathIsProject(string path)
    {
        // v0.6.7: 新しい拡張子 .erytheia と、v0.6.6 までの .tsp のどちらもプロジェクトとして開く。
        var args = new AppLaunchArguments(path, [], null, null);

        ProjectLaunchActionPlan plan = ProjectLaunchActionPlanner.Decide(args);

        plan.StartupAction.Should().Be(ProjectLaunchStartupAction.LoadProject);
        plan.LoadProjectPath.Should().Be(path);
        plan.InitialPlaylistPaths.Should().BeEmpty();
    }

    [Fact]
    public void ProjectDialog_DefaultsToTheNewExtensionAndAcceptsTheOldOne()
    {
        ProjectFileExtensions.DialogFilter.Should().Be("ERYTHEIA プロジェクト (*.erytheia;*.tsp)|*.erytheia;*.tsp");
        ProjectFileExtensions.DialogDefaultExtension.Should().Be("erytheia");
        ProjectFileExtensions.IsProjectFile(@"C:\media\clip.mp4").Should().BeFalse();
        ProjectFileExtensions.IsProjectFile(null).Should().BeFalse();
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
