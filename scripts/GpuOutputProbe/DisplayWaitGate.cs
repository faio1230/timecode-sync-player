using System.Runtime.ExceptionServices;

namespace GpuOutputProbe;

internal enum DisplayWaitResult { Ready, Timeout, Cancelled }
internal readonly record struct DisplayWaitAttempt(long StartQpc, long EndQpc, long DeadlineQpc, int TimeoutMs,
    string Kind, string Outcome, bool PermissionHeld);

// Single GPU-worker owner. No source texture or lease is accepted here: selection occurs only after a usable notification.
internal sealed class DisplayWaitGate(PresentReadyGate readiness)
{
    private long lastScheduled = long.MinValue;
    public static string? SkipReason(long now, long deadline, bool cancelled) =>
        cancelled ? "display.wait.cancelled" : now >= deadline ? "display.wait.deadline" : null;
    public static int TimeoutMs(long now, long deadline, long frequency)
    {
        if (frequency <= 0) throw new ArgumentOutOfRangeException(nameof(frequency));
        return (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0.0, deadline - (double)now) * 1000.0 / frequency));
    }
    public bool TryForTick(long scheduled, long deadline, long frequency, CancellationToken cancellation,
        Func<long> timestamp, Func<int, DisplayWaitResult> waitOnce, Action<DisplayWaitAttempt> record, Action<string> skip)
    {
        if (scheduled == lastScheduled) return false; // Even an early timeout/source failure cannot retry within this output slot.
        if (scheduled < lastScheduled) throw new InvalidOperationException("Display output slots must advance.");
        lastScheduled = scheduled;
        long start = timestamp();
        string? reason = SkipReason(start, deadline, cancellation.IsCancellationRequested);
        if (reason != null) { skip(reason); return false; }
        bool retained = readiness.PermissionHeld;
        int timeout = retained ? 0 : TimeoutMs(start, deadline, frequency);
        DisplayWaitResult result = DisplayWaitResult.Ready;
        Exception? error = null; long end;
        try { if (!retained) result = waitOnce(timeout); }
        catch (Exception e) { error = e; }
        finally { end = timestamp(); }
        if (error == null && !retained && result == DisplayWaitResult.Ready) readiness.GrantFromNotification();
        reason = SkipReason(end, deadline, cancellation.IsCancellationRequested || result == DisplayWaitResult.Cancelled);
        string outcome = error != null ? "error" : reason == "display.wait.cancelled" ? "cancelled" : reason == "display.wait.deadline" ? "deadline" :
            retained ? "retained" : result == DisplayWaitResult.Ready ? "ready" : "timeout";
        record(new(start, end, deadline, timeout, retained ? "retained" : "native", outcome, readiness.PermissionHeld));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        if (reason != null) { skip(reason); return false; }
        reason = SkipReason(timestamp(), deadline, cancellation.IsCancellationRequested);
        if (reason != null) { skip(reason); return false; }
        return readiness.PermissionHeld;
    }
}
