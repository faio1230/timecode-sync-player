using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class ComposeLeadControllerTests
{
    private const long Frequency = 1_000_000; // 1 tick = 1 µs.

    // 13 samples at 80ms cadence + the boundary sample = exactly one 1s window per call.
    private static bool FeedWindow(ComposeLeadController controller, long durationTicks, ref long now)
    {
        for (int i = 0; i < 13; i++)
        {
            _ = controller.Add(durationTicks, now, out _);
            now += 80_000;
        }
        return controller.Add(durationTicks, now, out _);
    }

    // 起動後 3 秒は学習しない（段階 3）。最初の標本で起点を作り、3 秒進める。
    private static void StartAfterWarmup(ComposeLeadController controller, ref long now)
    {
        _ = controller.Add(0, now, out _);
        now += (long)(ComposeLeadController.WarmupSeconds * Frequency) + 1;
    }

    [Fact]
    public void Add_RaisesImmediatelyToP99PlusMarginAndClampsAtMaximum()
    {
        var controller = new ComposeLeadController(Frequency, 2);
        long now = 0;
        StartAfterWarmup(controller, ref now);
        // 4ms p99 + 1ms = 5ms > current 2ms: immediate raise.
        FeedWindow(controller, 4000, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(5);

        // 20ms p99 clamps at the 8ms maximum.
        now += Frequency;
        FeedWindow(controller, 20_000, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(8);
    }

    [Fact]
    public void Add_LowersOnlyAfterFiveConsecutiveWindowsInHalfMillisecondSteps()
    {
        var controller = new ComposeLeadController(Frequency, 5);
        long now = 0;
        StartAfterWarmup(controller, ref now);
        // Fast compose: p99+1ms = 1.01ms, at least 0.5ms below the current 5ms.
        for (int i = 0; i < 4; i++) FeedWindow(controller, 10, ref now).Should().BeFalse();
        controller.CurrentLeadMs.Should().Be(5);
        FeedWindow(controller, 10, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(4.5);

        // The counter restarts: four more windows do nothing, the fifth lowers by another step.
        for (int i = 0; i < 4; i++) FeedWindow(controller, 10, ref now).Should().BeFalse();
        FeedWindow(controller, 10, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(4.0);
    }

    [Fact]
    public void Add_ResetsTheDecreaseCounterWhenDesiredReturnsWithinBand()
    {
        var controller = new ComposeLeadController(Frequency, 5);
        long now = 0;
        StartAfterWarmup(controller, ref now);
        // Two low windows (2/5), then a window whose desired equals the current lead (5ms).
        FeedWindow(controller, 10, ref now).Should().BeFalse();
        FeedWindow(controller, 10, ref now).Should().BeFalse();
        FeedWindow(controller, 4000, ref now).Should().BeFalse();
        controller.CurrentLeadMs.Should().Be(5);

        // The counter was reset by the in-band window: four more low windows are still not enough.
        for (int i = 0; i < 4; i++) FeedWindow(controller, 10, ref now).Should().BeFalse();
        FeedWindow(controller, 10, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(4.5);
    }

    [Fact]
    public void Add_DoesNotFallBelowMinimumLead()
    {
        var controller = new ComposeLeadController(Frequency, 2);
        long now = 0;
        StartAfterWarmup(controller, ref now);
        // p99 = 0 -> desired = 1ms (minimum). Hysteresis: 5 windows per 0.5ms step, 2.0 -> 1.5 -> 1.0.
        for (int i = 0; i < 4; i++) FeedWindow(controller, 0, ref now).Should().BeFalse();
        FeedWindow(controller, 0, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(1.5);
        for (int i = 0; i < 4; i++) FeedWindow(controller, 0, ref now).Should().BeFalse();
        FeedWindow(controller, 0, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(1.0);
        FeedWindow(controller, 0, ref now).Should().BeFalse();
        controller.CurrentLeadMs.Should().Be(1.0);
    }

    [Fact]
    public void SuspendLearning_IgnoresSamplesForOneSecondAfterEvents()
    {
        var controller = new ComposeLeadController(Frequency, 5);
        long now = 0;
        StartAfterWarmup(controller, ref now);
        long suspendAt = now;
        controller.SuspendLearning(suspendAt);

        // 除外中（1 秒以内）の遅い標本は学習しない
        for (int i = 0; i < 6; i++)
            _ = controller.Add(50_000, suspendAt + i * 80_000, out _);
        controller.CurrentLeadMs.Should().Be(5);

        // 除外明けは遅い窓で即時上げる
        now = suspendAt + Frequency;
        for (int i = 0; i < 3 && controller.CurrentLeadMs < 8; i++)
            FeedWindow(controller, 50_000, ref now);
        controller.CurrentLeadMs.Should().Be(8);
    }

    [Fact]
    public void Add_IgnoresComposeSamplesDuringWarmup()
    {
        var controller = new ComposeLeadController(Frequency, 3);
        long now = 0;
        _ = controller.Add(20_000, now, out _);           // 起動直後の長い合成
        now += Frequency; _ = controller.Add(20_000, now, out _);
        now += Frequency; _ = controller.Add(20_000, now, out _);
        controller.CurrentLeadMs.Should().Be(3);          // 3 秒間は学習しない
        now += Frequency;
        FeedWindow(controller, 20_000, ref now).Should().BeTrue();
        controller.CurrentLeadMs.Should().Be(8);
    }

    [Fact]
    public void Add_IgnoresSparseWindowsAndInvalidDurations()
    {
        var controller = new ComposeLeadController(Frequency, 3);
        controller.Add(-5, 0, out _).Should().BeFalse();
        controller.Add(1000, 0, out _).Should().BeFalse();
        for (int i = 0; i < 3; i++) _ = controller.Add(1000, i * Frequency, out _); // fewer than 10 samples.
        controller.Add(1000, 4 * Frequency, out double lead).Should().BeFalse();
        controller.CurrentLeadMs.Should().Be(3);
    }

    [Fact]
    public void ComposeAlignGate_LeadChangeIsReflectedThroughSlewLimitedCorrection()
    {
        var gate = new ComposeAlignGate(2, Frequency);
        var display = new VblankDisplayGate(3, 60, Frequency);
        display.ObserveScanout(100_000, 10);
        long before = gate.Decide(display, 100_500, 116_667)!.Value.WantedQpc;
        gate.SetLeadMilliseconds(5);
        long after = gate.Decide(display, 100_500, 116_667)!.Value.WantedQpc;
        // Wanted compose time moves 3ms earlier; the correction is applied at most slew per scanout.
        (before - after).Should().Be(3000);
        var decision = gate.Decide(display, 100_500, 116_667)!.Value;
        Math.Abs(decision.CorrectionTicks).Should().BeLessThanOrEqualTo(gate.SlewTicks);
        Math.Abs(decision.CorrectionMicroseconds).Should().BeLessThanOrEqualTo(500);
    }
}
