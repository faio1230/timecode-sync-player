using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectFileCoordinatorTests
{
    [Fact]
    public async Task SaveAsync_ForwardsSelectedPathAndPreservesSaveThenLogOrder()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(
            calls,
            saveAsync: path =>
            {
                calls.Add($"save:{path}");
                return Task.CompletedTask;
            });

        await coordinator.SaveAsync("show.tsp");

        calls.Should().Equal("save:show.tsp", "saved:show.tsp");
    }

    [Fact]
    public async Task SaveAsync_MissingPathDoesNotInvokeEffects()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(calls);

        await coordinator.SaveAsync(null);

        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAsync_ForwardsLoadedProjectThenLogsPath()
    {
        var calls = new List<string>();
        var project = new ProjectData();
        ProjectData? applied = null;
        var coordinator = CreateCoordinator(
            calls,
            loadAsync: path =>
            {
                calls.Add($"load:{path}");
                return Task.FromResult<ProjectData?>(project);
            },
            applyProject: value =>
            {
                applied = value;
                calls.Add("apply");
            });

        await coordinator.LoadAsync("show.tsp");

        applied.Should().BeSameAs(project);
        calls.Should().Equal("load:show.tsp", "apply", "loaded:show.tsp");
    }

    [Fact]
    public async Task LoadAsync_InvalidProjectInvokesOnlyInvalidEffectAfterLoad()
    {
        var calls = new List<string>();
        var coordinator = CreateCoordinator(
            calls,
            loadAsync: path =>
            {
                calls.Add($"load:{path}");
                return Task.FromResult<ProjectData?>(null);
            });

        await coordinator.LoadAsync("bad.tsp");

        calls.Should().Equal("load:bad.tsp", "invalid");
    }

    private static ProjectFileCoordinator CreateCoordinator(
        List<string> calls,
        Func<string, Task>? saveAsync = null,
        Func<string, Task<ProjectData?>>? loadAsync = null,
        Action<ProjectData>? applyProject = null)
    {
        return new ProjectFileCoordinator(
            new ProjectFileActionRunner(),
            new ProjectFileEffects(
                SaveAsync: saveAsync ?? (_ =>
                {
                    calls.Add("save");
                    return Task.CompletedTask;
                }),
                LogSaved: path => calls.Add($"saved:{path}"),
                HandleSaveFailure: _ => calls.Add("save-failure"),
                LoadAsync: loadAsync ?? (_ => Task.FromResult<ProjectData?>(new ProjectData())),
                ApplyProject: applyProject ?? (_ => calls.Add("apply")),
                LogLoaded: path => calls.Add($"loaded:{path}"),
                HandleInvalidProject: () => calls.Add("invalid"),
                HandleLoadFailure: _ => calls.Add("load-failure")));
    }
}
