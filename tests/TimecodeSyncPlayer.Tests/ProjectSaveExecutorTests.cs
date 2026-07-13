using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectSaveExecutorTests
{
    [Fact]
    public async Task SaveAsync_DelegatesToSaveFunction()
    {
        var calls = new List<string>();
        var executor = new ProjectSaveExecutor((path, syncMode, gapBehavior) =>
        {
            calls.Add($"{path}:{syncMode}:{gapBehavior}");
            return Task.CompletedTask;
        });

        await executor.SaveAsync("show.tsp", SyncMode.Continue, GapBehavior.Freeze);

        calls.Should().Equal("show.tsp:Continue:Freeze");
    }
}
