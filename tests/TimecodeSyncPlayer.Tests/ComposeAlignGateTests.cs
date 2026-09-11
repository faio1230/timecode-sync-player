using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class ComposeAlignGateTests
{
    private const long Frequency = 1_000_000; // 1000 ticks per ms; period 16_667, margin 3000, lead 1500, slew 500.
    private static ComposeAlignGate Gate(double leadMs = 1.5, long frequency = Frequency) => new(leadMs, frequency);

    [Fact]
    public void WrapAndClamp_QuantizeCorrectionsToMicroseconds()
    {
        FluentActions.Invoking(() => new ComposeAlignGate(0, Frequency)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new ComposeAlignGate(1.5, 0)).Should().Throw<ArgumentOutOfRangeException>();
        var gate = Gate();
        gate.LeadTicks.Should().Be(1500);
        gate.SlewTicks.Should().Be(500);
        ComposeAlignGate.SlewMs.Should().Be(0.5);

        const long vblank = 116_667, period = 16_667, margin = 3000, wanted = vblank - margin - 1500; // 112_167
        gate.Decide(100_000, wanted + 200, vblank, period, margin).Should().Be(new ComposeAlignDecision(200, 200, -200, -200, wanted));
        gate.Decide(100_000, wanted - 300, vblank, period, margin).Should().Be(new ComposeAlignDecision(-300, -300, 300, 300, wanted));
        gate.Decide(100_000, wanted + 8333, vblank, period, margin).Should().Be(new ComposeAlignDecision(8333, 8333, -500, -500, wanted));
        gate.Decide(100_000, wanted + 8334, vblank, period, margin).Should().Be(new ComposeAlignDecision(-8333, -8333, 500, 500, wanted));
        gate.Decide(100_000, wanted + period, vblank, period, margin)!.Value.ErrorTicks.Should().Be(0);
        gate.Decide(100_000, wanted - 3 * period, vblank, period, margin)!.Value.CorrectionTicks.Should().Be(0);
        gate.Decide(100_000, wanted - 5000, vblank, period, margin)!.Value.CorrectionTicks.Should().Be(500);
        gate.Decide(wanted - 1, wanted, vblank, period, margin)!.Value.WantedQpc.Should().Be(wanted);
        gate.Decide(wanted, wanted, vblank, period, margin)!.Value.WantedQpc.Should().Be(wanted + period);
        gate.Decide(200_000, wanted, vblank, period, margin)!.Value.WantedQpc.Should().Be(wanted + 6 * period);
        gate.Decide(50_000, wanted, vblank, period, margin)!.Value.WantedQpc.Should().Be(wanted - 3 * period);
        gate.Decide(100_000, wanted, vblank, 0, margin).Should().BeNull();

        var fine = Gate(1.5, 10_000_000);
        var d = fine.Decide(1_000_000, 1_000_000 + 45_000 + 4837, 1_000_000 + 45_000 + 30_000 + 15_000, 166_667, 30_000)!.Value;
        d.ErrorTicks.Should().Be(4837);
        d.ErrorMicroseconds.Should().Be(483);
        d.CorrectionMicroseconds.Should().Be(-483);
        d.CorrectionTicks.Should().Be(-4830);
        d = fine.Decide(1_000_000, 1_000_000 + 45_000 - 70_000, 1_000_000 + 45_000 + 30_000 + 15_000, 166_667, 30_000)!.Value;
        d.CorrectionMicroseconds.Should().Be(500);
        d.CorrectionTicks.Should().Be(5000);
        d.ErrorMicroseconds.Should().Be(-7000);
    }

    [Fact]
    public void NoCorrectionBeforeScanout_ThenUsesThePredictionPhase()
    {
        var display = new VblankDisplayGate(3, 60, Frequency);
        var gate = Gate();
        gate.Decide(display, 100_500, 116_667).Should().BeNull();
        display.ObserveScanout(100_000, 10);
        var d = gate.Decide(display, 100_500, 116_667);
        d.Should().NotBeNull();
        d!.Value.WantedQpc.Should().Be(112_167);
        d.Value.ErrorTicks.Should().Be(4500);
        d.Value.CorrectionTicks.Should().Be(-500);
    }

    [Fact]
    public void ConvergesFromHalfPeriodErrorWithinSlewBand()
    {
        var display = new VblankDisplayGate(3, 60, Frequency);
        var gate = Gate();
        var offset = new ScheduleOffset();
        const long origin = 112_167 + 8333; // First tick half a period after the first wanted time.
        var schedule = new TickSchedule(origin, 60, Frequency, offset);
        display.ObserveScanout(100_000, 10);
        long now = origin, last = long.MinValue, converged = -1;
        for (int step = 0; step < 60; step++)
        {
            while (now >= schedule.DueQpc)
            {
                var tick = schedule.Take(now);
                tick.Scheduled.Should().BeGreaterThan(last);
                tick.Skipped.Should().Be(0);
                last = tick.Scheduled;
            }
            var d = gate.Decide(display, now, schedule.DueQpc)!.Value;
            offset.Add(d.CorrectionTicks);
            if (converged < 0 && Math.Abs(d.ErrorTicks) <= gate.SlewTicks) converged = step;
            if (converged >= 0) Math.Abs(d.ErrorTicks).Should().BeLessThanOrEqualTo(gate.SlewTicks);
            now += 16_667;
        }
        converged.Should().BeInRange(0, 17);
        offset.Ticks.Should().BeInRange(-9000, -8000);
    }

    [Fact]
    public void SpoutSchedule_KeepsFourMillisecondPhaseUnderSharedOffset()
    {
        var offset = new ScheduleOffset();
        const long origin = 1234567, frequency = 1_000_000;
        const double fps = 59.94;
        long gpuOrigin = origin, spoutOrigin = origin + 4000;
        var gpu = new TickSchedule(gpuOrigin, fps, frequency, offset);
        var spout = new TickSchedule(spoutOrigin, fps, frequency, offset);
        var random = new Random(11);
        for (int i = 0; i < 1000; i++)
        {
            offset.Add(random.Next(-500, 501));
            long gpuDue = gpu.DueQpc, spoutDue = spout.DueQpc;
            (spoutDue - gpuDue).Should().Be(4000);
            gpu.Take(gpuDue).Scheduled.Should().Be(gpuDue);
            spout.Take(spoutDue).Scheduled.Should().Be(spoutDue);
        }
        offset.Ticks.Should().NotBe(0);
    }
}
