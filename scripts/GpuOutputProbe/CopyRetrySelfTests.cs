namespace GpuOutputProbe;

// Fake clocks only: these tests never wait on an event or initialize graphics.
internal static class CopyRetrySelfTests
{
    private const long Frequency = 10_000_000; // 10_000 ticks per millisecond.
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static void BudgetClamping()
    {
        Check(CopyRetryGate.BudgetMs(0, 39_000, Frequency) == 3, "3.9 ms remainder must floor to three whole milliseconds.");
        Check(CopyRetryGate.BudgetMs(0, 100_000, Frequency) == CopyRetryGate.MaxWaitMs, "Ten millisecond remainder must cap at four.");
        Check(CopyRetryGate.BudgetMs(0, 40_000, Frequency) == 4, "Exactly 4.0 ms must yield four.");
        Check(CopyRetryGate.BudgetMs(0, 41_000, Frequency) == 4, "4.1 ms must cap at four.");
        Check(CopyRetryGate.BudgetMs(1_000, 41_000, Frequency) == 4, "Remaining 4.0 ms after elapsed time must yield four.");
    }

    public static void ExpiredAndInvalid()
    {
        Check(CopyRetryGate.BudgetMs(50_000, 50_000, Frequency) == 0, "Work at the deadline must not wait.");
        Check(CopyRetryGate.BudgetMs(60_000, 50_000, Frequency) == 0, "Expired work must not borrow another period.");
        Throws<ArgumentOutOfRangeException>(() => CopyRetryGate.BudgetMs(0, 100_000, 0));
        Throws<ArgumentOutOfRangeException>(() => CopyRetryGate.BudgetMs(0, 100_000, -1));
    }
}
