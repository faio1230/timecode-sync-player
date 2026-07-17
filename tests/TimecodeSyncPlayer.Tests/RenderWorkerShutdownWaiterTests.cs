using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderWorkerShutdownWaiterTests
{
    [Fact]
    public void Wait_FaultedTaskReportsFailureWithoutThrowing()
    {
        var expected = new InvalidOperationException("render failed");
        Exception? reported = null;

        Action act = () => RenderWorkerShutdownWaiter.Wait(
            Task.FromException(expected),
            exception => reported = exception);

        act.Should().NotThrow();
        reported.Should().BeSameAs(expected);
    }

    [Fact]
    public void Wait_CompletedTaskDoesNotReportFailure()
    {
        int reports = 0;

        RenderWorkerShutdownWaiter.Wait(Task.CompletedTask, _ => reports++);

        reports.Should().Be(0);
    }

    [Fact]
    public void Wait_NullTaskDoesNotReportFailure()
    {
        int reports = 0;

        RenderWorkerShutdownWaiter.Wait(null, _ => reports++);

        reports.Should().Be(0);
    }
}
