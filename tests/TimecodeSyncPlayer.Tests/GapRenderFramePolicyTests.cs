using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapRenderFramePolicyTests
{
    [Theory]
    [InlineData((int)GapState.EnteringFreeze, false)]
    [InlineData((int)GapState.EnteringFreeze, true)]
    [InlineData((int)GapState.WaitingForFrameStep, false)]
    [InlineData((int)GapState.WaitingForFrameStep, true)]
    [InlineData((int)GapState.FreezeComplete, false)]
    public void Decide_HoldsCurrentImageUntilConfirmedFreezeExists(int state, bool hasFrame)
    {
        GapRenderFramePolicy.Decide((GapState)state, GapBehavior.Freeze, hasFrame, 1920, 1080)
            .Should().Be(GapRenderFrameDecision.Hold);
    }

    [Fact]
    public void Decide_ReturnsBlack_ForBlackStates()
    {
        GapRenderFramePolicy.Decide(
            GapState.BlackFrameActive,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Black);

        GapRenderFramePolicy.Decide(
            GapState.ForceBlack,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Black);
    }

    [Fact]
    public void Decide_ReturnsGapFreeze_ForCompletedFreezeState()
    {
        GapRenderFramePolicy.Decide(
            GapState.FreezeComplete,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.GapFreeze);
    }

    [Fact]
    public void Decide_HoldsPublishedImage_WhileCapturing_EvenWhenOldCacheIsAvailable()
    {
        GapRenderFramePolicy.Decide(
            GapState.EnteringFreeze,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Hold);

        GapRenderFramePolicy.Decide(
            GapState.WaitingForFrameStep,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Hold);
    }

    [Theory]
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 0, 1080)]
    [InlineData(true, 1920, 0)]
    public void Decide_HoldsPublishedImage_WhileCapturing_WhenMetadataIsUnavailable(
        bool hasConfirmedFrame,
        int videoWidth,
        int videoHeight)
    {
        GapRenderFramePolicy.Decide(
            GapState.EnteringFreeze,
            GapBehavior.Freeze,
            hasConfirmedFrame,
            videoWidth,
            videoHeight).Should().Be(GapRenderFrameDecision.Hold);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenGapIsInactive()
    {
        GapRenderFramePolicy.Decide(
            GapState.Inactive,
            GapBehavior.Freeze,
            hasConfirmedFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.None);
    }
}
