using System.Diagnostics;

namespace GpuOutputProbe;

// Fake clocks, fake statistics, and fake waits only: no D3D device, swapchain, or Spout call is made here.
internal static class VblankSelfTests
{
    private const long Frequency = 1_000_000; // 1000 ticks per millisecond; 60 Hz period 16_667, 3 ms margin 3000.
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static VblankDisplayGate Gate(double marginMs = 3, double refreshHz = 60) => new(marginMs, refreshHz, Frequency);

    public static void VblankOptions()
    {
        var o = Options.Parse(["--display-pacing", "vblank"]);
        Check(o.DisplayPacing == "vblank" && o.PresentMarginMs == 3, "Default vblank options differ.");
        Check(Options.Parse(["--output", "fullscreen", "--display-pacing", "vblank", "--present-margin-ms", "0.5"]).PresentMarginMs == 0.5, "Minimum margin rejected.");
        Check(Options.Parse(["--mode", "split", "--output", "both", "--source-sync", "fence", "--display-pacing", "vblank", "--present-margin-ms", "8"]).PresentMarginMs == 8, "Fence + vblank + maximum margin rejected.");
        Check(Options.Parse([]).PresentMarginMs == 3 && Options.Parse(["--present-margin-ms", "3"]).DisplayPacing == "tick", "Default margin with tick pacing rejected.");
        Throws<ArgumentException>(() => Options.Parse(["--output", "spout", "--display-pacing", "vblank"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vblank", "--present-wait-ms", "1"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vblank", "--present-wait-plan", "abba", "--seconds", "32", "--warmup", "1"]));
        foreach (string margin in new[] { "0.4", "8.1", "NaN", "Infinity", "0", "-1" })
            Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vblank", "--present-margin-ms", margin]));
        Throws<ArgumentException>(() => Options.Parse(["--present-margin-ms", "2"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "vsync", "--present-margin-ms", "2"]));
        Throws<ArgumentException>(() => Options.Parse(["--display-pacing", "other"]));
    }

    public static void MarginAndPeriodDefaults()
    {
        Throws<ArgumentOutOfRangeException>(() => new VblankDisplayGate(0, 60, Frequency));
        Throws<ArgumentOutOfRangeException>(() => new VblankDisplayGate(double.NaN, 60, Frequency));
        Throws<ArgumentOutOfRangeException>(() => new VblankDisplayGate(3, 60, 0));
        var gate = Gate(0.5, 0);
        Check(gate.MarginTicks == 500 && gate.PeriodTicks == 16_667 && !gate.HasScanout, "Unknown refresh must fall back to 60 Hz until statistics arrive.");
        Check(Gate(8, 120).PeriodTicks == 8333 && Gate().MarginTicks == 3000, "Initial period/margin ticks differ.");
        Throws<InvalidOperationException>(() => Gate().Predict(0));
    }

    public static void PredictionAndLateArrival()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        var p = gate.Predict(100_500);
        Check(p == new VblankPrediction(113_667, 116_667, 11, 16_667), "First prediction differs.");
        Check(gate.Predict(113_666).PredictedRefresh == 11 && gate.Predict(113_667).PredictedRefresh == 12, "k must be the smallest with target strictly after now.");
        Check(gate.Predict(50_000).VblankQpc == 116_667, "A now before the last vblank must still predict the next vblank.");
        Check(gate.Decide(0, 100_500, 116_000, 1, false) == (VblankStep.WaitTarget, 113_667, "target"), "Target ahead of compose deadline must wait for the target.");
        // Late arrival: the vblank has passed, so the next one is predicted; no catch-up present.
        var late = gate.Decide(0, 117_000, 133_000, 1, false);
        Check(late == (VblankStep.WaitTarget, 130_334, "target") && gate.Pending?.PredictedRefresh == 12, "Late arrival did not re-predict the next vblank.");
        var present = gate.Decide(0, 130_400, 133_000, 1, false);
        Check(present.Step == VblankStep.Present && present.DeadlineQpc == 133_334 && gate.AttemptPrediction?.PredictedRefresh == 12, "Reached target did not present.");
    }

    public static void MedianPeriodAfterJump()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        Check(gate.HasScanout && gate.PeriodTicks == 16_667, "First observation must keep the initial period.");
        gate.ObserveScanout(116_600, 11); Check(gate.PeriodTicks == 16_600, "Single sample median differs.");
        gate.ObserveScanout(133_300, 12); Check(gate.PeriodTicks == 16_650, "Two-sample median differs.");
        gate.ObserveScanout(166_700, 14); Check(gate.PeriodTicks == 16_700, "A two-refresh jump must contribute its per-refresh average.");
        gate.ObserveScanout(216_700, 15); Check(gate.PeriodTicks == 16_700, "An outlier gap must not move the median.");
        gate.ObserveScanout(216_700, 15); gate.ObserveScanout(216_000, 16); gate.ObserveScanout(300_000, 15);
        Check(gate.PeriodTicks == 16_700 && gate.Predict(216_700) == new VblankPrediction(230_400, 233_400, 16, 16_700), "Non-advancing observations must be ignored.");
        for (int i = 1; i <= 8; i++) gate.ObserveScanout(216_700 + i * 16_680, (uint)(15 + i));
        Check(gate.PeriodTicks == 16_680, "Old samples must leave the eight-sample window.");
    }

    public static void OnePresentPerPredictedVblank()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        Check(gate.Decide(0, 100_500, 116_000, 1, false).Step == VblankStep.WaitTarget, "Wait expected.");
        Throws<InvalidOperationException>(() => gate.BeginAttempt(0)); // Not armed by a Present/Bootstrap decision.
        Check(gate.Decide(0, 113_700, 116_000, 1, false).Step == VblankStep.Present, "Present expected at target.");
        gate.BeginAttempt(0);
        Throws<InvalidOperationException>(() => gate.BeginAttempt(0));
        Check(gate.SkipReason(1) == null && gate.SkipReason(0) == "display.vblank.noNewerImage", "Newer-image rule differs.");
        gate.Presented(1);
        Check(gate.LastPresentedId == 1 && gate.LastPresentedVblankQpc == 116_667 && gate.Pending == null && gate.AttemptPrediction == null, "Presented state differs.");
        Throws<InvalidOperationException>(() => gate.Presented(1));
        Check(gate.Decide(0, 113_800, 116_000, 2, false).Step == VblankStep.Idle, "Second attempt in the same compose slot.");
        var next = gate.Decide(16_667, 116_700, 133_334, 2, false);
        Check(next == (VblankStep.WaitTarget, 130_334, "target") && gate.Pending?.PredictedRefresh == 12, "Next slot must target the next refresh.");
        Check(gate.Decide(16_667, 130_400, 133_334, 2, false).Step == VblankStep.Present, "Present expected at the next target.");
        gate.BeginAttempt(16_667); gate.Presented(2);
        Check(gate.LastPresentedVblankQpc == 133_334, "Presented vblank must advance by one period per present.");
    }

    // Defect fixes: presents are de-duplicated by predicted vblank time, not by the refresh label (SyncRefreshCount numbering
    // can be one off the gate's labels), and a predicted attempt's draw/present guards use the predicted vblank, not the compose tick.
    public static void TimeDedupeAndPresentDeadline()
    {
        var gate = Gate();
        Check(gate.PresentDeadline(16_667, 1_000_000) == 16_667 && gate.PresentDeadline(16_667, 10_000) == 10_000, "Bootstrap deadline must be min(compose tick, common end).");
        for (int i = 0; i <= 8; i++) gate.ObserveScanout(100_000 - (8 - i) * 16_667, (uint)(2 + i)); // Full median window at 16_667; base (100_000, 10).
        // Observations lag: with (100_000, 10) still the base, the attempt at 130_400 targets the second vblank ahead (133_334, label 12).
        Check(gate.Decide(16_667, 116_700, 133_334, 1, false) == (VblankStep.WaitTarget, 130_334, "target") && gate.Pending?.PredictedRefresh == 12, "Second-vblank-ahead prediction differs.");
        Check(gate.Decide(16_667, 130_400, 133_334, 1, false).Step == VblankStep.Present, "Present expected at target.");
        gate.BeginAttempt(16_667); gate.Presented(1);
        Check(gate.LastPresentedVblankQpc == 133_334, "Presented vblank differs.");
        // (a) The statistics label that vblank 11 (one lower than predicted) and count one refresh over two periods (hardware
        // 1080p run): the median absorbs the sample and the next vblank (150_001) must stay presentable although its label repeats 12.
        gate.ObserveScanout(133_334, 11);
        Check(gate.PeriodTicks == 16_667, "Median must absorb the double-period sample.");
        Check(gate.Decide(33_334, 133_400, 150_001, 2, false) == (VblankStep.WaitTarget, 147_001, "target") && gate.Pending == new VblankPrediction(147_001, 150_001, 12, 16_667), "A refresh label one lower than predicted must not skip the next vblank.");
        // (c) The attempt's draw/present deadline is the predicted vblank (150_001), not the compose tick (148_000); the common end still caps it.
        Check(gate.Decide(33_334, 147_100, 148_000, 2, false).Step == VblankStep.Present, "Present expected despite the repeated refresh label.");
        gate.BeginAttempt(33_334);
        Check(gate.PresentDeadline(148_000, 1_000_000) == 150_001 && gate.PresentDeadline(148_000, 149_000) == 149_000, "Predicted attempt deadline must be min(predicted vblank, common end), not the compose tick.");
        gate.Presented(2);
        Check(gate.LastPresentedVblankQpc == 150_001 && gate.PresentDeadline(148_000, 1_000_000) == 148_000, "Without an attempt the deadline is the compose tick.");
        // (b) A prediction within half a period of the last presented vblank is the same vblank: Idle; a later one is presentable again.
        gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, false); gate.Decide(0, 113_700, 116_000, 1, false); gate.BeginAttempt(0); gate.Presented(1);
        gate.ObserveScanout(110_000, 11); // Period estimate 10_000: the next prediction (120_000) is within 5_000 of 116_667.
        Check(gate.PeriodTicks == 10_000 && gate.Predict(116_700).VblankQpc == 120_000 && !gate.Presentable(gate.Predict(116_700)), "Prediction inside half a period must not be presentable.");
        Check(gate.Decide(16_667, 116_700, 133_334, 2, false).Step == VblankStep.Idle && gate.Pending == null, "Same-vblank prediction must idle.");
        Check(gate.Decide(16_667, 118_000, 133_334, 2, false) == (VblankStep.WaitTarget, 127_000, "target"), "A later vblank must be presentable again.");
    }

    public static void ComposePriorityAndWaitRecording()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        Check(gate.Decide(0, 116_000, 116_000, 1, false).Step == VblankStep.Compose && gate.Decide(0, 120_000, 116_000, 1, false).Step == VblankStep.Compose, "Due compose lost to display.");
        Check(gate.Decide(0, 100_500, 110_000, 1, false) == (VblankStep.WaitTarget, 110_000, "compose"), "Target beyond the compose deadline must wait for compose.");
        VblankWaitAttempt? recorded = null; long clock = 100_600;
        Check(gate.Wait(110_000, "compose", () => clock, (start, due) => { Check(start == 100_600 && due == 110_000, "Wait bounds differ."); clock = 110_050; return VblankWaitResult.Reached; }, a => recorded = a), "Compose wait not reached.");
        Check(recorded == new VblankWaitAttempt(100_600, 110_050, 110_000, "compose", "compose", 9_400, 0), "Compose wait evidence differs.");
        // After the compose the earlier prediction is stale (its vblank passed): re-predict and wait for the new target.
        Check(gate.Decide(16_667, 116_700, 133_334, 2, false) == (VblankStep.WaitTarget, 130_334, "target"), "Post-compose re-prediction differs.");
        clock = 116_700;
        Check(gate.Wait(130_334, "target", () => clock, (_, _) => { clock = 130_400; return VblankWaitResult.Reached; }, a => recorded = a), "Target wait not reached.");
        Check(recorded?.Outcome == "target" && recorded.Value.RequestedMicroseconds == 13_634 && recorded.Value.LatenessMicroseconds == 66 && recorded.Value.DeadlineQpc == 130_334, "Target wait evidence differs.");
        Throws<ArgumentException>(() => gate.Wait(1, "other", () => 0, (_, _) => VblankWaitResult.Reached, _ => { }));
        Check(gate.Decide(16_667, 130_400, 133_334, 2, false).Step == VblankStep.Present, "Present expected after the target wait.");
    }

    public static void StopPriorityAndError()
    {
        var gate = Gate();
        Check(gate.Decide(0, 100, 16_667, 1, true).Step == VblankStep.Stop, "Stop lost to bootstrap.");
        gate.ObserveScanout(100_000, 10);
        Check(gate.Decide(0, 100_500, 116_000, 1, true).Step == VblankStep.Stop, "Stop lost to display.");
        VblankWaitAttempt? recorded = null;
        Check(!gate.Wait(113_667, "target", () => 100_600, (_, _) => VblankWaitResult.Cancelled, a => recorded = a), "Cancelled wait reported reached.");
        Check(recorded?.Outcome == "cancelled" && recorded.Value.LatenessMicroseconds == 0 && recorded.Value.RequestedMicroseconds == 13_067, "Cancelled evidence differs.");
        bool recordedError = false;
        Throws<ApplicationException>(() => gate.Wait(113_667, "target", () => 100_600, (_, _) => throw new ApplicationException("Wait failure"), a => recordedError = a.Outcome == "error"));
        Check(recordedError, "Wait failure was not recorded before faulting.");
        Throws<InvalidOperationException>(() => gate.BeginAttempt(0));
    }

    public static void BootstrapToNormalTransition()
    {
        var gate = Gate();
        Check(gate.Decide(long.MinValue, 100, 16_667, 1, false).Step == VblankStep.Idle && gate.Decide(0, 100, 16_667, 0, false).Step == VblankStep.Idle, "Display before the first compose or without an image.");
        Check(gate.Decide(0, 100, 16_667, 1, false).Step == VblankStep.Bootstrap && gate.AttemptPrediction == null, "First image without statistics must bootstrap.");
        gate.BeginAttempt(0); gate.Presented(1);
        Check(gate.LastPresentedVblankQpc == long.MinValue && gate.Decide(0, 200, 16_667, 1, false).Step == VblankStep.Idle, "Bootstrap must not consume a predicted vblank and attempts once per slot.");
        Check(gate.Decide(16_667, 16_700, 33_334, 2, false).Step == VblankStep.Bootstrap, "Still no statistics: bootstrap again.");
        gate.Defer(); // Latency waitable unsignaled: no attempt was made, the slot stays available.
        Check(gate.Decide(16_667, 16_800, 33_334, 2, false).Step == VblankStep.Bootstrap, "Deferred bootstrap lost its slot.");
        gate.BeginAttempt(16_667); gate.Presented(2);
        gate.ObserveScanout(20_000, 5);
        Check(gate.Decide(33_334, 33_400, 50_001, 3, false) == (VblankStep.WaitTarget, 33_667, "target"), "First statistics did not switch to prediction.");
        Check(gate.Decide(33_334, 33_700, 50_001, 3, false).Step == VblankStep.Present && gate.AttemptPrediction?.PredictedRefresh == 6, "Predicted present after bootstrap differs.");
        gate.BeginAttempt(33_334); gate.Presented(3);
        Check(gate.LastPresentedVblankQpc == 36_667 && gate.LastPresentedId == 3, "Transition state differs.");
    }

    public static void IdleWithoutNewerImageAndDefer()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, false); gate.Decide(0, 113_700, 116_000, 1, false); gate.BeginAttempt(0); gate.Presented(1);
        Check(gate.Decide(16_667, 116_700, 133_334, 1, false).Step == VblankStep.Idle && gate.Pending == null, "Idle expected when the latest image is already displayed.");
        Check(gate.Decide(16_667, 116_700, 133_334, 2, false).Step == VblankStep.WaitTarget, "Wait expected.");
        Check(gate.Decide(16_667, 130_400, 133_334, 2, false).Step == VblankStep.Present, "Present expected.");
        gate.Defer();
        Check(gate.AttemptPrediction == null && gate.Pending == null, "Defer must drop the prediction.");
        var next = gate.Decide(16_667, 130_450, 133_334, 2, false);
        Check(next == (VblankStep.WaitTarget, 133_334, "compose") && gate.Pending?.PredictedRefresh == 13, "Deferred attempt must target a later vblank, not retry inside the margin.");
    }

    // Defect fix (4K hardware trace, display 44.8 Hz): the target wake-up landed after the compose deadline, the compose ran
    // first and the present then took the newer image (N skipped, N+1 held at the following vblank). A passed target whose
    // vblank is at least Lead (1 ms) ahead presents before the due compose; inside the lead it re-predicts instead.
    public static void PassedTargetPresentsBeforeDueCompose()
    {
        var gate = Gate();
        Check(gate.LeadTicks == 1000, "Lead must be 1 ms.");
        gate.ObserveScanout(100_000, 10);
        Check(gate.Decide(0, 100_500, 113_967, 1, false) == (VblankStep.WaitTarget, 113_667, "target"), "Target before the compose deadline must wait for the target.");
        // Woke 0.4 ms late: compose due passed by 0.1 ms, vblank 2.6 ms ahead -> Present, not Compose.
        var step = gate.Decide(0, 114_067, 113_967, 1, false);
        Check(step == (VblankStep.Present, 116_667, "") && gate.AttemptPrediction?.VblankQpc == 116_667, "Passed target with a reachable vblank lost to the due compose.");
        gate.BeginAttempt(0); gate.Presented(1);
        Check(gate.LastPresentedVblankQpc == 116_667 && gate.Decide(0, 114_100, 113_967, 1, false).Step == VblankStep.Compose, "Compose must follow the present.");
        Check(gate.Decide(0, 114_100, 113_967, 2, false).Step == VblankStep.Compose, "A newer image must not present twice into the same vblank before the due compose.");
        // Exactly at vblank - lead is still reachable; without a newer image the due compose wins and the prediction stays.
        gate = Gate(); gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 113_967, 1, false);
        Check(gate.Decide(0, 115_667, 113_967, 0, false).Step == VblankStep.Compose && gate.Pending?.VblankQpc == 116_667, "Without a newer image the due compose wins.");
        Check(gate.Decide(0, 115_667, 113_967, 1, false).Step == VblankStep.Present, "vblank - lead must still be reachable.");
        // Inside the lead (0.5 ms before the vblank): no present into a vblank it cannot make; Compose when due, else re-predict.
        gate = Gate(); gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_100, 1, false);
        Check(gate.Decide(0, 116_167, 116_100, 1, false).Step == VblankStep.Compose && gate.Pending == null && gate.AttemptPrediction == null, "Inside the lead the due compose must win and drop the prediction.");
        Check(gate.Decide(16_667, 116_700, 133_334, 2, false) == (VblankStep.WaitTarget, 130_334, "target") && gate.Pending?.PredictedRefresh == 12, "Re-prediction after the compose differs.");
        gate = Gate(); gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_500, 1, false);
        Check(gate.Decide(0, 116_167, 116_500, 1, false) == (VblankStep.WaitTarget, 116_500, "compose") && gate.Pending?.VblankQpc == 133_334, "Inside the lead with compose ahead must re-predict the next vblank.");
    }

    public static void LoopIdleWaitThreshold()
    {
        const long f = 10_000_000; // 100 ns ticks: 50 us = 500 ticks.
        Check(!LoopIdleWait.UseTimer(1000, 1000 + 500, f) && !LoopIdleWait.UseTimer(1000, 1000, f) && !LoopIdleWait.UseTimer(1000, 999, f), "At or below 50 us (and past due) must yield.");
        Check(LoopIdleWait.UseTimer(1000, 1000 + 501, f) && LoopIdleWait.UseTimer(1000, 1000 + 16_667 * 10, f), "Above 50 us must use the timer.");
        Check(!LoopIdleWait.UseTimer(0, 50, Frequency) && LoopIdleWait.UseTimer(0, 51, Frequency), "Threshold must follow the clock frequency (1 MHz: 50 ticks).");
    }

    public static void WaitableTimerReachesDeadlineAndStopWins()
    {
        using var timer = new VblankWaitTimer();
        using var stop = new ManualResetEvent(false);
        long now = Stopwatch.GetTimestamp(), due = now + Stopwatch.Frequency / 500; // 2 ms.
        Check(timer.WaitUntilOrStop(stop, now, due, Stopwatch.Frequency) == VblankWaitResult.Reached && Stopwatch.GetTimestamp() >= due, "Timer returned before its deadline.");
        stop.Set();
        Check(timer.WaitUntilOrStop(stop, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp() + Stopwatch.Frequency, Stopwatch.Frequency) == VblankWaitResult.Cancelled, "Stop handle did not win.");
    }
}
