namespace GpuOutputProbe;

internal static class PresentReadySelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static void ValidationAndBudget()
    {
        Check(Options.Parse([]).PresentWaitMs == 0, "Default presentation request changed.");
        Check(Options.Parse(["--present-wait-ms", "1"]).PresentWaitMs == 1, "Combined output must accept 1ms.");
        Check(Options.Parse(["--output", "fullscreen", "--present-wait-ms", "1"]).PresentWaitMs == 1, "Fullscreen-only request rejected.");
        Check(Options.Parse(["--output", "spout", "--present-wait-ms", "0"]).PresentWaitMs == 0, "Explicit zero must allow Spout-only.");
        foreach (string invalid in new[] { "-1", "2" }) Throws<ArgumentException>(() => Options.Parse(["--present-wait-ms", invalid]));
        Throws<FormatException>(() => Options.Parse(["--present-wait-ms", "0.5"]));
        Throws<ArgumentException>(() => Options.Parse(["--output", "spout", "--present-wait-ms", "1"]));
        Throws<ArgumentException>(() => Options.Parse(["--present-wait-ms", "0", "--present-wait-ms", "1"]));
        Check(PresentReadyGate.TimeoutMs(1, 0, 999, 1_000_000) == 0, "Fractional remaining millisecond was rounded up.");
        Check(PresentReadyGate.TimeoutMs(1, 0, 1000, 1_000_000) == 1, "One available millisecond was lost.");
        Check(PresentReadyGate.TimeoutMs(0, 0, 100_000, 1_000_000) == 0, "Zero baseline started waiting.");
        var schedule = new TickSchedule(500, 240, 1_000_000);
        _ = schedule.Take(schedule.DueQpc);
        Check(PresentReadyGate.TimeoutMs(1, schedule.DueQpc - 500, schedule.DueQpc, 1_000_000) == 0, "High-fps tail exceeded remaining budget.");
    }

    public static void SingleWaitAndErrorEvidence()
    {
        var gate = new PresentReadyGate(); int waits = 0; PresentReadyAttempt? attempt = null; string? skip = null;
        Check(!gate.TryAcquire(1, 1000, 1_000_000, default, () => 0,
            timeout => { waits++; Check(timeout == 1, "Unexpected request."); return false; }, a => attempt = a, s => skip = s), "Unsignaled event permitted presentation.");
        Check(waits == 1 && skip == "present.notReady" && attempt?.Kind == "native" && attempt.Value.Outcome == "notReady" && !attempt.Value.PermissionHeld,
            "Not-ready acquisition retried or misreported.");
        bool recordedBeforeThrow = false;
        Throws<ApplicationException>(() => gate.TryAcquire(1, 1000, 1_000_000, default, () => 0,
            _ => throw new ApplicationException("Wait failed"), a => { recordedBeforeThrow = a.Outcome == "error" && !a.PermissionHeld; }, _ => { }));
        Check(recordedBeforeThrow && !gate.PermissionHeld, "Wait failure was not recorded before faulting.");
        Throws<InvalidOperationException>(() => gate.ConsumeForPresent());
    }

    public static void LateSignalRetainedForNewestImage()
    {
        var pool = new LatestPool(3); int slot = pool.TryBeginWrite(); pool.Publish(slot, new(1, 1), true);
        var gate = new PresentReadyGate(); long now = 0; int waits = 0; PresentReadyAttempt? attempt = null;
        using (var first = pool.AcquireLatest()!)
        {
            bool allowed = gate.TryAcquire(1, 1000, 1_000_000, default, () => now,
                _ => { waits++; now = 1000; return true; }, a => attempt = a, s => Check(s == "present.deadline", "Wrong late-signal skip."));
            Check(!allowed && gate.PermissionHeld && attempt?.Outcome == "deadline" && attempt.Value.PermissionHeld,
                "Late success lost the permission or started drawing.");
        }
        slot = pool.TryBeginWrite(); pool.Publish(slot, new(2, 2), true);
        using (var latest = pool.AcquireLatest()!)
        {
            Check(latest.Stamp.Id == 2, "Retained permission retained the obsolete image.");
            now = 1100;
            Check(gate.TryAcquire(1, 2100, 1_000_000, default, () => now,
                _ => throw new InvalidOperationException("Retained permission must not wait again."), a => attempt = a, _ => { }), "Retained permission was not usable next cycle.");
            Check(attempt?.Kind == "retained" && attempt.Value.Outcome == "retained" && attempt.Value.TimeoutMs == 0 && waits == 1,
                "Retained attempt evidence or wait count differs.");
            gate.ConsumeForPresent();
        }
        Check(!gate.PermissionHeld, "Actual Present attempt did not consume permission.");
        Check(gate.TryAcquire(0, 3000, 1_000_000, default, () => now,
            timeout => { waits++; Check(timeout == 0, "Zero request changed."); return true; }, _ => { }, _ => { }), "Next signal failed.");
        Check(waits == 2, "A consumed permission bypassed the next native wait.");
        gate.Discard(); Check(!gate.PermissionHeld, "Shutdown did not discard permission.");
    }

    public static void CancellationAndDrawDeadlineRetention()
    {
        using var cancellation = new CancellationTokenSource(); var gate = new PresentReadyGate(); PresentReadyAttempt? attempt = null;
        Check(!gate.TryAcquire(1, 1000, 1_000_000, cancellation.Token, () => 0,
            _ => { cancellation.Cancel(); return true; }, a => attempt = a, s => Check(s == "present.cancelled", "Wrong cancellation skip.")),
            "Cancelled acquisition permitted GPU work.");
        Check(gate.PermissionHeld && attempt?.Outcome == "cancelled" && attempt.Value.PermissionHeld, "Cancellation discarded a successful signal.");
        int records = 0;
        Check(!gate.TryAcquire(1, 1000, 1_000_000, cancellation.Token, () => 0,
            _ => throw new InvalidOperationException("Cancelled work must not wait."), _ => records++, _ => { }), "Cancelled next cycle accepted work.");
        Check(records == 0 && gate.PermissionHeld, "Pre-cancel generated a pair or consumed permission.");

        long now = 100; int waits = 0;
        Check(gate.TryAcquire(1, 1000, 1_000_000, default, () => now, _ => { waits++; return false; },
            _ => now = 1000, _ => { }), "Retained permission unexpectedly failed before diagnostic enqueue.");
        // The engine rechecks this at actual draw.start; diagnostic work must not consume the permission.
        Check(PresentReadyGate.SkipReason(now, 1000, false) == "present.deadline" && gate.PermissionHeld && waits == 0,
            "Pre-draw expiration consumed readiness.");
        // Likewise a completed draw can be skipped before Present without consuming permission.
        Check(PresentReadyGate.SkipReason(1001, 1000, false) == "present.deadline" && gate.PermissionHeld, "Post-draw deadline consumed readiness.");
        gate.Discard();
    }

    public static void StaleBeforeWaitAndOversleep()
    {
        var gate = new PresentReadyGate(); int records = 0;
        Check(!gate.TryAcquire(1, 1000, 1_000_000, default, () => 1000,
            _ => throw new InvalidOperationException("Expired work waited."), _ => records++, s => Check(s == "present.deadline", "Wrong expired skip.")), "Expired work accepted.");
        Check(records == 0 && !gate.PermissionHeld, "Expired attempt emitted a pair or acquired permission.");
        long now = 0; PresentReadyAttempt? attempt = null;
        Check(gate.TryAcquire(1, 10_000, 1_000_000, default, () => now,
            _ => { now = 5000; return true; }, a => attempt = a, _ => throw new InvalidOperationException("Oversleep before deadline must be usable.")),
            "OS oversleep before deadline was rejected.");
        Check(attempt?.EndQpc - attempt?.StartQpc == 5000 && attempt?.TimeoutMs == 1 && gate.PermissionHeld, "Actual readiness interval was lost.");
    }
}
