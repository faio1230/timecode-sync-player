namespace GpuOutputProbe;

// Fake clocks and fake waits only: no D3D device, fence, swapchain, or Spout call is made here.
internal static class FenceVsyncSelfTests
{
    private const long Frequency = 1_000_000; // 1000 ticks per millisecond.
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    public static void SourceSyncOptions()
    {
        Check(Options.Parse([]).SourceSync == "keyed", "Default source sync changed.");
        Check(Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "fence"]).SourceSync == "fence", "Fence rejected for split/both.");
        Check(Options.Parse(["--mode", "split", "--output", "spout", "--source-sync", "fence"]).SourceSync == "fence", "Fence rejected for split/spout.");
        Throws<ArgumentException>(() => Options.Parse(["--source-sync", "other"]));
        Throws<ArgumentException>(() => Options.Parse(["--mode", "common", "--output", "both", "--source-sync", "fence"]));
        Throws<ArgumentException>(() => Options.Parse(["--mode", "split", "--output", "fullscreen", "--source-sync", "fence"]));
        Throws<ArgumentException>(() => Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "fence", "--copy-retry", "signal"]));
        Check(Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "keyed", "--copy-retry", "signal"]).CopyRetry == "signal", "Keyed retry combination rejected.");
        Check(Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "fence", "--display-pacing", "vsync"]).DisplayPacing == "vsync", "Fence and vsync must combine.");
    }

    public static void VsyncOptions()
    {
        Check(Options.Parse(["--display-pacing", "vsync"]).DisplayPacing == "vsync", "Common/both vsync rejected.");
        Check(Options.Parse(["--mode", "split", "--output", "both", "--display-pacing", "vsync"]).DisplayPacing == "vsync", "Split/both vsync rejected.");
        Check(Options.Parse(["--output", "fullscreen", "--display-pacing", "vsync"]).DisplayPacing == "vsync", "Fullscreen-only vsync rejected.");
        Throws<ArgumentException>(() => Options.Parse(["--output", "spout", "--display-pacing", "vsync"]));
        Throws<ArgumentException>(() => Options.Parse(["--mode", "split", "--output", "spout", "--display-pacing", "vsync"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vsync", "--present-wait-ms", "1"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vsync", "--present-wait-plan", "abba", "--seconds", "32", "--warmup", "1"]));
        Check(Options.Parse(["--display-pacing", "tick"]).DisplayPacing == "tick" && Options.Parse(["--mode", "split", "--display-pacing", "ready"]).DisplayPacing == "ready", "Existing pacing options changed.");
    }

    public static void FenceValueMonotonic()
    {
        var sequence = new FenceSequence();
        Check(sequence.Next(1) == 1 && sequence.Next(2) == 2 && sequence.Next(5) == 5 && sequence.Last == 5, "Increasing fence values were rejected.");
        Throws<InvalidOperationException>(() => sequence.Next(5));
        Throws<InvalidOperationException>(() => sequence.Next(4));
        Throws<InvalidOperationException>(() => new FenceSequence().Next(0));
        Check(sequence.Last == 5, "A rejected value advanced the sequence.");
    }

    public static void VsyncNoNewerImageNeverWaits()
    {
        var ready = new PresentReadyGate(); var gate = new VsyncDisplayGate(ready);
        Check(gate.Decide(0, 100, 16_667, 0, false, Frequency).Step == VsyncStep.Idle, "Waited on readiness without any image.");
        Check(gate.Decide(0, 100, 16_667, 1, false, Frequency) == (VsyncStep.WaitReady, 16), "Newer image did not request a floored-millisecond wait.");
        Check(gate.Decide(0, 16_000, 16_667, 1, false, Frequency).Step == VsyncStep.Idle, "Sub-millisecond remainder requested a zero-timeout native wait.");
        Check(gate.Wait(0, 16_667, 16, default, () => 100, _ => DisplayWaitResult.Ready, _ => { }, _ => throw new InvalidOperationException("Unexpected skip.")), "Ready notification was not granted.");
        gate.BeginAttempt(0); ready.ConsumeForPresent(); gate.Presented(1); // DisplayTarget.Present consumes the permission.
        Check(gate.LastPresentedId == 1 && !ready.PermissionHeld, "Present did not consume the grant.");
        Check(gate.Decide(0, 5000, 16_667, 1, false, Frequency).Step == VsyncStep.Idle, "Same image re-armed a wait on a possibly signaled handle.");
        Check(gate.SkipReason(1) == "display.vsync.noNewerImage" && gate.SkipReason(2) == null, "Newer-image rule differs.");
    }

    public static void VsyncOnePresentPerNotification()
    {
        var ready = new PresentReadyGate(); var gate = new VsyncDisplayGate(ready); int waits = 0; DisplayWaitAttempt? recorded = null;
        Check(gate.Decide(0, 0, 16_667, 1, false, Frequency).Step == VsyncStep.WaitReady, "First image should wait for the notification.");
        Check(gate.Wait(0, 16_667, 16, default, () => 4000, _ => { waits++; return DisplayWaitResult.Ready; }, a => recorded = a, _ => { }), "Grant missing.");
        Check(recorded?.Kind == "native" && recorded.Value.Outcome == "ready" && recorded.Value.TimeoutMs == 16 && recorded.Value.PermissionHeld && recorded.Value.DeadlineQpc == 16_667, "Wait evidence differs.");
        Check(gate.Decide(0, 4000, 16_667, 1, false, Frequency).Step == VsyncStep.Present, "Held permission with a newer image must present without waiting.");
        gate.BeginAttempt(0); ready.ConsumeForPresent(); gate.Presented(1);
        Throws<InvalidOperationException>(() => gate.BeginAttempt(0));
        // Next slot, next image: the consumed notification cannot be reused; a new native wait is required.
        Check(gate.Decide(16_667, 17_000, 33_333, 2, false, Frequency).Step == VsyncStep.WaitReady && waits == 1, "A second present reused a consumed notification.");
        Throws<InvalidOperationException>(() => gate.Presented(1));
        Throws<InvalidOperationException>(() => gate.Presented(0));
    }

    public static void VsyncComposePriority()
    {
        var ready = new PresentReadyGate(); var gate = new VsyncDisplayGate(ready); string? skipped = null;
        Check(gate.Decide(0, 16_667, 16_667, 1, false, Frequency).Step == VsyncStep.Compose, "Due compose lost to display.");
        Check(gate.Decide(0, 20_000, 16_667, 1, false, Frequency).Step == VsyncStep.Compose, "Late compose lost to display even with permission unheld.");
        // Notification arrives after the compose deadline: keep the grant, skip this slot, present next slot without a new wait.
        Check(gate.Wait(0, 16_667, 16, default, () => 16_700, _ => DisplayWaitResult.Ready, _ => { }, s => skipped = s), "Late grant was dropped.");
        Check(skipped == "display.vsync.deadline" && ready.PermissionHeld, "Late grant must be recorded as a deadline skip and retained.");
        Check(gate.Decide(0, 16_700, 16_667, 1, false, Frequency).Step == VsyncStep.Compose, "Retained grant must not delay the due compose.");
        Check(gate.Decide(16_667, 16_900, 33_333, 2, false, Frequency).Step == VsyncStep.Present, "Retained grant did not present in the next slot.");
        gate.BeginAttempt(16_667); ready.ConsumeForPresent(); gate.Presented(2);
        // A retained grant with a busy/failed attempt in this slot waits for the next slot; no second selection here.
        var again = new PresentReadyGate(); var busy = new VsyncDisplayGate(again);
        busy.Wait(0, 16_667, 16, default, () => 2000, _ => DisplayWaitResult.Ready, _ => { }, _ => { });
        busy.BeginAttempt(0);
        Check(busy.Decide(0, 3000, 16_667, 1, false, Frequency).Step == VsyncStep.Idle && again.PermissionHeld, "Failed attempt retried within the same slot.");
        Check(busy.Decide(16_667, 17_000, 33_333, 1, false, Frequency).Step == VsyncStep.Present, "Retained grant was lost across the slot boundary.");
    }

    public static void VsyncOneWaitPerSlotAndTimeout()
    {
        var ready = new PresentReadyGate(); var gate = new VsyncDisplayGate(ready); DisplayWaitAttempt? recorded = null;
        Check(!gate.Wait(0, 16_667, 16, default, () => 10_000, _ => DisplayWaitResult.Timeout, a => recorded = a, _ => throw new InvalidOperationException("Timeout must not skip.")), "Timeout granted permission.");
        Check(recorded?.Outcome == "timeout" && !recorded.Value.PermissionHeld, "Timeout evidence differs.");
        Check(gate.Decide(0, 10_000, 16_667, 1, false, Frequency).Step == VsyncStep.Idle, "An early timeout re-waited within the same slot.");
        Throws<InvalidOperationException>(() => gate.Wait(0, 16_667, 6, default, () => 10_000, _ => DisplayWaitResult.Ready, _ => { }, _ => { }));
        Check(gate.Decide(16_667, 17_000, 33_333, 1, false, Frequency).Step == VsyncStep.WaitReady, "Next slot did not regain its wait.");
    }

    public static void VsyncStopPriorityAndError()
    {
        var ready = new PresentReadyGate(); var gate = new VsyncDisplayGate(ready);
        Check(gate.Decide(0, 100, 16_667, 1, true, Frequency).Step == VsyncStep.Stop, "Stop lost to display.");
        DisplayWaitAttempt? recorded = null; string? skipped = null;
        Check(!gate.Wait(0, 16_667, 16, default, () => 100, _ => DisplayWaitResult.Cancelled, a => recorded = a, s => skipped = s), "Cancelled wait granted permission.");
        Check(recorded?.Outcome == "cancelled" && !recorded.Value.PermissionHeld && skipped == null, "Index-0 stop evidence differs.");
        using var stop = new CancellationTokenSource();
        var both = new VsyncDisplayGate(ready);
        Check(both.Wait(16_667, 33_333, 16, stop.Token, () => 17_000, _ => { stop.Cancel(); return DisplayWaitResult.Ready; }, a => recorded = a, s => skipped = s), "Grant alongside stop was lost.");
        Check(recorded?.Outcome == "ready" && recorded.Value.PermissionHeld && skipped == "display.vsync.cancelled" && both.Decide(16_667, 17_000, 33_333, 1, stop.IsCancellationRequested, Frequency).Step == VsyncStep.Stop,
            "Stop after a grant did not keep ownership evidence and stop.");
        ready.Discard();
        var failing = new VsyncDisplayGate(new PresentReadyGate()); bool recordedError = false;
        Throws<ApplicationException>(() => failing.Wait(0, 16_667, 16, default, () => 0, _ => throw new ApplicationException("Wait failure"), a => recordedError = a.Outcome == "error" && !a.PermissionHeld, _ => { }));
        Check(recordedError, "Wait failure was not recorded before faulting.");
        Throws<InvalidOperationException>(() => new VsyncDisplayGate(new PresentReadyGate()).BeginAttempt(0));
    }
}
