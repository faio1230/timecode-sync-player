using System.Runtime.ExceptionServices;

namespace GpuOutputProbe;

internal enum VsyncStep { Stop, Compose, Idle, WaitReady, Present }

// Pure decision logic for notification-driven display (`--display-pacing vsync`). Single GPU-worker owner.
// No lease, keyed mutex, or fence wait is held while deciding or waiting. Compose deadlines always win.
internal sealed class VsyncDisplayGate(PresentReadyGate readiness)
{
    public long LastPresentedId { get; private set; }
    private long waitedSlot = long.MinValue, attemptedSlot = long.MinValue;

    // slot: scheduledQpc of the last compose tick. composeDue: next compose deadline (or common end).
    public (VsyncStep Step, int TimeoutMs) Decide(long slot, long now, long composeDue, long latestId, bool cancelled, long frequency)
    {
        if (cancelled) return (VsyncStep.Stop, 0);
        if (now >= composeDue) return (VsyncStep.Compose, 0);
        // Never wait on a notification without a newer image: a signaled handle would otherwise busy-loop.
        if (latestId <= LastPresentedId || slot == attemptedSlot) return (VsyncStep.Idle, 0);
        if (readiness.PermissionHeld) return (VsyncStep.Present, 0);
        if (slot == waitedSlot) return (VsyncStep.Idle, 0);
        int timeout = DisplayWaitGate.TimeoutMs(now, composeDue, frequency);
        // Under one whole millisecond the loop yields to the compose deadline instead of a zero-timeout native wait.
        return timeout == 0 ? (VsyncStep.Idle, 0) : (VsyncStep.WaitReady, timeout);
    }

    public bool Wait(long slot, long deadline, int timeout, CancellationToken cancellation, Func<long> timestamp,
        Func<int, DisplayWaitResult> waitOnce, Action<DisplayWaitAttempt> record, Action<string> skip)
    {
        if (slot <= waitedSlot) throw new InvalidOperationException("At most one notification wait per compose slot.");
        if (readiness.PermissionHeld) throw new InvalidOperationException("Cannot wait while a readiness permission is retained.");
        waitedSlot = slot;
        long start = timestamp();
        DisplayWaitResult result = DisplayWaitResult.Timeout; Exception? error = null; long end;
        try { result = waitOnce(timeout); }
        catch (Exception e) { error = e; }
        finally { end = timestamp(); }
        if (error == null && result == DisplayWaitResult.Ready) readiness.GrantFromNotification();
        string outcome = error != null ? "error" : result switch { DisplayWaitResult.Ready => "ready", DisplayWaitResult.Cancelled => "cancelled", _ => "timeout" };
        record(new(start, end, deadline, timeout, "native", outcome, readiness.PermissionHeld));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        // A late or cancelled grant keeps its permission; the next slot presents without another native wait.
        if (readiness.PermissionHeld && (cancellation.IsCancellationRequested || result == DisplayWaitResult.Cancelled)) skip("display.vsync.cancelled");
        else if (readiness.PermissionHeld && end >= deadline) skip("display.vsync.deadline");
        return readiness.PermissionHeld;
    }

    public void BeginAttempt(long slot)
    {
        if (slot <= attemptedSlot) throw new InvalidOperationException("At most one display attempt per compose slot.");
        if (!readiness.PermissionHeld) throw new InvalidOperationException("Display attempt requires a readiness permission.");
        attemptedSlot = slot;
    }
    public string? SkipReason(long imageId) => imageId > LastPresentedId ? null : "display.vsync.noNewerImage";
    public void Presented(long imageId)
    {
        if (imageId <= LastPresentedId) throw new InvalidOperationException("Presented image ids must strictly increase.");
        LastPresentedId = imageId;
    }
}
