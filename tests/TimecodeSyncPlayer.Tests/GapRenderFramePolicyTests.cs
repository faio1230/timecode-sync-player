using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// 段 3: Freeze 指定の FreezeComplete は必ず GapFreeze を要求し、最終フレームの保存は
/// GPU 合成層（ComposeLayer.SaveFreeze）が進入時に行う。
/// </summary>
public class GapRenderFramePolicyTests
{
    [Theory]
    [InlineData((int)GapState.EnteringFreeze)]
    [InlineData((int)GapState.WaitingForFrameStep)]
    public void Decide_HoldsCurrentImage_WhileCaptureIsPending(int state)
    {
        GapRenderFramePolicy.Decide((GapState)state, GapBehavior.Freeze)
            .Should().Be(GapRenderFrameDecision.Hold);
    }

    [Fact]
    public void Decide_ReturnsBlack_ForBlackStates()
    {
        GapRenderFramePolicy.Decide(GapState.BlackFrameActive, GapBehavior.Freeze)
            .Should().Be(GapRenderFrameDecision.Black);
        GapRenderFramePolicy.Decide(GapState.ForceBlack, GapBehavior.Freeze)
            .Should().Be(GapRenderFrameDecision.Black);
    }

    [Fact]
    public void Decide_ReturnsGapFreeze_ForCompletedFreezeState()
    {
        GapRenderFramePolicy.Decide(GapState.FreezeComplete, GapBehavior.Freeze)
            .Should().Be(GapRenderFrameDecision.GapFreeze);
    }

    [Fact]
    public void Decide_ReturnsBlack_ForCompletedFreezeState_WhenBehaviorIsBlack()
    {
        GapRenderFramePolicy.Decide(GapState.FreezeComplete, GapBehavior.Black)
            .Should().Be(GapRenderFrameDecision.Black);
        GapRenderFramePolicy.Decide(GapState.EnteringFreeze, GapBehavior.Black)
            .Should().Be(GapRenderFrameDecision.Black);
        GapRenderFramePolicy.Decide(GapState.WaitingForFrameStep, GapBehavior.Black)
            .Should().Be(GapRenderFrameDecision.Black);
    }

    [Fact]
    public void Decide_ReturnsNone_WhenGapIsInactive()
    {
        GapRenderFramePolicy.Decide(GapState.Inactive, GapBehavior.Freeze)
            .Should().Be(GapRenderFrameDecision.None);
    }
}
