using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderThreadExecutorTests
{
    [Fact]
    public async Task InvokeAsync_RunsEveryOperationOnOneDedicatedThread()
    {
        int callingThread = Environment.CurrentManagedThreadId;
        using var executor = new RenderThreadExecutor();

        int first = await executor.InvokeAsync(() => Environment.CurrentManagedThreadId);
        int second = await executor.InvokeAsync(() => Environment.CurrentManagedThreadId);
        int third = await executor.InvokeAsync(() => Environment.CurrentManagedThreadId);

        first.Should().NotBe(callingThread);
        new[] { first, second, third }.Should().OnlyContain(id => id == first);
    }

    [Fact]
    public async Task InvokeAsync_SerializesQueuedOperationsInSubmissionOrder()
    {
        using var executor = new RenderThreadExecutor();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = new List<string>();

        Task first = executor.InvokeAsync(() =>
        {
            calls.Add("first-start");
            firstStarted.Set();
            releaseFirst.Wait();
            calls.Add("first-end");
        });
        firstStarted.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Task second = executor.InvokeAsync(() => calls.Add("second"));
        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();

        releaseFirst.Set();
        await Task.WhenAll(first, second);

        calls.Should().Equal("first-start", "first-end", "second");
    }

    [Fact]
    public async Task InvokeAsync_AfterDisposeThrowsObjectDisposedException()
    {
        var executor = new RenderThreadExecutor();
        executor.Dispose();

        Func<Task> act = async () => await executor.InvokeAsync(() => { });

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Dispose_WaitsForRunningAndQueuedOperations()
    {
        var executor = new RenderThreadExecutor();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = new List<string>();

        Task first = executor.InvokeAsync(() =>
        {
            calls.Add("first-start");
            firstStarted.Set();
            releaseFirst.Wait();
            calls.Add("first-end");
        });
        firstStarted.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        Task second = executor.InvokeAsync(() => calls.Add("second"));

        Task dispose = Task.Run(executor.Dispose);
        await Task.Delay(50);
        dispose.IsCompleted.Should().BeFalse();

        releaseFirst.Set();
        await Task.WhenAll(first, second, dispose);
        calls.Should().Equal("first-start", "first-end", "second");
    }
}
