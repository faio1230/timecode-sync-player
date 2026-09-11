namespace GpuOutputProbe;

internal static class PresentWaitPlanSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Expected invalid plan to be rejected."); }
    private static Options Plan(string plan) => Options.Parse(["--present-wait-plan", plan, "--seconds", "32", "--warmup", "1"]);

    public static void ValidationAndFixedCompatibility()
    {
        Check(Options.Parse([]).PresentWaitPlan == "fixed", "Default plan changed.");
        Reject(() => Plan("other"));
        Reject(() => Options.Parse(["--present-wait-plan", "abba"])); // 8 seconds per segment cannot exclude 5 seconds on both ends.
        Reject(() => Options.Parse(["--present-wait-plan", "abba", "--seconds", "8", "--warmup", "1"]));
        Reject(() => Options.Parse(["--present-wait-plan", "baab", "--output", "spout", "--seconds", "32", "--warmup", "1"]));
        Reject(() => Options.Parse(["--present-wait-plan", "abba", "--present-wait-ms", "1", "--seconds", "32", "--warmup", "1"]));
        Check(Plan("abba").PresentWaitMs == 0 && Plan("baab").PresentWaitMs == 0, "Segment plan changed standalone fixed request.");
        foreach (int wait in new[] { 0, 1 })
        {
            var options = Options.Parse(["--present-wait-ms", wait.ToString()]);
            var segments = PresentWaitPlanPolicy.Create(options, 500, 1_000_000);
            Check(segments.Length == 1 && segments[0].WaitMs == wait && segments[0].StartQpc == 500 && segments[0].EndQpc == 32_000_500,
                "Fixed plan changed request or run boundaries.");
            Check(segments[0].AnalysisStartSeconds == 5 && segments[0].AnalysisEndSeconds == 27, "Fixed analysis window changed.");
            var cursor = new PresentWaitPlanCursor(segments);
            _ = cursor.Select(500, out bool first); _ = cursor.Select(32_000_499, out bool later);
            Check(first && !later, "Fixed plan must emit exactly one applied-segment notification.");
        }
    }

    public static void BoundariesAndEqualNeighbors()
    {
        foreach (string plan in new[] { "abba", "baab" })
        {
            var segments = PresentWaitPlanPolicy.Create(Plan(plan), 123, 1_000_000);
            int[] expected = plan == "abba" ? [0, 1, 1, 0] : [1, 0, 0, 1];
            var cursor = new PresentWaitPlanCursor(segments);
            for (int i = 0; i < 4; i++)
            {
                var actual = cursor.Select(segments[i].StartQpc, out bool changed);
                Check(changed && actual.Index == i && actual.WaitMs == expected[i], "Boundary/equal-valued segment transition not notified.");
                Check(actual.StartSeconds == i * 8 && actual.EndSeconds == (i + 1) * 8 && actual.AnalysisStartSeconds == i * 8 + 1 && actual.AnalysisEndSeconds == (i + 1) * 8 - 1,
                    "Per-segment excluded windows are incorrect.");
                actual = cursor.Select(segments[i].EndQpc - 1, out changed);
                Check(!changed && actual.Index == i, "Tick just before boundary switched too early.");
            }
            Reject(() => PresentWaitPlanPolicy.Select(segments, segments[0].StartQpc - 1));
            Reject(() => PresentWaitPlanPolicy.Select(segments, segments[^1].EndQpc));
        }
    }

    public static void LateSkipAndFractionalSchedule()
    {
        var options = Plan("abba") with { Seconds = 32.12345, Fps = 59.94 };
        const long origin = 1234567, frequency = 10_000_000;
        var segments = PresentWaitPlanPolicy.Create(options, origin, frequency);
        for (int i = 0; i < 4; i++)
        {
            Check(segments[i].StartQpc == origin + (long)(i * (options.Seconds / 4) * frequency) &&
                segments[i].EndQpc == origin + (long)((i + 1) * (options.Seconds / 4) * frequency), "Fractional seconds boundary was rounded or accumulated differently.");
        }
        var cursor = new PresentWaitPlanCursor(segments); var scheduler = new TickSchedule(origin, options.Fps, frequency);
        var first = scheduler.Take(origin); _ = cursor.Select(first.Scheduled, out bool initial);
        long late = segments[3].StartQpc + frequency / 10;
        var current = scheduler.Take(late);
        var selected = cursor.Select(current.Scheduled, out bool changed);
        Check(initial && changed && selected.Index == 3 && current.Skipped > 100 && scheduler.DueQpc > late,
            "Late tick replayed intermediate segments or lost scheduler skipping.");
        _ = cursor.Select(current.Scheduled, out bool repeated); Check(!repeated, "Repeated selection emitted another transition.");
        // The cycle can begin after a boundary yet have a scheduled timestamp before it.
        var boundary = segments[1].StartQpc;
        Check(PresentWaitPlanPolicy.Select(segments, boundary - 1).Index == 0, "Selection must use scheduled time, not observation time.");
    }

    public static void ReadyPermissionSurvivesSwitch()
    {
        var segments = PresentWaitPlanPolicy.Create(Plan("baab"), 0, 1_000_000);
        var cursor = new PresentWaitPlanCursor(segments); var gate = new PresentReadyGate(); int waits = 0;
        var first = cursor.Select(0, out _);
        Check(gate.TryAcquire(first.WaitMs, 1000, 1_000_000, default, () => 0,
            _ => { waits++; return true; }, _ => { }, _ => { }), "Could not get the initial display permission.");
        // Simulate a draw completed too late to Present: the signal survives into the next request segment.
        Check(PresentReadyGate.SkipReason(1000, 1000, false) == "present.deadline", "Expected skipped Present.");
        var next = cursor.Select(segments[1].StartQpc, out bool changed); PresentReadyAttempt? attempt = null;
        Check(changed && next.WaitMs == 0 && gate.PermissionHeld, "Changing the request reset readiness.");
        Check(gate.TryAcquire(next.WaitMs, segments[1].StartQpc + 1000, 1_000_000, default, () => segments[1].StartQpc,
            _ => throw new InvalidOperationException("Segment switch must not wait again for a retained permission."), a => attempt = a, _ => { }),
            "Retained permission was not usable across the segment switch.");
        Check(waits == 1 && attempt?.Kind == "retained" && attempt.Value.PermissionHeld, "Permission contract changed across segment boundary.");
        gate.ConsumeForPresent(); Check(!gate.PermissionHeld, "Actual presentation no longer consumes permission.");
    }
}
