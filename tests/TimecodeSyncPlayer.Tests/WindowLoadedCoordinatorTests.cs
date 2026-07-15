using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

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
}
