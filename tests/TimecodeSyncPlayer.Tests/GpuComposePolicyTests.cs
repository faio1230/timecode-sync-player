using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// GPU 合成経路の共通規則。mpv スナップショットのテストとは独立に置く
/// （mpv 経路の削除後も残る）。
/// </summary>
public class GpuComposePolicyTests
{
    [Fact]
    public void TimelineOutputMailbox_KeepsOnlyLatestState()
    {
        var mailbox = new TimelineOutputMailbox();
        mailbox.Take().Should().BeNull();
        var first = TimelineOutputState.Default with { Gap = OutputGapMode.Black };
        var second = TimelineOutputState.Default with { Gap = OutputGapMode.GapFreeze, PositionSeconds = 3.5 };
        mailbox.Publish(first);
        mailbox.Publish(second);
        mailbox.Take().Should().Be(second);
        mailbox.Take().Should().BeNull();
    }

    [Theory]
    [InlineData((int)OutputGapMode.None, true, false, false, (int)LayerAction.DrawAcquired)]
    [InlineData((int)OutputGapMode.None, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.None, false, false, false, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.Hold, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.Hold, false, false, false, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.Black, true, true, true, (int)LayerAction.DrawBlack)]
    [InlineData((int)OutputGapMode.GapFreeze, false, false, true, (int)LayerAction.DrawFrozen)]
    [InlineData((int)OutputGapMode.GapFreeze, false, true, false, (int)LayerAction.DrawHeld)]
    [InlineData((int)OutputGapMode.GapFreeze, false, false, false, (int)LayerAction.DrawBlack)]
    public void ComposeLayerPolicy_HoldsWithoutInsertingBlackForNotReady(
        int gap, bool acquired, bool held, bool frozen, int expected)
    {
        ComposeLayerPolicy.Decide((OutputGapMode)gap, acquired, held, frozen).Should().Be((LayerAction)expected);
    }
}
