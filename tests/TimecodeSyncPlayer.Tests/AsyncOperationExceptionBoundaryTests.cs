using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class AsyncOperationExceptionBoundaryTests
{
    [Fact]
    public async Task RunAsync_FailureIsReportedWithoutEscaping()
    {
        var expected = new InvalidOperationException("render failed");
        Exception? reported = null;

        Func<Task> act = () => AsyncOperationExceptionBoundary.RunAsync(
            () => Task.FromException(expected),
            exception => reported = exception);

        await act.Should().NotThrowAsync();
        reported.Should().BeSameAs(expected);
    }

    [Fact]
    public async Task RunAsync_SuccessDoesNotReportFailure()
    {
        int reports = 0;

        await AsyncOperationExceptionBoundary.RunAsync(
            () => Task.CompletedTask,
            _ => reports++);

        reports.Should().Be(0);
    }
}
