using System.Runtime.ExceptionServices;

namespace GpuOutputProbe;

internal enum VblankStep { Stop, Compose, Bootstrap, Idle, WaitTarget, Present }
internal enum VblankWaitResult { Reached, Cancelled }
internal readonly record struct VblankPrediction(long TargetQpc, long VblankQpc, long PredictedRefresh, long PeriodTicks);
internal readonly record struct VblankWaitAttempt(long StartQpc, long EndQpc, long DeadlineQpc, string Kind, string Outcome, long RequestedMicroseconds, long LatenessMicroseconds);

// Pure decision logic for vblank-phased display (`--display-pacing vblank`). Single GPU-worker owner.
// Phase comes from present.scanout observations (SyncQPCTime, SyncRefreshCount); the next vblank is
// lastSyncQpc + k*period and the newest image is presented `margin` before it. Before the first
// observation the gate bootstraps: one present right after each compose. Stop wins while deciding; a passed target whose
// vblank is still reachable (at least Lead before it) presents before a due compose, otherwise compose deadlines win;
// the draw/present guards of a predicted attempt use the predicted vblank (PresentDeadline), not the compose tick.
// Presents are de-duplicated by predicted vblank time (at least half a period apart); PredictedRefresh is informational only.
// No lease, keyed mutex, or fence wait is held while deciding or waiting.
internal sealed class VblankDisplayGate
{
    private const int PeriodSamples = 8;
    // Draw+present budget before the vblank: measured draw+present p99 ~0.4 ms at 4K. A target reached later than
    // vblank - Lead cannot make its vblank and is re-predicted instead of presenting into it.
    private const double LeadMs = 1;
    private readonly long frequency, marginTicks, leadTicks;
    private readonly List<long> periods = new();
    private long lastSyncQpc; private uint lastSyncRefresh;
    private VblankPrediction? pending, attempt;
    private bool armed;
    private long attemptedSlot = long.MinValue;
    public long PeriodTicks { get; private set; }
    public long MarginTicks => marginTicks;
    public long LeadTicks => leadTicks;
    public bool HasScanout { get; private set; }
    public long LastPresentedId { get; private set; }
    public long LastPresentedVblankQpc { get; private set; } = long.MinValue;
    public VblankPrediction? AttemptPrediction => attempt;
    public VblankPrediction? Pending => pending;

    public VblankDisplayGate(double marginMs, double refreshHz, long frequency)
    {
        if (frequency <= 0 || !double.IsFinite(marginMs) || marginMs <= 0) throw new ArgumentOutOfRangeException();
        this.frequency = frequency;
        marginTicks = Math.Max(1, (long)Math.Round(marginMs * frequency / 1000));
        leadTicks = Math.Max(1, (long)Math.Round(LeadMs * frequency / 1000));
        PeriodTicks = Math.Max(1, (long)Math.Round(frequency / (double.IsFinite(refreshHz) && refreshHz > 0 ? refreshHz : 60))); // Initial estimate until statistics arrive.
    }

    // One present.scanout observation. Non-advancing refresh/time is ignored. The period estimate is the median of the
    // last eight per-refresh differences (a jump of n refreshes contributes its average, so skipped observations do not bias it).
    public void ObserveScanout(long syncQpc, uint syncRefresh)
    {
        if (HasScanout)
        {
            if (syncRefresh <= lastSyncRefresh || syncQpc <= lastSyncQpc) return;
            long perRefresh = (syncQpc - lastSyncQpc) / (syncRefresh - lastSyncRefresh);
            if (perRefresh <= 0) return;
            if (periods.Count == PeriodSamples) periods.RemoveAt(0);
            periods.Add(perRefresh);
            var sorted = periods.Order().ToArray();
            PeriodTicks = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
        }
        lastSyncQpc = syncQpc; lastSyncRefresh = syncRefresh; HasScanout = true;
    }

    // Smallest k with lastSyncQpc + k*period - margin > now; never a vblank whose target has already passed.
    public VblankPrediction Predict(long now)
    {
        if (!HasScanout) throw new InvalidOperationException("No scanout statistics yet.");
        long delta = now + marginTicks - lastSyncQpc;
        long k = delta < 0 ? 1 : delta / PeriodTicks + 1;
        long vblank = lastSyncQpc + k * PeriodTicks;
        return new(vblank - marginTicks, vblank, lastSyncRefresh + k, PeriodTicks);
    }

    // A prediction whose vblank is within half a period of the last presented vblank is the same vblank (labels may be off by one).
    public bool Presentable(VblankPrediction p) => p.VblankQpc > LastPresentedVblankQpc + PeriodTicks / 2;
    // Draw/present guard deadline: the predicted vblank of the current attempt (bootstrap: the compose tick), never past the common end.
    public long PresentDeadline(long nextScheduled, long commonEnd) => Math.Min(attempt?.VblankQpc ?? nextScheduled, commonEnd);

    // slot: scheduledQpc of the last compose tick. composeDue: next compose deadline (or common end).
    // WaitTarget returns the wake deadline min(target, composeDue) and which one it is (`target`/`compose`).
    public (VblankStep Step, long DeadlineQpc, string Kind) Decide(long slot, long now, long composeDue, long latestId, bool cancelled)
    {
        armed = false;
        if (cancelled) return (VblankStep.Stop, 0, "");
        bool newer = latestId > LastPresentedId && slot != attemptedSlot;
        // A passed target with a reachable vblank presents before a due compose: composing first would replace the image this
        // vblank should show with a newer one (image N skipped, N+1 duplicated at the next vblank; 4K hardware trace).
        if (pending is { } p && now >= p.TargetQpc && newer)
        {
            if (now <= p.VblankQpc - leadTicks && Presentable(p)) { attempt = p; armed = true; return (VblankStep.Present, p.VblankQpc, ""); }
            pending = null; // Unreachable (vblank within the lead or passed) or already presented: re-predict, never catch up.
        }
        if (now >= composeDue) return (VblankStep.Compose, 0, "");
        if (!newer) return (VblankStep.Idle, 0, "");
        if (!HasScanout) { attempt = null; armed = true; return (VblankStep.Bootstrap, 0, ""); }
        var next = Predict(now);
        if (!Presentable(next)) return (VblankStep.Idle, 0, "");
        pending = next;
        return next.TargetQpc < composeDue ? (VblankStep.WaitTarget, next.TargetQpc, "target") : (VblankStep.WaitTarget, composeDue, "compose");
    }

    // waitUntil(startQpc, deadlineQpc) blocks on the stop handle and the timer only. Returns true when the deadline was reached.
    public bool Wait(long deadline, string kind, Func<long> timestamp, Func<long, long, VblankWaitResult> waitUntil, Action<VblankWaitAttempt> record)
    {
        if (kind is not ("target" or "compose")) throw new ArgumentException("Wait kind must be target or compose.");
        long start = timestamp();
        long requested = Math.Max(0, (deadline - start) * 1_000_000 / frequency);
        VblankWaitResult result = VblankWaitResult.Reached; Exception? error = null; long end;
        try { result = waitUntil(start, deadline); }
        catch (Exception e) { error = e; }
        finally { end = timestamp(); }
        string outcome = error != null ? "error" : result == VblankWaitResult.Cancelled ? "cancelled" : kind;
        long lateness = outcome == "target" ? (end - deadline) * 1_000_000 / frequency : 0;
        record(new(start, end, deadline, kind, outcome, requested, lateness));
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result == VblankWaitResult.Reached;
    }

    public void BeginAttempt(long slot)
    {
        if (slot <= attemptedSlot) throw new InvalidOperationException("At most one display attempt per compose slot.");
        if (!armed) throw new InvalidOperationException("Display attempt requires a Bootstrap or Present decision.");
        attemptedSlot = slot; armed = false;
    }
    // A Present/Bootstrap decision that was not attempted (latency waitable unsignaled): the prediction is dropped so the
    // next Decide targets a later vblank instead of retrying inside the margin. The slot's attempt stays available.
    public void Defer() { pending = null; attempt = null; armed = false; }
    public string? SkipReason(long imageId) => imageId > LastPresentedId ? null : "display.vblank.noNewerImage";
    public void Presented(long imageId)
    {
        if (imageId <= LastPresentedId) throw new InvalidOperationException("Presented image ids must strictly increase.");
        if (attempt is { } a)
        {
            if (!Presentable(a)) throw new InvalidOperationException("At most one present per predicted vblank.");
            LastPresentedVblankQpc = a.VblankQpc; pending = null;
        }
        LastPresentedId = imageId; attempt = null;
    }
}
