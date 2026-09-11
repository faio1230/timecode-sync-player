namespace TimecodeSyncPlayer.Output;

internal readonly record struct MutexAttempt(long StartQpc, long EndQpc, long DeadlineQpc, int TimeoutMs, string Outcome);

/// <summary>期限は現在の予定 tick のもの。古い試行が別の周期の予算を借りない。試作 SendMutexGate から移植。</summary>
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

internal sealed class SendMutexLease(Action release) : IDisposable
{
    private bool disposed;
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; // ReleaseMutex が例外でも再試行しない。
        release();
    }
}

/// <summary>
/// Spout アクセス mutex の期限付き取得。GPU/Spout から独立し、キャンセル・期限・所有権をテストできる。
/// 試作 scripts/GpuOutputProbe の SendMutexGate を移植。
/// </summary>
internal static class SendMutexGate
{
    public static SendMutexLease? Acquire(int configuredMs, long deadline, long frequency, CancellationToken cancellation,
        Func<long> timestamp, Func<int, bool> wait, Action release, Action<MutexAttempt> record, Action<string> skip)
    {
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

            // 受信機の強制終了などで abandoned になっても所有権は移っており取得できている。
            // 送信を止めず、outcome=abandoned として記録だけ残す。
            record(new(start, end, deadline, timeout, abandoned ? "abandoned" : acquired ? "acquired" : "busy"));

            reason = MutexWaitPolicy.SkipReason(end, deadline, cancellation.IsCancellationRequested);
            if (reason != null) { skip(reason); return null; }
            if (!acquired) { skip("send.accessMutexBusy"); return null; }

            reason = MutexWaitPolicy.SkipReason(timestamp(), deadline, cancellation.IsCancellationRequested);
            if (reason != null) { skip(reason); return null; }
            var lease = new SendMutexLease(release);
            acquired = false;
            return lease;
        }
        finally { if (acquired) release(); }
    }
}
