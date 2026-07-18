using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalLossPauseReasonFormatterTests
{
    [Fact]
    public void Format_WhenPolicyOwnsPause_ReturnsSignalLossReason()
    {
        LtcSignalLossPauseReasonFormatter.Format(isPauseOwned: true)
            .Should().Be("信号断で停止中");
    }

    [Fact]
    public void Format_WhenPolicyDoesNotOwnPause_ReturnsEmpty()
    {
        LtcSignalLossPauseReasonFormatter.Format(isPauseOwned: false)
            .Should().BeEmpty();
    }
}
