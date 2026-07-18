using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class LtcDisplayStateFormatterTests
{
    [Fact]
    public void Format_WhenMonitoringSignalIsLost_ShowsNoSignalInMutedColor()
    {
        LtcDisplayState state = LtcDisplayStateFormatter.Format(
            isMonitoring: true,
            isSignalLost: true,
            normalFormatText: "fps: 25");

        state.FormatText.Should().Be("NO SIGNAL");
        state.TimecodeForeground.Should().Be("#666666");
    }

    [Fact]
    public void Format_WhenSignalIsReceiving_ShowsFormatInActiveColor()
    {
        LtcDisplayState state = LtcDisplayStateFormatter.Format(
            isMonitoring: true,
            isSignalLost: false,
            normalFormatText: "fps: 25");

        state.FormatText.Should().Be("fps: 25");
        state.TimecodeForeground.Should().Be("#55D86A");
    }

    [Fact]
    public void Format_WhenManuallyStopped_DoesNotShowSignalLossState()
    {
        LtcDisplayState state = LtcDisplayStateFormatter.Format(
            isMonitoring: false,
            isSignalLost: true,
            normalFormatText: "LTC 停止中");

        state.FormatText.Should().Be("LTC 停止中");
        state.TimecodeForeground.Should().Be("#55D86A");
    }
}
