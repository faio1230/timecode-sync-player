using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class DeferredGapFrameOperationTests
{
    [Fact]
    public void RunIfCurrent_SameDecisionExecutesOperation()
    {
        GapRenderFrameDecision current = GapRenderFrameDecision.Black;
        int calls = 0;
        var operation = new DeferredGapFrameOperation(
            GapRenderFrameDecision.Black,
            () => current,
            () => calls++);

        operation.RunIfCurrent();

        calls.Should().Be(1);
    }

    [Fact]
    public void RunIfCurrent_DifferentDecisionSkipsStaleOperation()
    {
        GapRenderFrameDecision current = GapRenderFrameDecision.Black;
        int calls = 0;
        var operation = new DeferredGapFrameOperation(
            GapRenderFrameDecision.Black,
            () => current,
            () => calls++);
        current = GapRenderFrameDecision.None;

        operation.RunIfCurrent();

        calls.Should().Be(0);
    }
}
