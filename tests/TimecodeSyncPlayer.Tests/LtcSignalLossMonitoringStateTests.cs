using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class LtcSignalLossMonitoringStateTests
{
    [Fact]
    public void MarkStopped_WithException_KeepsSignalLossDetectionActive()
    {
        var state = new LtcSignalLossMonitoringState();
        state.MarkStarted();

        bool shouldResetPolicy = state.MarkStopped(new InvalidOperationException("device lost"));

        shouldResetPolicy.Should().BeFalse();
        state.IsDetectionActive(isReportedRunning: false).Should().BeTrue();
    }

    [Fact]
    public void MarkStopped_WithoutException_DeactivatesDetectionAndRequestsReset()
    {
        var state = new LtcSignalLossMonitoringState();
        state.MarkStarted();

        bool shouldResetPolicy = state.MarkStopped(exception: null);

        shouldResetPolicy.Should().BeTrue();
        state.IsDetectionActive(isReportedRunning: false).Should().BeFalse();
    }

    [Fact]
    public void MarkStarted_ClearsPreviousUnexpectedStop()
    {
        var state = new LtcSignalLossMonitoringState();
        state.MarkStopped(new InvalidOperationException("device lost"));

        state.MarkStarted();

        state.IsDetectionActive(isReportedRunning: false).Should().BeFalse();
        state.IsDetectionActive(isReportedRunning: true).Should().BeTrue();
    }
}
