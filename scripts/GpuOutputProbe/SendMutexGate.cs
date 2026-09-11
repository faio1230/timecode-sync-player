namespace GpuOutputProbe;

internal readonly record struct MutexAttempt(long StartQpc, long EndQpc, long DeadlineQpc, int TimeoutMs, string Outcome);

// The deadline belongs to the current scheduled tick. An old attempt never borrows another period.
internal static class MutexWaitPolicy
{
    public const int MaxWaitMs = 8;
    public static string? SkipReason(long now, long deadline, bool cancelled) =>
        cancelled ? "send.cancelled" : now >= deadline ? "send.deadlineExpired" : null;

    public static int TimeoutMs(int configured, long now, long deadline, long frequency)
    {
        if (configured is < 0 or > MaxWaitMs || frequency <= 0) throw new ArgumentOutOfRangeException();
        return Math.Min(configured, (int)Math.Min(MaxWaitMs, Math.Floor(Math.Max(0.0, deadline - (double)now) * 1000.0 / frequency)));
    }
}

// Kept independent of GPU/Spout so cancellation, stale acquisitions, and exact ownership can be tested.
internal sealed class SendMutexLease(Action release) : IDisposable
{
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; // Never retry ReleaseMutex if it throws.
        release();
    }
}

internal static class SendMutexGate
{
    public static SendMutexLease? Acquire(int configuredMs, long deadline, long frequency, CancellationToken cancellation,
        Func<long> timestamp, Func<int, bool> wait, Action release, Action<MutexAttempt> record, Action<string> skip)
    {
        // One timestamp is both the timeout decision point and the start of the CPU-observed acquisition interval.
        // This includes the small policy calculation, avoiding inconsistent floor budgets at a millisecond boundary.
        long start = timestamp();
        string? reason = MutexWaitPolicy.SkipReason(start, deadline, cancellation.IsCancellationRequested);
        if (reason != null) { skip(reason); return null; }
        int timeout = MutexWaitPolicy.TimeoutMs(configuredMs, start, deadline, frequency);
        bool acquired = false, abandoned = false;
        try
        {
            long end;
            try { acquired = wait(timeout); }
            catch (AbandonedMutexException) { acquired = true; abandoned = true; }
            finally { end = timestamp(); }

            // Enqueue after both timestamps have been captured; logging is outside the acquisition interval.
            record(new(start, end, deadline, timeout, abandoned ? "abandoned" : acquired ? "acquired" : "busy"));
            if (abandoned) throw new InvalidOperationException("Spout access mutex abandoned.");

            reason = MutexWaitPolicy.SkipReason(end, deadline, cancellation.IsCancellationRequested);
            if (reason != null) { skip(reason); return null; }
            if (!acquired) { skip("send.accessMutexBusy"); return null; }

            // Time spent recording must not turn an already-expired acquisition into new GPU work.
            reason = MutexWaitPolicy.SkipReason(timestamp(), deadline, cancellation.IsCancellationRequested);
            if (reason != null) { skip(reason); return null; }
            var lease = new SendMutexLease(release);
            acquired = false; // Transfer release responsibility only for a still-current acquisition.
            return lease;
        }
        finally { if (acquired) release(); }
    }
}
