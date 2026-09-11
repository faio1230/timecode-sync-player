using System.IO;
using System.Text.Json;

namespace GpuOutputProbe;

internal static class DisplayWaitSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static Options Ready(params string[] extra) => Options.Parse(new[] { "--mode", "split", "--output", "both", "--display-pacing", "ready" }.Concat(extra).ToArray());

    public static void OptionsAndBudget()
    {
        Check(Options.Parse([]).DisplayPacing == "tick", "Legacy default changed.");
        Check(Options.Parse(["--present-wait-plan", "abba", "--seconds", "32", "--warmup", "1"]).DisplayPacing == "tick", "Old segmented tick option became invalid.");
        Check(Ready().PresentWaitMs == 0 && Ready().PresentWaitPlan == "fixed", "Ready defaults differ.");
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "unknown"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "ready"]));
        foreach (string output in new[] { "spout", "fullscreen" })
            Throws<ArgumentException>(() => Options.Parse(["--mode", "split", "--output", output, "--display-pacing", "ready"]));
        Throws<ArgumentException>(() => Ready("--present-wait-ms", "1"));
        Throws<ArgumentException>(() => Ready("--present-wait-plan", "abba", "--seconds", "32", "--warmup", "1"));
        Check(DisplayWaitGate.TimeoutMs(0, 16_667, 1_000_000) == 16 && DisplayWaitGate.TimeoutMs(0, 999, 1_000_000) == 0,
            "Remaining wait was not floored to whole milliseconds.");
    }

    public static void ImmediateAndMidTickNotification()
    {
        foreach (long notificationTime in new long[] { 0, 2000 })
        {
            var ready = new PresentReadyGate(); var wait = new DisplayWaitGate(ready); long now = 0; int calls = 0; DisplayWaitAttempt? recorded = null;
            Check(wait.TryForTick(0, 16_667, 1_000_000, default, () => now,
                ms => { calls++; Check(ms == 16, "Incorrect remaining interval."); now = notificationTime; return DisplayWaitResult.Ready; },
                a => recorded = a, _ => throw new InvalidOperationException("Unexpected skip.")), "Ready notification was ignored.");
            Check(calls == 1 && ready.PermissionHeld && recorded?.Outcome == "ready" && recorded.Value.EndQpc == notificationTime,
                "Notification timing/permission differs.");
            Check(!wait.TryForTick(0, 16_667, 1_000_000, default, () => now,
                _ => throw new InvalidOperationException("Cannot wait twice within a tick."), _ => throw new InvalidOperationException("Cannot emit a second pair."), _ => { }),
                "One tick allowed another display attempt.");
            ready.ConsumeForPresent(); Check(!ready.PermissionHeld, "Presentation did not consume external grant.");
        }
    }

    public static void EarlyTimeoutDoesNotRetry()
    {
        var ready = new PresentReadyGate(); var gate = new DisplayWaitGate(ready); long now = 0; int calls = 0; DisplayWaitAttempt? recorded = null;
        Check(!gate.TryForTick(0, 16_667, 1_000_000, default, () => now,
            _ => { calls++; now = 16_000; return DisplayWaitResult.Timeout; }, a => recorded = a, _ => { }), "Timeout permitted presentation.");
        Check(recorded?.Outcome == "timeout" && !ready.PermissionHeld && now < 16_667, "Early timeout did not retain the fractional remainder.");
        Check(!gate.TryForTick(0, 16_667, 1_000_000, default, () => now,
            _ => { calls++; return DisplayWaitResult.Ready; }, _ => { }, _ => { }) && calls == 1,
            "An early timeout retried during the same output slot.");
        now = 16_667;
        Check(gate.TryForTick(16_667, 33_333, 1_000_000, default, () => now,
            _ => { calls++; return DisplayWaitResult.Ready; }, _ => { }, _ => { }) && calls == 2, "Next tick did not regain its single attempt.");
    }

    public static void LateGrantAndNewestLease()
    {
        var pool = new LatestPool(3); int firstSlot = pool.TryBeginWrite(); pool.Publish(firstSlot, new(1, 10), true);
        var ready = new PresentReadyGate(); var gate = new DisplayWaitGate(ready); long now = 0; DisplayWaitAttempt? recorded = null;
        Check(!gate.TryForTick(0, 1000, 1_000_000, default, () => now,
            _ => { Check(pool.PeakReaders == 0, "Source lease was held while waiting."); now = 1000; return DisplayWaitResult.Ready; },
            a => recorded = a, s => Check(s == "display.wait.deadline", "Wrong late skip.")), "Late grant should prioritize composition.");
        Check(ready.PermissionHeld && recorded?.Outcome == "deadline" && recorded.Value.PermissionHeld, "Late signal was lost.");
        Throws<InvalidOperationException>(() => ready.GrantFromNotification());
        int nextSlot = pool.TryBeginWrite(); pool.Publish(nextSlot, new(2, 1000), true);
        Check(gate.TryForTick(1000, 2000, 1_000_000, default, () => now,
            _ => throw new InvalidOperationException("Retained grant must not call native wait."), a => recorded = a, _ => { }), "Retained grant failed next cycle.");
        using var selected = pool.AcquireLatest()!;
        Check(selected.Stamp.Id == 2 && recorded?.Kind == "retained" && recorded.Value.TimeoutMs == 0, "Old image persisted with readiness.");
        // A failed source/key acquisition still uses this slot; do not retry even though permission remains.
        Check(!gate.TryForTick(1000, 2000, 1_000_000, default, () => now,
            _ => throw new InvalidOperationException(), _ => { }, _ => { }) && ready.PermissionHeld, "Source failure retried or lost the grant.");
    }

    public static void StopAndError()
    {
        var expired = new DisplayWaitGate(new PresentReadyGate()); int expiredPairs = 0;
        Check(!expired.TryForTick(0, 1000, 1_000_000, default, () => 1000,
            _ => throw new InvalidOperationException("Expired slot must not wait."), _ => expiredPairs++,
            s => Check(s == "display.wait.deadline", "Wrong pre-expired reason.")) && expiredPairs == 0,
            "Pre-expired slot generated a wait attempt.");
        foreach (bool signalArrives in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(); var ready = new PresentReadyGate(); var gate = new DisplayWaitGate(ready); DisplayWaitAttempt? recorded = null;
            Check(!gate.TryForTick(0, 1000, 1_000_000, stop.Token, () => 0,
                _ => { stop.Cancel(); return signalArrives ? DisplayWaitResult.Ready : DisplayWaitResult.Cancelled; }, a => recorded = a,
                s => Check(s == "display.wait.cancelled", "Wrong stop reason.")), "Stop permitted a display attempt.");
            Check(recorded?.Outcome == "cancelled" && recorded.Value.PermissionHeld == signalArrives && ready.PermissionHeld == signalArrives,
                "Stop/notification priority lost ownership information.");
            int pairs = 0;
            Check(!gate.TryForTick(1000, 2000, 1_000_000, stop.Token, () => 1000,
                _ => throw new InvalidOperationException("Pre-stopped slot must not wait."), _ => pairs++, _ => { }) && pairs == 0,
                "Pre-stopped slot emitted a wait pair.");
            ready.Discard();
        }
        var failureReady = new PresentReadyGate(); var failureGate = new DisplayWaitGate(failureReady); bool recordedError = false;
        Throws<ApplicationException>(() => failureGate.TryForTick(0, 1000, 1_000_000, default, () => 0,
            _ => throw new ApplicationException("Wait failure"), a => recordedError = a.Outcome == "error" && !a.PermissionHeld, _ => { }));
        Check(recordedError && !failureReady.PermissionHeld, "Wait failure was not recorded before faulting.");
    }

    public static void CpuSummaryScope()
    {
        string path = Path.GetFullPath(Path.Combine("TestResults", "gpu-ready-cpu-summary-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path);
        var options = Options.Parse([]) with { LogDir = path };
        var log = new ProbeLog(); log.Save(options, "cancelled", new LatestPool(3), new ProcessCpuSample(123, 456, 1.25));
        using var summary = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "summary.json")));
        Check(summary.RootElement.GetProperty("appCpuSeconds").GetDouble() == 1.25 && summary.RootElement.GetProperty("appCpuStartQpc").GetInt64() == 123 &&
            summary.RootElement.GetProperty("appCpuEndQpc").GetInt64() == 456 && summary.RootElement.GetProperty("appCpuScope").GetString()!.Contains("excludes log serialization"),
            "CPU sample lost its whole-run boundaries or scope.");
    }
}
