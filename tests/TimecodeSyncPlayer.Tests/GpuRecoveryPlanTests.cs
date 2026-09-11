using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class GpuRecoveryPlanTests
{
    [Fact]
    public void Steps_StartWithSpoutStop_EndWithSourceReconnect_AndKeepOrder()
    {
        GpuRecoveryPlan.Steps.Should().Equal(
            GpuRecoveryStep.StopSpoutWorker,
            GpuRecoveryStep.ReleaseLeasesAndDisposeComposeResources,
            GpuRecoveryStep.RecreateDevice,
            GpuRecoveryStep.RebuildComposeResources,
            GpuRecoveryStep.RecreateFullscreenSwapchain,
            GpuRecoveryStep.ReinitializeSpout,
            GpuRecoveryStep.ReconnectSources);

        GpuRecoveryPlan.Steps[0].Should().Be(GpuRecoveryStep.StopSpoutWorker, "Spout worker 停止が先");
        GpuRecoveryPlan.Steps[^1].Should().Be(GpuRecoveryStep.ReconnectSources, "ソース再接続が最後");
        GpuRecoveryPlan.Steps.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void ParseSimulatedDeviceLossSeconds_ReadsCommaSeparatedValues()
    {
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds("10,20").Should().Equal(10d, 20d);
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds("10").Should().Equal(10d);
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds(" 1.5 , 2 ").Should().Equal(1.5d, 2d);
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds("").Should().BeEmpty();
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds(null).Should().BeEmpty();
        OutputEngineSettings.ParseSimulatedDeviceLossSeconds("abc,-2").Should().BeEmpty();
    }
}
