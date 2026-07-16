using System.IO;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

[Collection("Project serializer state")]
public class WindowLoadedCoordinatorTests
{
    [Fact]
    public void Initialize_WhenSessionSucceedsRunsStartupSequenceInOrderAndSchedulesPlannedAction()
    {
        var calls = new List<string>();
        ProjectLaunchActionPlan? scheduledPlan = null;
        var arguments = new AppLaunchArguments(
            OpenPath: null,
            PlaylistPaths: [],
            LoadProjectPath: "show.tsp",
            SaveProjectPath: "saved.tsp");
        var coordinator = new WindowLoadedCoordinator(new WindowLoadedEffects(
            InitializeUi: () => calls.Add("InitializeUi"),
            ParseLaunchArguments: () => { calls.Add("ParseLaunchArguments"); return arguments; },
            InitializeSession: () => { calls.Add("InitializeSession"); return true; },
            ScheduleLaunchAction: plan =>
            {
                calls.Add("ScheduleLaunchAction");
                scheduledPlan = plan;
            }));

        coordinator.Initialize();

        calls.Should().Equal(
            "InitializeUi",
            "ParseLaunchArguments",
            "InitializeSession",
            "ScheduleLaunchAction");
        scheduledPlan.Should().Be(new ProjectLaunchActionPlan(
            ProjectLaunchStartupAction.LoadProject,
            "show.tsp",
            [],
            "saved.tsp"));
    }

    [Fact]
    public void Initialize_WhenSessionFailsStopsBeforePlanningAndSchedulingLaunchAction()
    {
        var calls = new List<string>();
        var coordinator = new WindowLoadedCoordinator(new WindowLoadedEffects(
            InitializeUi: () => calls.Add("InitializeUi"),
            ParseLaunchArguments: () =>
            {
                calls.Add("ParseLaunchArguments");
                return new AppLaunchArguments(null, [], null, null);
            },
            InitializeSession: () => { calls.Add("InitializeSession"); return false; },
            ScheduleLaunchAction: _ => calls.Add("ScheduleLaunchAction")));

        coordinator.Initialize();

        calls.Should().Equal(
            "InitializeUi",
            "ParseLaunchArguments",
            "InitializeSession");
    }

    [Fact]
    public async Task Initialize_WithCorruptProjectRoutesLoadFailureWithoutEscapingStartupAction()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"TimecodeSyncPlayer.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "corrupt.tsp");
        await File.WriteAllTextAsync(path, "{\"version\":1");
        try
        {
            Func<Task>? scheduledStartup = null;
            Exception? capturedFailure = null;
            var executor = new ProjectLaunchActionExecutor(
                async projectPath => _ = await ProjectSerializer.LoadAsync(projectPath),
                _ => Task.CompletedTask,
                _ => Task.CompletedTask);
            var scheduler = new ProjectLaunchActionScheduler(
                scheduleStartup: action => scheduledStartup = action,
                scheduleSave: _ => { },
                delayAsync: _ => Task.CompletedTask,
                logStartupFailure: (exception, _) => capturedFailure = exception,
                logSaveCompleted: _ => { },
                logSaveFailure: (_, _) => { });
            var coordinator = new WindowLoadedCoordinator(new WindowLoadedEffects(
                InitializeUi: () => { },
                ParseLaunchArguments: () => new AppLaunchArguments(null, [], path, null),
                InitializeSession: () => true,
                ScheduleLaunchAction: plan => scheduler.Schedule(plan, executor, TimeSpan.Zero)));

            coordinator.Initialize();
            await scheduledStartup!();

            capturedFailure.Should().BeOfType<System.Text.Json.JsonException>();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
