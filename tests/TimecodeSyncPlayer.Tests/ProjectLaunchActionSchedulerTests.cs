using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ProjectLaunchActionSchedulerTests
{
    [Fact]
    public async Task Schedule_RunsStartupAction_WhenPlanRequestsStartup()
    {
        Func<Task>? scheduledStartup = null;
        var calls = new List<string>();
        var scheduler = CreateScheduler(scheduleStartup: action => scheduledStartup = action);
        var executor = new ProjectLaunchActionExecutor(
            path =>
            {
                calls.Add($"load:{path}");
                return Task.CompletedTask;
            },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.LoadProject, "show.tsp", [], null);

        scheduler.Schedule(plan, executor, TimeSpan.FromSeconds(3));
        scheduledStartup.Should().NotBeNull();
        await scheduledStartup!();

        calls.Should().Equal("load:show.tsp");
    }

    [Fact]
    public void Schedule_DoesNotQueueStartup_WhenPlanHasNoStartupAction()
    {
        var startupQueued = false;
        var scheduler = CreateScheduler(scheduleStartup: _ => startupQueued = true);
        var executor = CreateNoOpExecutor();
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.None, null, [], null);

        scheduler.Schedule(plan, executor, TimeSpan.FromSeconds(3));

        startupQueued.Should().BeFalse();
    }

    [Fact]
    public async Task Schedule_DelaysThenRunsSave_WhenSavePathExists()
    {
        Func<Task>? scheduledSave = null;
        var calls = new List<string>();
        var scheduler = CreateScheduler(
            scheduleSave: action => scheduledSave = action,
            delayAsync: delay =>
            {
                calls.Add($"delay:{delay.TotalMilliseconds}");
                return Task.CompletedTask;
            },
            logSaveCompleted: path => calls.Add($"saved:{path}"));
        var executor = new ProjectLaunchActionExecutor(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            path =>
            {
                calls.Add($"save:{path}");
                return Task.CompletedTask;
            });
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.None, null, [], "out.tsp");

        scheduler.Schedule(plan, executor, TimeSpan.FromMilliseconds(3000));
        scheduledSave.Should().NotBeNull();
        await scheduledSave!();

        calls.Should().Equal("delay:3000", "save:out.tsp", "saved:out.tsp");
    }

    [Fact]
    public async Task Schedule_LogsStartupFailure_WhenStartupActionThrows()
    {
        Func<Task>? scheduledStartup = null;
        var failures = new List<(Exception Exception, ProjectLaunchActionPlan Plan)>();
        var scheduler = CreateScheduler(
            scheduleStartup: action => scheduledStartup = action,
            logStartupFailure: (ex, plan) => failures.Add((ex, plan)));
        var expected = new InvalidOperationException("boom");
        var executor = new ProjectLaunchActionExecutor(
            _ => throw expected,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
        var plan = new ProjectLaunchActionPlan(ProjectLaunchStartupAction.LoadProject, "bad.tsp", [], null);

        scheduler.Schedule(plan, executor, TimeSpan.FromSeconds(3));
        scheduledStartup.Should().NotBeNull();
        await scheduledStartup!();

        failures.Should().ContainSingle()
            .Which.Should().Be((expected, plan));
    }

    private static ProjectLaunchActionScheduler CreateScheduler(
        Action<Func<Task>>? scheduleStartup = null,
        Action<Func<Task>>? scheduleSave = null,
        Func<TimeSpan, Task>? delayAsync = null,
        Action<Exception, ProjectLaunchActionPlan>? logStartupFailure = null,
        Action<string>? logSaveCompleted = null,
        Action<Exception, string>? logSaveFailure = null)
    {
        return new ProjectLaunchActionScheduler(
            scheduleStartup ?? (_ => { }),
            scheduleSave ?? (_ => { }),
            delayAsync ?? (_ => Task.CompletedTask),
            logStartupFailure ?? ((_, _) => { }),
            logSaveCompleted ?? (_ => { }),
            logSaveFailure ?? ((_, _) => { }));
    }

    private static ProjectLaunchActionExecutor CreateNoOpExecutor()
    {
        return new ProjectLaunchActionExecutor(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask);
    }
}
