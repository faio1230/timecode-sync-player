using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class GapRenderFramePolicyTests
{
    [Fact]
    public void Decide_ReturnsBlack_ForBlackStates()
    {
        GapRenderFramePolicy.Decide(
            GapState.BlackFrameActive,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Black);

        GapRenderFramePolicy.Decide(
            GapState.ForceBlack,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.Black);
    }

    [Fact]
    public void Decide_ReturnsGapFreeze_ForCompletedFreezeState()
    {
        GapRenderFramePolicy.Decide(
            GapState.FreezeComplete,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.GapFreeze);
    }

    [Fact]
    public void Decide_ReturnsBufferedFreeze_WhileCapturing_WhenFrameIsAvailable()
    {
        GapRenderFramePolicy.Decide(
            GapState.EnteringFreeze,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.GapFreeze);

        GapRenderFramePolicy.Decide(
            GapState.WaitingForFrameStep,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.GapFreeze);
    }

    [Theory]
    [InlineData(false, 1920, 1080)]
    [InlineData(true, 0, 1080)]
    [InlineData(true, 1920, 0)]
    public void Decide_FallsBackToBlack_WhileCapturing_WhenFreezeFrameCannotRender(
        bool hasFrozenFrame,
        int videoWidth,
        int videoHeight)
    {
        GapRenderFramePolicy.Decide(
            GapState.EnteringFreeze,
            GapBehavior.Freeze,
            hasFrozenFrame,
            videoWidth,
            videoHeight).Should().Be(GapRenderFrameDecision.Black);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenGapIsInactive()
    {
        GapRenderFramePolicy.Decide(
            GapState.Inactive,
            GapBehavior.Freeze,
            hasFrozenFrame: true,
            videoWidth: 1920,
            videoHeight: 1080).Should().Be(GapRenderFrameDecision.None);
    }
}
