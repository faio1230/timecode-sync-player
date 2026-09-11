namespace GpuOutputProbe;

// Fake clocks and mutex callbacks only: these tests do not wait on an OS mutex or initialize graphics.
internal static class MutexWaitSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static void OptionsAndClamping()
    {
        Check(Options.Parse([]).MutexWaitMs == 0, "Default changed from zero wait.");
        foreach (int ms in new[] { 0, 1, 4, 8 }) Check(Options.Parse(["--mutex-wait-ms", ms.ToString()]).MutexWaitMs == ms, "Valid option rejected.");
        foreach (string invalid in new[] { "-1", "9" }) Throws<ArgumentException>(() => Options.Parse(["--mutex-wait-ms", invalid]));
        Throws<FormatException>(() => Options.Parse(["--mutex-wait-ms", "1.5"]));
        Throws<ArgumentException>(() => Options.Parse(["--mutex-wait-ms", "1", "--mutex-wait-ms", "0"]));
        Check(MutexWaitPolicy.TimeoutMs(4, 1501, 2000, 1_000_000) == 0, "Submillisecond remainder must floor to zero.");
        Check(MutexWaitPolicy.TimeoutMs(4, 0, 2999, 1_000_000) == 2, "Wait exceeds remaining whole milliseconds.");
        Check(MutexWaitPolicy.TimeoutMs(1, 0, 20_000, 1_000_000) == 1, "Configured cap ignored.");
        Check(MutexWaitPolicy.TimeoutMs(0, 0, 20_000, 1_000_000) == 0, "Zero baseline must not wait.");
        Check(MutexWaitPolicy.TimeoutMs(4, 3000, 2000, 1_000_000) == 0, "Expired work received a new budget.");
    }

    public static void EightMillisecondBudget()
    {
        const long frequency = 1_000_000;
        Check(MutexWaitPolicy.TimeoutMs(8, 0, 7999, frequency) == 7, "7.999 ms remainder must clamp to seven whole milliseconds.");
        Check(MutexWaitPolicy.TimeoutMs(8, 0, 20_000, frequency) == 8, "Eight millisecond request was not allowed.");
        Throws<ArgumentOutOfRangeException>(() => MutexWaitPolicy.TimeoutMs(9, 0, 20_000, frequency));

        var highRate = new TickSchedule(123, 240, frequency);
        var tick = highRate.Take(highRate.DueQpc);
        Check(MutexWaitPolicy.TimeoutMs(8, tick.Scheduled + 1000, highRate.DueQpc, frequency) == 3,
            "High-fps deadline did not override the larger configured request.");
        Check(MutexWaitPolicy.TimeoutMs(8, highRate.DueQpc - 999, highRate.DueQpc, frequency) == 0,
            "Submillisecond high-fps budget must remain zero wait.");

        long now = 0; int waits = 0, releases = 0; string? reason = null; MutexAttempt? attempt = null;
        var lease = SendMutexGate.Acquire(8, 8000, frequency, default, () => now,
            timeout => { Check(timeout == 8, "Expected one eight-millisecond OS request."); waits++; now = 8000; return true; },
            () => releases++, a => attempt = a, skip => reason = skip);
        Check(lease == null && waits == 1 && releases == 1 && reason == "send.deadlineExpired",
            "Acquisition at the deadline must release once and not allow a send.");
        Check(attempt?.Outcome == "acquired" && attempt.Value.TimeoutMs == 8 && attempt.Value.EndQpc == 8000,
            "Successful but too-late acquisition must retain its actual outcome and timing.");
    }

    public static void FractionalDeadline()
    {
        const long frequency = 1_000_000;
        var scheduler = new TickSchedule(123, 59.94, frequency);
        for (int i = 0; i < 1000; i++)
        {
            var tick = scheduler.Take(scheduler.DueQpc);
            long actualDeadline = scheduler.DueQpc;
            long approximated = tick.Scheduled + (long)Math.Round(frequency / 59.94);
            if (actualDeadline <= approximated) continue;
            long now = actualDeadline - 1000;
            MutexAttempt? recorded = null;
            int released = 0;
            using var lease = SendMutexGate.Acquire(4, actualDeadline, frequency, default, () => now,
                timeout => { Check(timeout == 1, "Rounded-period approximation lost the true next deadline."); return true; },
                () => released++, attempt => recorded = attempt, _ => throw new InvalidOperationException("Unexpected skip."));
            Check(recorded?.DeadlineQpc == actualDeadline && recorded.Value.TimeoutMs == 1, "True fractional deadline not carried into evidence.");
            lease!.Dispose(); Check(released == 1, "Acquired resource was not returned."); return;
        }
        throw new InvalidOperationException("Test failed to exercise fractional period rounding.");
    }

    public static void StaleAndCancelledBeforeWait()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource(); if (cancel) cts.Cancel();
            string? reason = null;
            var lease = SendMutexGate.Acquire(1, 1000, 1_000_000, cts.Token, () => cancel ? 0 : 1000,
                _ => throw new InvalidOperationException("Must not acquire stale/cancelled work."),
                () => throw new InvalidOperationException("Nothing was acquired."),
                _ => throw new InvalidOperationException("No OS wait happened."), x => reason = x);
            Check(lease == null && reason == (cancel ? "send.cancelled" : "send.deadlineExpired"), "Wrong skip before acquisition.");
        }
    }

    public static void LateAndCancelledAcquisition()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource(); long now = 0; int releases = 0, waits = 0;
            string? skip = null; MutexAttempt? observed = null;
            var lease = SendMutexGate.Acquire(1, 10_000, 1_000_000, cts.Token, () => now,
                _ => { waits++; now = cancel ? 2000 : 10_000; if (cancel) cts.Cancel(); return true; },
                () => releases++, x => observed = x, x => skip = x);
            Check(lease == null && waits == 1 && releases == 1, "Rejected ownership must be released exactly once without a second attempt.");
            Check(skip == (cancel ? "send.cancelled" : "send.deadlineExpired") && observed?.Outcome == "acquired", "Actual acquired outcome was lost.");
        }
        // Logging itself can consume the remaining tick budget; that does not authorize late GPU work.
        long clock = 0; int released = 0; string? reason = null;
        var afterLog = SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => clock, _ => true,
            () => released++, _ => clock = 1000, s => reason = s);
        Check(afterLog == null && released == 1 && reason == "send.deadlineExpired", "Logging latency resurrected old work.");
    }

    public static void OversleepBeforeDeadlineAndBusy()
    {
        long now = 0; int waits = 0, releases = 0; MutexAttempt? attempt = null;
        using var lease = SendMutexGate.Acquire(1, 10_000, 1_000_000, default, () => now,
            timeout => { Check(timeout == 1, "Configured wait not passed."); waits++; now = 5000; return true; },
            () => releases++, x => attempt = x, _ => throw new InvalidOperationException("An oversleep before deadline should be accepted."));
        Check(lease != null && attempt?.EndQpc - attempt?.StartQpc == 5000 && waits == 1, "Actual OS duration was not retained.");
        lease!.Dispose(); lease.Dispose(); Check(releases == 1, "Repeated dispose released mutex twice.");

        string? reason = null; now = 0; waits = 0;
        var busy = SendMutexGate.Acquire(1, 10_000, 1_000_000, default, () => now,
            _ => { waits++; now = 1000; return false; }, () => throw new InvalidOperationException("Busy mutex is not owned."),
            x => Check(x.Outcome == "busy", "Busy outcome missing."), x => reason = x);
        Check(busy == null && waits == 1 && reason == "send.accessMutexBusy", "Busy path retried or mislabeled.");
    }

    public static void AbandonedAndExceptionalOwnership()
    {
        int releases = 0; MutexAttempt? attempt = null;
        Throws<InvalidOperationException>(() => SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => 0,
            _ => throw new AbandonedMutexException(), () => releases++, x => attempt = x,
            _ => throw new InvalidOperationException("Abandonment must fault, not skip.")));
        Check(releases == 1 && attempt?.Outcome == "abandoned", "Abandoned mutex ownership/evidence lost.");

        releases = 0;
        Throws<ApplicationException>(() => SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => 0,
            _ => true, () => releases++, _ => throw new ApplicationException("Logging callback failed"), _ => { }));
        Check(releases == 1, "Exception after acquisition leaked ownership.");
        int calls = 0;
        var lease = new SendMutexLease(() => { calls++; throw new ApplicationException("Release failed"); });
        Throws<ApplicationException>(() => lease.Dispose()); lease.Dispose();
        Check(calls == 1, "A failed release was retried without ownership proof.");
    }
}
