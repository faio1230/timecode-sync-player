using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalLossPauseReasonFormatterTests
{
    [Fact]
    public void Format_WhenPolicyOwnsPause_ReturnsSignalLossReason()
    {
        LtcSignalLossPauseReasonFormatter.Format(isPauseOwned: true, LtcSignalLossReason.SignalLoss)
            .Should().Be("信号断で停止中");
    }

    [Fact]
    public void Format_WhenPolicyOwnsPauseForHeldTimecode_ReturnsHeldReason()
    {
        LtcSignalLossPauseReasonFormatter.Format(isPauseOwned: true, LtcSignalLossReason.TimecodeHeld)
            .Should().Be("タイムコード停止で停止中");
    }

    [Fact]
    public void Format_WhenPolicyDoesNotOwnPause_ReturnsEmpty()
    {
        LtcSignalLossPauseReasonFormatter.Format(isPauseOwned: false, LtcSignalLossReason.TimecodeHeld)
            .Should().BeEmpty();
    }
}
