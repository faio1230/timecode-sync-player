namespace GpuOutputProbe;

internal readonly record struct ComposeAlignDecision(long ErrorTicks, long ErrorMicroseconds, long CorrectionTicks, long CorrectionMicroseconds, long WantedQpc);

// Pure phase policy for `--compose-align vblank` (GPU worker only, fake-clock testable). After each present.scanout
// observation the next compose due D is compared with the wanted time W = the first (vblank - margin - lead) strictly
// after now (stepping by the display period): e = wrap(D - W) into (-period/2, period/2], c = -clamp(e, ±slew). The shared
// ScheduleOffset moves by c, so compose and Spout keep their period and only the phase moves, at most slew per observation.
// No correction before the first scanout (bootstrap) or without a valid period. Corrections are quantized to whole
// microseconds so the analyzer rebuilds the exact offset from compose.align events (detail = c µs).
internal sealed class ComposeAlignGate
{
    public const double SlewMs = 0.5;
    private readonly long frequency;
    public long LeadTicks { get; }
    public long SlewTicks { get; }

    public ComposeAlignGate(double leadMs, long frequency)
    {
        if (frequency <= 0 || !double.IsFinite(leadMs) || leadMs <= 0) throw new ArgumentOutOfRangeException();
        this.frequency = frequency;
        LeadTicks = Math.Max(1, (long)Math.Round(leadMs * frequency / 1000));
        SlewTicks = Math.Max(1, (long)Math.Round(SlewMs * frequency / 1000));
    }

    public long Microseconds(long ticks) => ticks * 1_000_000 / frequency;

    // Null before the first scanout observation: the schedule is not moved while the display bootstraps.
    public ComposeAlignDecision? Decide(VblankDisplayGate gate, long now, long dueQpc)
    {
        if (!gate.HasScanout) return null;
        var prediction = gate.Predict(now);
        return Decide(now, dueQpc, prediction.VblankQpc, prediction.PeriodTicks, gate.MarginTicks);
    }

    // vblankQpc: any vblank of the display's phase (Predict(now).VblankQpc); W is stepped from it by whole periods.
    public ComposeAlignDecision? Decide(long now, long dueQpc, long vblankQpc, long periodTicks, long marginTicks)
    {
        if (periodTicks <= 0) return null;
        long first = vblankQpc - marginTicks - LeadTicks;
        long wanted = first + (FloorDiv(now - first, periodTicks) + 1) * periodTicks; // Smallest n with first + n*period > now.
        long remainder = ((dueQpc - wanted) % periodTicks + periodTicks) % periodTicks;
        long error = remainder > periodTicks / 2 ? remainder - periodTicks : remainder;
        long correctionMicroseconds = Microseconds(-Math.Clamp(error, -SlewTicks, SlewTicks));
        return new(error, Microseconds(error), correctionMicroseconds * frequency / 1_000_000, correctionMicroseconds, wanted);
    }

    private static long FloorDiv(long a, long b) { long q = a / b; return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q; }
}
