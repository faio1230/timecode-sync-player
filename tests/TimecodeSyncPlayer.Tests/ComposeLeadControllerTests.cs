using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class ComposeLeadControllerTests
{
    private const long Frequency = 1_000_000; // 1 tick = 1 µs.

    [Fact]
    public void Add_KeepsLeadWhenP99PlusMarginMatchesCurrent()
    {
        var controller = new ComposeLeadController(Frequency, 3);
        // 2ms p99 + 1ms margin = 3ms: the default lead does not change.
        long now = 0;
        for (int i = 0; i < 20; i++)
        {
            controller.Add(2000, now, out _).Should().BeFalse();
            now += 100_000;
        }
        controller.CurrentLeadMs.Should().Be(3);
    }

    [Fact]
    public void Add_UpdatesOncePerSecondWithP99AndClampsToRange()
    {
        var controller = new ComposeLeadController(Frequency, 3);
        long now = 0;
        // 5ms p99 + 1ms margin -> 6ms. 80ms cadence keeps the 1s window boundary after the 13th sample.
        for (int i = 0; i < 13; i++)
        {
            _ = controller.Add(5000, now, out _);
            now += 80_000;
        }
        controller.Add(5000, now, out double lead).Should().BeTrue();
        lead.Should().Be(6);
        controller.CurrentLeadMs.Should().Be(6);

        // Very slow compose clamps at 8ms.
        now += Frequency;
        for (int i = 0; i < 13; i++)
        {
            _ = controller.Add(50_000, now, out _);
            now += 80_000;
        }
        controller.Add(50_000, now, out lead).Should().BeTrue();
        lead.Should().Be(8);

        // Very fast compose clamps at 1ms.
        now += Frequency;
        for (int i = 0; i < 13; i++)
        {
            _ = controller.Add(10, now, out _);
            now += 80_000;
        }
        controller.Add(10, now, out lead).Should().BeTrue();
        lead.Should().BeApproximately(1.0, 0.05);
    }

    [Fact]
    public void Add_IgnoresSparseWindowsAndInvalidDurations()
    {
        var controller = new ComposeLeadController(Frequency, 3);
        controller.Add(-5, 0, out _).Should().BeFalse();
        controller.Add(1000, 0, out _).Should().BeFalse();
        for (int i = 0; i < 3; i++) _ = controller.Add(1000, i * Frequency, out _); // fewer than 10 samples in the window.
        controller.Add(1000, 4 * Frequency, out double lead).Should().BeFalse();
        controller.CurrentLeadMs.Should().Be(3);
    }

    [Fact]
    public void ComposeAlignGate_SetLeadMillisecondsMovesWantedComposeTime()
    {
        var gate = new ComposeAlignGate(1.5, Frequency);
        const long vblank = 116_667, period = 16_667, margin = 3000;
        long before = gate.Decide(100_000, 100_000, vblank, period, margin)!.Value.WantedQpc;
        gate.SetLeadMilliseconds(5);
        long after = gate.Decide(100_000, 100_000, vblank, period, margin)!.Value.WantedQpc;
        gate.LeadTicks.Should().Be(5000);
        (before - after).Should().Be(3500);
    }
}
