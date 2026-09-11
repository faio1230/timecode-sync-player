namespace TimecodeSyncPlayer.Output;

internal readonly record struct ComposeAlignDecision(long ErrorTicks, long ErrorMicroseconds, long CorrectionTicks, long CorrectionMicroseconds, long WantedQpc);

/// <summary>
/// 合成位相の vblank 整列（GPU worker のみ、フェイク時計でテスト可能）。
/// present.scanout 観測ごとに、次の合成予定 D と望ましい時刻 W（now より後の最初の vblank − margin − lead）の
/// 位相誤差 e を wrap し、c = -clamp(e, ±slew) だけ ScheduleOffset を動かす。周期は変えず位相だけ。
/// 補正は整数 µs に量子化する。試作 scripts/GpuOutputProbe の ComposeAlignGate を移植。
/// </summary>
internal sealed class ComposeAlignGate
{
    public const double SlewMs = 0.5;
    private readonly long frequency;
    private long leadTicks;
    public long LeadTicks => Volatile.Read(ref leadTicks);
    public long SlewTicks { get; }

    public ComposeAlignGate(double leadMs, long frequency)
    {
        if (frequency <= 0 || !double.IsFinite(leadMs) || leadMs <= 0) throw new ArgumentOutOfRangeException();
        this.frequency = frequency;
        leadTicks = Math.Max(1, (long)Math.Round(leadMs * frequency / 1000));
        SlewTicks = Math.Max(1, (long)Math.Round(SlewMs * frequency / 1000));
    }

    /// <summary>合成時間の実測に応じて lead を更新する（GPU worker のみが呼ぶ）。</summary>
    public void SetLeadMilliseconds(double leadMs)
    {
        if (!double.IsFinite(leadMs) || leadMs <= 0) throw new ArgumentOutOfRangeException(nameof(leadMs));
        Volatile.Write(ref leadTicks, Math.Max(1, (long)Math.Round(leadMs * frequency / 1000)));
    }

    public long Microseconds(long ticks) => ticks * 1_000_000 / frequency;

    // 最初の scanout 観測より前は null（bootstrap 中はスケジュールを動かさない）。
    public ComposeAlignDecision? Decide(VblankDisplayGate gate, long now, long dueQpc)
    {
        if (!gate.HasScanout) return null;
        var prediction = gate.Predict(now);
        return Decide(now, dueQpc, prediction.VblankQpc, prediction.PeriodTicks, gate.MarginTicks);
    }

    public ComposeAlignDecision? Decide(long now, long dueQpc, long vblankQpc, long periodTicks, long marginTicks)
    {
        if (periodTicks <= 0) return null;
        long first = vblankQpc - marginTicks - LeadTicks;
        long wanted = first + (FloorDiv(now - first, periodTicks) + 1) * periodTicks;
        long remainder = ((dueQpc - wanted) % periodTicks + periodTicks) % periodTicks;
        long error = remainder > periodTicks / 2 ? remainder - periodTicks : remainder;
        long correctionMicroseconds = Microseconds(-Math.Clamp(error, -SlewTicks, SlewTicks));
        return new(error, Microseconds(error), correctionMicroseconds * frequency / 1_000_000, correctionMicroseconds, wanted);
    }

    private static long FloorDiv(long a, long b) { long q = a / b; return a % b != 0 && (a < 0) != (b < 0) ? q - 1 : q; }
}
