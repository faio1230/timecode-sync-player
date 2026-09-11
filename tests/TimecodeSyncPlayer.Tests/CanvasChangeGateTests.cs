using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

/// <summary>段階 4.3: キャンバス変更可否の表（5 行）。</summary>
public class CanvasChangeGateTests
{
    [Fact]
    public void StoppedWithoutLtcFollow_AllowsChange()
    {
        CanvasChangeGate.CanChange(isPlaying: false, isLtcFollowing: false, isRenderingFrozenOnly: false)
            .Should().BeTrue();
        CanvasChangeGate.DescribeReason(false, false, false).Should().BeNull();
    }

    [Fact]
    public void Playing_BlocksChangeRegardlessOfSpeed()
    {
        // 速度は引数に取らない = 速度に関わらず再生中は不可。
        CanvasChangeGate.CanChange(isPlaying: true, isLtcFollowing: false, isRenderingFrozenOnly: false)
            .Should().BeFalse();
        CanvasChangeGate.DescribeReason(true, false, false).Should().NotBeNull();
    }

    [Fact]
    public void LtcFollowingWithSignal_BlocksChange()
    {
        CanvasChangeGate.CanChange(isPlaying: false, isLtcFollowing: true, isRenderingFrozenOnly: false)
            .Should().BeFalse();
        CanvasChangeGate.DescribeReason(false, true, false).Should().NotBeNull();
    }

    [Fact]
    public void LtcFollowingDuringSignalLoss_BlocksChange()
    {
        // 信号ロス中も SyncEnabled は true のため isLtcFollowing=true（追従再開があり得る）。
        CanvasChangeGate.CanChange(isPlaying: false, isLtcFollowing: true, isRenderingFrozenOnly: false)
            .Should().BeFalse();
    }

    [Fact]
    public void FrozenOrPreparingFrame_BlocksChange()
    {
        CanvasChangeGate.CanChange(isPlaying: false, isLtcFollowing: false, isRenderingFrozenOnly: true)
            .Should().BeFalse();
        CanvasChangeGate.DescribeReason(false, false, true).Should().NotBeNull();
    }
}
