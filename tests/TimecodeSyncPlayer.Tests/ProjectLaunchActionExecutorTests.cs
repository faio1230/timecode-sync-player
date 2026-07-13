using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectLaunchActionExecutorTests
{
    [Fact]
    public async Task ExecuteStartupAsync_LoadsProject_WhenPlanRequestsProjectLoad()
    {
        var calls = new List<string>();
        var executor = new ProjectLaunchActionExecutor(
            loadProjectAsync: path =>
            {
                calls.Add($"project:{path}");
                return Task.CompletedTask;
            },
            loadPlaylistAsync: paths =>
            {
                calls.Add($"playlist:{string.Join(",", paths)}");
                return Task.CompletedTask;
            },
            saveProjectAsync: path =>
            {
                calls.Add($"save:{path}");
                return Task.CompletedTask;
            });
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.LoadProject, "show.tsp", [], null);

        await executor.ExecuteStartupAsync(plan);

        calls.Should().Equal("project:show.tsp");
    }

    [Fact]
    public async Task ExecuteStartupAsync_LoadsPlaylist_WhenPlanRequestsPlaylistLoad()
    {
        var calls = new List<string>();
        var executor = new ProjectLaunchActionExecutor(
            _ => Task.CompletedTask,
            paths =>
            {
                calls.Add($"playlist:{string.Join(",", paths)}");
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask);
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.LoadPlaylist, null, ["a.mp4", "b.mp4"], null);

        await executor.ExecuteStartupAsync(plan);

        calls.Should().Equal("playlist:a.mp4,b.mp4");
    }

    [Fact]
    public async Task ExecuteSaveAsync_Saves_WhenSavePathExists()
    {
        var calls = new List<string>();
        var executor = new ProjectLaunchActionExecutor(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            path =>
            {
                calls.Add($"save:{path}");
                return Task.CompletedTask;
            });
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.None, null, [], "out.tsp");

        await executor.ExecuteSaveAsync(plan);

        calls.Should().Equal("save:out.tsp");
    }

    [Fact]
    public async Task ExecuteSaveAsync_DoesNothing_WhenSavePathIsMissing()
    {
        var called = false;
        var executor = new ProjectLaunchActionExecutor(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ =>
            {
                called = true;
                return Task.CompletedTask;
            });
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.None, null, [], null);

        await executor.ExecuteSaveAsync(plan);

        called.Should().BeFalse();
    }
}
