using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderFramePipelineGateTests
{
    [Fact]
    public async Task RunAsync_DoesNotOverlapOperations()
    {
        var gate = new RenderFramePipelineGate();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        int activeOperations = 0;
        int maximumActiveOperations = 0;

        Task first = gate.RunAsync(async () =>
        {
            int active = Interlocked.Increment(ref activeOperations);
            maximumActiveOperations = Math.Max(maximumActiveOperations, active);
            firstStarted.Set();
            await Task.Run(releaseFirst.Wait);
            Interlocked.Decrement(ref activeOperations);
        });
        firstStarted.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();

        Task second = gate.RunAsync(() =>
        {
            int active = Interlocked.Increment(ref activeOperations);
            maximumActiveOperations = Math.Max(maximumActiveOperations, active);
            Interlocked.Decrement(ref activeOperations);
            return Task.CompletedTask;
        });

        await Task.Delay(50);
        second.IsCompleted.Should().BeFalse();
        releaseFirst.Set();
        await Task.WhenAll(first, second);

        maximumActiveOperations.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_PreservesSubmissionOrder()
    {
        var gate = new RenderFramePipelineGate();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var calls = new List<string>();

        Task first = gate.RunAsync(async () =>
        {
            calls.Add("first-start");
            firstStarted.Set();
            await Task.Run(releaseFirst.Wait);
            calls.Add("first-end");
        });
        firstStarted.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue();
        Task second = gate.RunAsync(() =>
        {
            calls.Add("second");
            return Task.CompletedTask;
        });

        releaseFirst.Set();
        await Task.WhenAll(first, second);

        calls.Should().Equal("first-start", "first-end", "second");
    }
}
