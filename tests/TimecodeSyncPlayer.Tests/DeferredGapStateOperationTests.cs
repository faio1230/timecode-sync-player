using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class DeferredGapStateOperationTests
{
    [Fact]
    public void RunIfCurrent_ExpectedStateExecutesOperation()
    {
        GapState current = GapState.WaitingForFrameStep;
        int calls = 0;
        var operation = new DeferredGapStateOperation(
            GapState.WaitingForFrameStep,
            () => current,
            () => calls++);

        operation.RunIfCurrent();

        calls.Should().Be(1);
    }

    [Fact]
    public void RunIfCurrent_StateChangedSkipsStaleOperation()
    {
        GapState current = GapState.WaitingForFrameStep;
        int calls = 0;
        var operation = new DeferredGapStateOperation(
            GapState.WaitingForFrameStep,
            () => current,
            () => calls++);
        current = GapState.Inactive;

        operation.RunIfCurrent();

        calls.Should().Be(0);
    }
}
