namespace GpuOutputProbe;

// Fake clocks and fake statistics only: no D3D device, swapchain, timer, or Spout call is made here.
internal static class ComposeAlignSelfTests
{
    private const long Frequency = 1_000_000; // 1000 ticks per millisecond; 60 Hz period 16_667, margin 3000, lead 1500, slew 500.
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static ComposeAlignGate Gate(double leadMs = 1.5, long frequency = Frequency) => new(leadMs, frequency);

    public static void AlignOptions()
    {
        var o = Options.Parse([]);
        Check(o.ComposeAlign == "off" && o.ComposeLeadMs == 1.5, "Default align options differ.");
        o = Options.Parse(["--display-pacing", "vblank", "--compose-align", "vblank"]);
        Check(o.ComposeAlign == "vblank" && o.ComposeLeadMs == 1.5, "Align with vblank pacing rejected.");
        Check(Options.Parse(["--output", "fullscreen", "--display-pacing", "vblank", "--compose-align", "vblank", "--compose-lead-ms", "0.5"]).ComposeLeadMs == 0.5, "Minimum lead rejected.");
        Check(Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "fence", "--display-pacing", "vblank", "--compose-align", "vblank", "--compose-lead-ms", "8"]).ComposeLeadMs == 8, "Fence + vblank + maximum lead rejected.");
        Check(Options.Parse(["--compose-lead-ms", "1.5"]).ComposeAlign == "off" && Options.Parse(["--display-pacing", "vblank", "--compose-align", "off"]).ComposeAlign == "off", "Default lead / explicit off rejected.");
        Throws<ArgumentException>(() => Options.Parse(["--compose-align", "vblank"])); // tick pacing
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vsync", "--compose-align", "vblank"]));
        Throws<ArgumentException>(() => Options.Parse(["--output", "spout", "--display-pacing", "vblank", "--compose-align", "vblank"]));
        Throws<ArgumentException>(() => Options.Parse(["--compose-align", "other"]));
        Throws<ArgumentException>(() => Options.Parse(["--compose-lead-ms", "2"])); // non-default lead without align
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vblank", "--compose-lead-ms", "2"]));
        foreach (string lead in new[] { "0.4", "8.1", "NaN", "Infinity", "0", "-1" })
            Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vblank", "--compose-align", "vblank", "--compose-lead-ms", lead]));
        Throws<ArgumentException>(() => Options.Parse(["--compose-align", "off", "--compose-align", "off"]));
    }

    public static void WrapAndClamp()
    {
        Throws<ArgumentOutOfRangeException>(() => new ComposeAlignGate(0, Frequency));
        Throws<ArgumentOutOfRangeException>(() => new ComposeAlignGate(1.5, 0));
        var gate = Gate();
        Check(gate.LeadTicks == 1500 && gate.SlewTicks == 500 && ComposeAlignGate.SlewMs == 0.5, "Lead/slew ticks differ.");
        const long vblank = 116_667, period = 16_667, margin = 3000, wanted = vblank - margin - 1500; // 112_167
        Check(gate.Decide(100_000, wanted + 200, vblank, period, margin) == new ComposeAlignDecision(200, 200, -200, -200, wanted), "Small positive error must be corrected fully.");
        Check(gate.Decide(100_000, wanted - 300, vblank, period, margin) == new ComposeAlignDecision(-300, -300, 300, 300, wanted), "Small negative error must be corrected fully.");
        Check(gate.Decide(100_000, wanted + 8333, vblank, period, margin) == new ComposeAlignDecision(8333, 8333, -500, -500, wanted), "Half period stays positive and is clamped to slew.");
        Check(gate.Decide(100_000, wanted + 8334, vblank, period, margin) == new ComposeAlignDecision(-8333, -8333, 500, 500, wanted), "Above half period wraps negative.");
        Check(gate.Decide(100_000, wanted + period, vblank, period, margin)!.Value.ErrorTicks == 0 && gate.Decide(100_000, wanted - 3 * period, vblank, period, margin)!.Value.CorrectionTicks == 0, "Whole periods must wrap to zero.");
        Check(gate.Decide(100_000, wanted - 5000, vblank, period, margin)!.Value.CorrectionTicks == 500, "Large negative error must be clamped to +slew.");
        // W is the first (vblank - margin - lead) strictly after now, stepping by the period in both directions.
        Check(gate.Decide(wanted - 1, wanted, vblank, period, margin)!.Value.WantedQpc == wanted && gate.Decide(wanted, wanted, vblank, period, margin)!.Value.WantedQpc == wanted + period, "W must be strictly after now.");
        Check(gate.Decide(200_000, wanted, vblank, period, margin)!.Value.WantedQpc == wanted + 6 * period, "W must step forward by whole periods.");
        Check(gate.Decide(50_000, wanted, vblank, period, margin)!.Value.WantedQpc == wanted - 3 * period, "W must step backward by whole periods.");
        Check(gate.Decide(100_000, wanted, vblank, 0, margin) == null, "No correction without a valid period.");
        // Corrections are quantized to whole microseconds (10 MHz clock): the applied ticks equal detail µs * frequency / 1e6.
        var fine = Gate(1.5, 10_000_000);
        var d = fine.Decide(1_000_000, 1_000_000 + 45_000 + 4837, 1_000_000 + 45_000 + 30_000 + 15_000, 166_667, 30_000)!.Value;
        Check(d.ErrorTicks == 4837 && d.ErrorMicroseconds == 483 && d.CorrectionMicroseconds == -483 && d.CorrectionTicks == -4830, "Microsecond quantization differs.");
        d = fine.Decide(1_000_000, 1_000_000 + 45_000 - 70_000, 1_000_000 + 45_000 + 30_000 + 15_000, 166_667, 30_000)!.Value;
        Check(d.CorrectionMicroseconds == 500 && d.CorrectionTicks == 5000 && d.ErrorMicroseconds == -7000, "Clamped correction must be exactly slew in microseconds.");
    }

    // Before the first scanout the display bootstraps and the schedule is not moved; afterwards Predict(now) supplies the phase.
    public static void NoCorrectionBeforeScanout()
    {
        var display = new VblankDisplayGate(3, 60, Frequency);
        var gate = Gate();
        Check(gate.Decide(display, 100_500, 116_667) == null, "Correction before the first scanout.");
        display.ObserveScanout(100_000, 10);
        var d = gate.Decide(display, 100_500, 116_667);
        Check(d is { WantedQpc: 112_167, ErrorTicks: 4500, CorrectionTicks: -500 }, "Correction after the first scanout differs: " + d);
    }

    // Initial error of half a period converges to within slew in <= 17 corrections (8333 / 500) and stays there while the
    // compose period (fps 60 -> 16666.67 ticks) drifts against the display period (16667 ticks) by less than slew per tick.
    public static void ConvergesFromHalfPeriodError()
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
                Check(tick.Scheduled > last && tick.Skipped == 0, "Aligned schedule skipped or regressed.");
                last = tick.Scheduled;
            }
            var d = gate.Decide(display, now, schedule.DueQpc)!.Value;
            offset.Add(d.CorrectionTicks);
            if (converged < 0 && Math.Abs(d.ErrorTicks) <= gate.SlewTicks) converged = step;
            if (converged >= 0) Check(Math.Abs(d.ErrorTicks) <= gate.SlewTicks, $"Error left the slew band at step {step}: {d.ErrorTicks}.");
            now += 16_667; // One display period per observation.
        }
        Check(converged is >= 0 and <= 17, "Convergence took " + converged + " steps.");
        Check(offset.Ticks is <= -8000 and >= -9000, "Accumulated offset differs: " + offset.Ticks);
    }

    public static void OffsetScheduleNeverRegresses()
    {
        var offset = new ScheduleOffset();
        var s = new TickSchedule(1000, 60, 6000, offset); // Period 100.
        Check(s.DueQpc == 1000 && s.Take(1000) == (1000L, 0L) && s.DueQpc == 1100, "Zero offset must match the plain schedule.");
        offset.Add(-30);
        Check(s.DueQpc == 1070 && s.Take(1070) == (1070L, 0L), "Offset moved earlier must shift the next due only.");
        offset.Add(50);
        Check(s.DueQpc == 1220 && s.Take(1225) == (1220L, 0L), "Offset moved later must shift the next due only.");
        // A change between the loop's due read and Take (Spout worker while the GPU worker corrects): Take uses the sample.
        long due = s.DueQpc; offset.Add(40);
        Check(due == 1320 && s.Take(1320) == (1320L, 0L) && s.DueQpc == 1460, "A later offset after the due read made a due tick not due, or was not picked up next.");
        offset.Add(-40);
        Throws<InvalidOperationException>(() => s.Take(1459)); // Not yet sampled: the last sample (due 1460) still applies.
        Check(s.DueQpc == 1420, "Earlier offset was not applied at the next due read.");
        Throws<InvalidOperationException>(() => s.Take(1419));
        Check(s.Take(1420) == (1420L, 0L), "Earlier offset after the due read was not applied at the next read.");
        // Pathological: an offset moved earlier by more than a period never hands out a time at or before the last taken one.
        offset.Add(-150);
        Check(s.DueQpc == 1470 && s.DueQpc > 1420, "Next due at or before the last taken tick.");
        var skipped = s.Take(1471);
        Check(skipped.Scheduled == 1470 && skipped.Skipped == 1 && s.DueQpc == 1570, "Skipped-forward index differs.");
        // Late ticks still skip without catch-up under an offset.
        var late = s.Take(1925);
        Check(late == (1870L, 3L) && s.DueQpc == 1970, "Late-skip semantics changed under an offset.");
        Throws<InvalidOperationException>(() => s.Take(1969));
        // Fractional rate with a wandering offset: strictly increasing, and each taken tick is due at or before now.
        var random = new Random(7);
        var f = new TickSchedule(500, 59.94, 1_000_000, offset);
        long previous = long.MinValue, clock = 500;
        for (int i = 0; i < 2000; i++)
        {
            offset.Add(random.Next(-500, 501));
            long next = f.DueQpc;
            clock = Math.Max(clock, next) + random.Next(0, 300);
            var tick = f.Take(clock);
            Check(tick.Scheduled > previous && tick.Scheduled <= clock && tick.Skipped == 0, $"Wandering offset broke the schedule at {i}.");
            previous = tick.Scheduled;
        }
    }

    // The Spout schedule reads the same offset, so its 4 ms phase relative to the GPU schedule is preserved exactly.
    public static void SpoutKeepsPhaseUnderSharedOffset()
    {
        var options = Options.Parse(["--mode", "split", "--output", "both", "--send-phase-ms", "4", "--fps", "59.94", "--display-pacing", "vblank", "--compose-align", "vblank"]);
        var offset = new ScheduleOffset();
        const long origin = 1234567, frequency = 1_000_000;
        var gpuTiming = WorkerTiming.Create(options, origin, frequency, false);
        var spoutTiming = WorkerTiming.Create(options, origin, frequency, true);
        Check(gpuTiming.OriginQpc == origin && spoutTiming.OriginQpc == origin + 4000, "Worker origins changed.");
        var gpu = new TickSchedule(gpuTiming.OriginQpc, options.Fps, frequency, offset);
        var spout = new TickSchedule(spoutTiming.OriginQpc, options.Fps, frequency, offset);
        var random = new Random(11);
        for (int i = 0; i < 1000; i++)
        {
            offset.Add(random.Next(-500, 501));
            long gpuDue = gpu.DueQpc, spoutDue = spout.DueQpc;
            Check(spoutDue - gpuDue == 4000, $"Spout phase drifted at {i}: {spoutDue - gpuDue}.");
            Check(gpu.Take(gpuDue).Scheduled == gpuDue && spout.Take(spoutDue).Scheduled == spoutDue, "Taken ticks differ from their due times.");
        }
        Check(offset.Ticks != 0, "Test did not exercise a moved offset.");
    }
}
