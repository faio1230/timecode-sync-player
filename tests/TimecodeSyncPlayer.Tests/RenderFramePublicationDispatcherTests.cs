using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class RenderFramePublicationDispatcherTests
{
    [Fact]
    public void Execute_NoGapPublishesNormalFrameThenRunsCompletion()
    {
        var calls = new List<string>();

        RenderFramePublicationDispatcher.Execute(
            GapRenderFrameDecision.None,
            publishNormalFrame: () => calls.Add("publish"),
            captureWithoutPublishing: () => calls.Add("capture"),
            afterFrameProcessed: () => calls.Add("after"));

        calls.Should().Equal("publish", "after");
    }

    [Theory]
    [InlineData((int)GapRenderFrameDecision.Black)]
    [InlineData((int)GapRenderFrameDecision.GapFreeze)]
    public void Execute_ActiveGapCapturesWithoutPublishingThenRunsCompletion(
        int decisionValue)
    {
        var calls = new List<string>();
        var decision = (GapRenderFrameDecision)decisionValue;

        RenderFramePublicationDispatcher.Execute(
            decision,
            publishNormalFrame: () => calls.Add("publish"),
            captureWithoutPublishing: () => calls.Add("capture"),
            afterFrameProcessed: () => calls.Add("after"));

        calls.Should().Equal("capture", "after");
    }
}
