using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class SpoutOutputPolicyTests
{
    [Theory]
    [InlineData(OutputBackend.Cpu, true)]
    [InlineData(OutputBackend.Gpu, false)]
    public void InitializeCpuSpout_OnlyForCpuBackend(OutputBackend backend, bool expected)
        => SpoutOutputPolicy.InitializeCpuSpout(backend).Should().Be(expected);

    [Theory]
    [InlineData(OutputBackend.Cpu, true, true)]
    [InlineData(OutputBackend.Cpu, false, false)]
    [InlineData(OutputBackend.Gpu, true, false)]
    [InlineData(OutputBackend.Gpu, false, false)]
    public void SendCpuFrame_OnlyForCpuBackendAndEnabled(OutputBackend backend, bool enabled, bool expected)
        => SpoutOutputPolicy.SendCpuFrame(backend, enabled).Should().Be(expected);

    [Theory]
    [InlineData(0, 5, (int)SpoutCopyDecision.NoImage)]
    [InlineData(5, 5, (int)SpoutCopyDecision.SameHeld)]
    [InlineData(6, 5, (int)SpoutCopyDecision.Copy)]
    [InlineData(1, 0, (int)SpoutCopyDecision.Copy)]
    public void DecideCopy_NoImageAndSameHeldAreResends(long selectedId, long heldId, int expected)
        => SpoutOutputPolicy.DecideCopy(selectedId, heldId).Should().Be((SpoutCopyDecision)expected);

    [Theory]
    [InlineData(true, false, (int)SpoutWorkerAction.Start)]
    [InlineData(true, true, (int)SpoutWorkerAction.None)]
    [InlineData(false, true, (int)SpoutWorkerAction.Stop)]
    [InlineData(false, false, (int)SpoutWorkerAction.None)]
    public void EvaluateWorker_StartsOnceAndStopsOnDisable(bool enabled, bool running, int expected)
        => SpoutOutputPolicy.EvaluateWorker(enabled, running).Should().Be((SpoutWorkerAction)expected);

    [Fact]
    public void EvaluateWorker_ManualRetryAfterStopRestarts()
    {
        // 手動再試行: 無効化で停止した後、再度有効化すると起動できる（fault 後も同じ経路）。
        SpoutOutputPolicy.EvaluateWorker(false, true).Should().Be(SpoutWorkerAction.Stop);
        SpoutOutputPolicy.EvaluateWorker(true, false).Should().Be(SpoutWorkerAction.Start);
    }
}
