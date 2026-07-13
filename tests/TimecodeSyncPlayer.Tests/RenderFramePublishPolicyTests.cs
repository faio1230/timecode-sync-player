using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class RenderFramePublishPolicyTests
{
    [Fact]
    public void Decide_AllowsPublish_WhenRenderSucceededAndSizeIsDisplayable()
    {
        RenderFramePublishDecision decision = RenderFramePublishPolicy.Decide(renderResultCode: 0, hasDisplayableVideoSize: true);

        decision.ShouldPublish.Should().BeTrue();
        decision.SkipReason.Should().Be(RenderFramePublishSkipReason.None);
    }

    [Fact]
    public void Decide_Skips_WhenRenderFailed()
    {
        RenderFramePublishDecision decision = RenderFramePublishPolicy.Decide(renderResultCode: -1, hasDisplayableVideoSize: true);

        decision.ShouldPublish.Should().BeFalse();
        decision.SkipReason.Should().Be(RenderFramePublishSkipReason.RenderFailed);
    }

    [Fact]
    public void Decide_Skips_WhenSizeIsFallbackOnly()
    {
        RenderFramePublishDecision decision = RenderFramePublishPolicy.Decide(renderResultCode: 0, hasDisplayableVideoSize: false);

        decision.ShouldPublish.Should().BeFalse();
        decision.SkipReason.Should().Be(RenderFramePublishSkipReason.NoDisplayableVideoSize);
    }
}
