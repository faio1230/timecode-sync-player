namespace GpuOutputProbe;

// Pure policy for the sender's single same-tick copy retry. No GPU or handle dependency so it is directly testable.
internal static class CopyRetryGate
{
    public const int MaxWaitMs = 4;
    public static int BudgetMs(long nowQpc, long deadlineQpc, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        double ms = (deadlineQpc - (double)nowQpc) * 1000.0 / frequency;
        if (ms <= 0) return 0;
        return Math.Min(MaxWaitMs, (int)Math.Floor(ms));
    }
}
