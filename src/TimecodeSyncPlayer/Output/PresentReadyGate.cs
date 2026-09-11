using System.Runtime.ExceptionServices;

namespace TimecodeSyncPlayer.Output;

internal readonly record struct PresentReadyAttempt(long StartQpc, long EndQpc, long DeadlineQpc, int TimeoutMs,
    string Kind, string Outcome, bool PermissionHeld);

/// <summary>
/// swapchain latency イベントから得る「表示可能」権。実際の Present 試行だけが消費する。
/// 試作 scripts/GpuOutputProbe の PresentReadyGate を移植。
/// </summary>
internal sealed class PresentReadyGate
{
    public const int MaxWaitMs = 1;
    public bool PermissionHeld { get; private set; }

    public static string? SkipReason(long now, long deadline, bool cancelled) =>
        cancelled ? "present.cancelled" : now >= deadline ? "present.deadline" : null;

    public static int TimeoutMs(int configured, long now, long deadline, long frequency)
    {
        if (configured is < 0 or > MaxWaitMs || frequency <= 0) throw new ArgumentOutOfRangeException();
        return Math.Min(configured, (int)Math.Min(MaxWaitMs, Math.Floor(Math.Max(0.0, deadline - (double)now) * 1000.0 / frequency)));
    }

    public bool TryAcquire(int configured, long deadline, long frequency, CancellationToken cancellation, Func<long> timestamp,
        Func<int, bool> waitOnce, Action<PresentReadyAttempt> record, Action<string> skip)
    {
        long start = timestamp();
        string? reason = SkipReason(start, deadline, cancellation.IsCancellationRequested);
        if (reason != null) { skip(reason); return false; }
        bool retained = PermissionHeld;
        int timeout = retained ? 0 : TimeoutMs(configured, start, deadline, frequency);
        Exception? error = null;
        long end;
        try { if (!retained) PermissionHeld = waitOnce(timeout); }
        catch (Exception e) { error = e; }
        finally { end = timestamp(); }
        reason = SkipReason(end, deadline, cancellation.IsCancellationRequested);
        string outcome = error != null ? "error" : reason == "present.cancelled" ? "cancelled" : reason == "present.deadline" ? "deadline" :
            retained ? "retained" : PermissionHeld ? "ready" : "notReady";
        record(new(start, end, deadline, timeout, retained ? "retained" : "native", outcome, PermissionHeld));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        if (reason != null) { skip(reason); return false; }
        if (!PermissionHeld) { skip("present.notReady"); return false; }
        return true;
    }

    public void ConsumeForPresent()
    {
        if (!PermissionHeld) throw new InvalidOperationException("Present requires a retained latency permission.");
        PermissionHeld = false;
    }

    public void GrantFromNotification()
    {
        if (PermissionHeld) throw new InvalidOperationException("Cannot grant a second latency permission while one is retained.");
        PermissionHeld = true;
    }

    public void Discard() => PermissionHeld = false;
}
