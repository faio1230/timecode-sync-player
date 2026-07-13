using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderUpdateSchedulerTests
{
    [Fact]
    public void RequestDispatch_SchedulesOnlyFirstCallbackUntilDispatchCompletes()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();
        scheduler.RequestDispatch().Should().BeFalse();

        RenderUpdateSchedulerStats stats = scheduler.ConsumeStats();
        stats.Requests.Should().Be(3);
        stats.CoalescedRequests.Should().Be(2);
    }

    [Fact]
    public void CompleteDispatch_RequestsAnotherDispatchWhenCallbacksArrivedWhileScheduled()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.RequestDispatch().Should().BeFalse();

        scheduler.CompleteDispatch().Should().BeTrue();
        scheduler.CompleteDispatch().Should().BeFalse();
    }

    [Fact]
    public void CompleteDispatch_AllowsFutureCallbackToScheduleAfterNoPendingWork()
    {
        var scheduler = new RenderUpdateScheduler();

        scheduler.RequestDispatch().Should().BeTrue();
        scheduler.CompleteDispatch().Should().BeFalse();

        scheduler.RequestDispatch().Should().BeTrue();
    }
}
