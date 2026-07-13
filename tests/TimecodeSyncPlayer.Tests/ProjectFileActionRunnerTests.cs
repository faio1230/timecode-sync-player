using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectFileActionRunnerTests
{
    [Fact]
    public async Task SaveAsync_DoesNothing_WhenPathIsMissing()
    {
        bool saved = false;
        var runner = new ProjectFileActionRunner();

        await runner.SaveAsync(
            selectedPath: null,
            saveAsync: _ =>
            {
                saved = true;
                return Task.CompletedTask;
            },
            logSaved: _ => { },
            handleFailure: _ => { });

        saved.Should().BeFalse();
    }

    [Fact]
    public async Task SaveAsync_SavesAndLogs_WhenPathIsSelected()
    {
        var calls = new List<string>();
        var runner = new ProjectFileActionRunner();

        await runner.SaveAsync(
            selectedPath: "show.tsp",
            saveAsync: path =>
            {
                calls.Add($"save:{path}");
                return Task.CompletedTask;
            },
            logSaved: path => calls.Add($"log:{path}"),
            handleFailure: _ => calls.Add("failure"));

        calls.Should().Equal("save:show.tsp", "log:show.tsp");
    }

    [Fact]
    public async Task SaveAsync_HandlesFailure_WhenSaveThrows()
    {
        var expected = new InvalidOperationException("save failed");
        Exception? captured = null;
        var runner = new ProjectFileActionRunner();

        await runner.SaveAsync(
            selectedPath: "show.tsp",
            saveAsync: _ => throw expected,
            logSaved: _ => { },
            handleFailure: ex => captured = ex);

        captured.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task LoadAsync_DoesNothing_WhenPathIsMissing()
    {
        bool loaded = false;
        var runner = new ProjectFileActionRunner();

        await runner.LoadAsync(
            selectedPath: null,
            loadAsync: _ =>
            {
                loaded = true;
                return Task.FromResult<ProjectData?>(null);
            },
            applyProject: _ => { },
            logLoaded: _ => { },
            handleInvalidProject: () => { },
            handleFailure: _ => { });

        loaded.Should().BeFalse();
    }

    [Fact]
    public async Task LoadAsync_AppliesAndLogs_WhenProjectIsLoaded()
    {
        var project = new ProjectData();
        var calls = new List<string>();
        var runner = new ProjectFileActionRunner();

        await runner.LoadAsync(
            selectedPath: "show.tsp",
            loadAsync: path =>
            {
                calls.Add($"load:{path}");
                return Task.FromResult<ProjectData?>(project);
            },
            applyProject: loaded =>
            {
                loaded.Should().BeSameAs(project);
                calls.Add("apply");
            },
            logLoaded: path => calls.Add($"log:{path}"),
            handleInvalidProject: () => calls.Add("invalid"),
            handleFailure: _ => calls.Add("failure"));

        calls.Should().Equal("load:show.tsp", "apply", "log:show.tsp");
    }

    [Fact]
    public async Task LoadAsync_HandlesInvalidProject_WhenLoaderReturnsNull()
    {
        var calls = new List<string>();
        var runner = new ProjectFileActionRunner();

        await runner.LoadAsync(
            selectedPath: "bad.tsp",
            loadAsync: _ => Task.FromResult<ProjectData?>(null),
            applyProject: _ => calls.Add("apply"),
            logLoaded: _ => calls.Add("log"),
            handleInvalidProject: () => calls.Add("invalid"),
            handleFailure: _ => calls.Add("failure"));

        calls.Should().Equal("invalid");
    }

    [Fact]
    public async Task LoadAsync_HandlesFailure_WhenLoadThrows()
    {
        var expected = new InvalidOperationException("load failed");
        Exception? captured = null;
        var runner = new ProjectFileActionRunner();

        await runner.LoadAsync(
            selectedPath: "bad.tsp",
            loadAsync: _ => throw expected,
            applyProject: _ => { },
            logLoaded: _ => { },
            handleInvalidProject: () => { },
            handleFailure: ex => captured = ex);

        captured.Should().BeSameAs(expected);
    }
}
