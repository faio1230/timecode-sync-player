using System.Diagnostics;
using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class VblankDisplayGateTests
{
    private const long Frequency = 1_000_000; // 1000 ticks per ms; 60 Hz period 16_667, 3 ms margin 3000.
    private static VblankDisplayGate Gate(double marginMs = 3, double refreshHz = 60) => new(marginMs, refreshHz, Frequency);

    [Fact]
    public void MarginAndPeriodDefaults_ValidateAndFallBackToSixtyHertz()
    {
        FluentActions.Invoking(() => new VblankDisplayGate(0, 60, Frequency)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new VblankDisplayGate(double.NaN, 60, Frequency)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new VblankDisplayGate(3, 60, 0)).Should().Throw<ArgumentOutOfRangeException>();
        var gate = Gate(0.5, 0);
        gate.MarginTicks.Should().Be(500);
        gate.PeriodTicks.Should().Be(16_667);
        gate.HasScanout.Should().BeFalse();
        Gate(8, 120).PeriodTicks.Should().Be(8333);
        Gate().MarginTicks.Should().Be(3000);
        FluentActions.Invoking(() => Gate().Predict(0)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void PredictionAndLateArrival_RepredictsTheNextVblank()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Predict(100_500).Should().Be(new VblankPrediction(113_667, 116_667, 11, 16_667));
        gate.Predict(113_666).PredictedRefresh.Should().Be(11);
        // vblank - lead = 115_667 > 113_667 なので、目標(113_667)を過ぎても同じ vblank を狙う。
        gate.Predict(113_667).PredictedRefresh.Should().Be(11);
        gate.Predict(50_000).VblankQpc.Should().Be(116_667);
        gate.Decide(0, 100_500, 116_000, 1, false).Should().Be((VblankStep.WaitTarget, 113_667L, "target"));

        var late = gate.Decide(0, 117_000, 133_000, 1, false);
        late.Should().Be((VblankStep.WaitTarget, 130_334L, "target"));
        gate.Pending?.PredictedRefresh.Should().Be(12);
        var present = gate.Decide(0, 130_400, 133_000, 1, false);
        present.Step.Should().Be(VblankStep.Present);
        present.DeadlineQpc.Should().Be(133_334);
        gate.AttemptPrediction?.PredictedRefresh.Should().Be(12);
    }

    [Fact]
    public void MedianPeriodAfterJump_UsesPerRefreshAverageAndIgnoresNonAdvancing()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.HasScanout.Should().BeTrue();
        gate.PeriodTicks.Should().Be(16_667);
        gate.ObserveScanout(116_600, 11);
        gate.PeriodTicks.Should().Be(16_600);
        gate.ObserveScanout(133_300, 12);
        gate.PeriodTicks.Should().Be(16_650);
        gate.ObserveScanout(166_700, 14);
        gate.PeriodTicks.Should().Be(16_700);
        gate.ObserveScanout(216_700, 15);
        gate.PeriodTicks.Should().Be(16_700);
        gate.ObserveScanout(216_700, 15);
        gate.ObserveScanout(216_000, 16);
        gate.ObserveScanout(300_000, 15);
        gate.PeriodTicks.Should().Be(16_700);
        gate.Predict(216_700).Should().Be(new VblankPrediction(230_400, 233_400, 16, 16_700));
        for (int i = 1; i <= 8; i++) gate.ObserveScanout(216_700 + i * 16_680, (uint)(15 + i));
        gate.PeriodTicks.Should().Be(16_680);
    }

    [Fact]
    public void OnePresentPerPredictedVblank_EnforcesAttemptOncePerSlot()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, false).Step.Should().Be(VblankStep.WaitTarget);
        FluentActions.Invoking(() => gate.BeginAttempt(0)).Should().Throw<InvalidOperationException>();
        gate.Decide(0, 113_700, 116_000, 1, false).Step.Should().Be(VblankStep.Present);
        gate.BeginAttempt(0);
        FluentActions.Invoking(() => gate.BeginAttempt(0)).Should().Throw<InvalidOperationException>();
        gate.SkipReason(1).Should().BeNull();
        gate.SkipReason(0).Should().Be("display.vblank.noNewerImage");
        gate.Presented(1);
        gate.LastPresentedId.Should().Be(1);
        gate.LastPresentedVblankQpc.Should().Be(116_667);
        gate.Pending.Should().BeNull();
        gate.AttemptPrediction.Should().BeNull();
        FluentActions.Invoking(() => gate.Presented(1)).Should().Throw<InvalidOperationException>();
        gate.Decide(0, 113_800, 116_000, 2, false).Step.Should().Be(VblankStep.Idle);
        var next = gate.Decide(16_667, 116_700, 133_334, 2, false);
        next.Should().Be((VblankStep.WaitTarget, 130_334L, "target"));
        gate.Pending?.PredictedRefresh.Should().Be(12);
        gate.Decide(16_667, 130_400, 133_334, 2, false).Step.Should().Be(VblankStep.Present);
        gate.BeginAttempt(16_667);
        gate.Presented(2);
        gate.LastPresentedVblankQpc.Should().Be(133_334);
    }

    [Fact]
    public void TimeDedupeAndPresentDeadline_UsePredictedVblank()
    {
        var gate = Gate();
        gate.PresentDeadline(16_667, 1_000_000).Should().Be(16_667);
        gate.PresentDeadline(16_667, 10_000).Should().Be(10_000);
        for (int i = 0; i <= 8; i++) gate.ObserveScanout(100_000 - (8 - i) * 16_667, (uint)(2 + i));
        gate.Decide(16_667, 116_700, 133_334, 1, false).Should().Be((VblankStep.WaitTarget, 130_334L, "target"));
        gate.Pending?.PredictedRefresh.Should().Be(12);
        gate.Decide(16_667, 130_400, 133_334, 1, false).Step.Should().Be(VblankStep.Present);
        gate.BeginAttempt(16_667);
        gate.Presented(1);
        gate.LastPresentedVblankQpc.Should().Be(133_334);

        gate.ObserveScanout(133_334, 11);
        gate.PeriodTicks.Should().Be(16_667);
        gate.Decide(33_334, 133_400, 150_001, 2, false).Should().Be((VblankStep.WaitTarget, 147_001L, "target"));
        gate.Pending.Should().Be(new VblankPrediction(147_001, 150_001, 12, 16_667));
        gate.Decide(33_334, 147_100, 148_000, 2, false).Step.Should().Be(VblankStep.Present);
        gate.BeginAttempt(33_334);
        gate.PresentDeadline(148_000, 1_000_000).Should().Be(150_001);
        gate.PresentDeadline(148_000, 149_000).Should().Be(149_000);
        gate.Presented(2);
        gate.LastPresentedVblankQpc.Should().Be(150_001);
        gate.PresentDeadline(148_000, 1_000_000).Should().Be(148_000);

        gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, false);
        gate.Decide(0, 113_700, 116_000, 1, false);
        gate.BeginAttempt(0);
        gate.Presented(1);
        gate.ObserveScanout(110_000, 11);
        gate.PeriodTicks.Should().Be(10_000);
        gate.Predict(116_700).VblankQpc.Should().Be(120_000);
        gate.Presentable(gate.Predict(116_700)).Should().BeFalse();
        gate.Decide(16_667, 116_700, 133_334, 2, false).Step.Should().Be(VblankStep.Idle);
        gate.Pending.Should().BeNull();
        // vblank 120_000 は直前の表示 (116_667) と半周期以内なので Idle。時間が進むと次の
        // vblank 130_000 を狙う（周期が 10ms へ変わった直後の重複排除）。
        gate.Decide(16_667, 118_000, 133_334, 2, false).Step.Should().Be(VblankStep.Idle);
        gate.Pending.Should().BeNull();
        gate.Decide(16_667, 119_100, 133_334, 2, false).Should().Be((VblankStep.WaitTarget, 127_000L, "target"));
    }

    [Fact]
    public void ComposePriorityAndWaitRecording_KeepComposeDeadlineAndEvidence()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 116_000, 116_000, 1, false).Step.Should().Be(VblankStep.Compose);
        gate.Decide(0, 120_000, 116_000, 1, false).Step.Should().Be(VblankStep.Compose);
        gate.Decide(0, 100_500, 110_000, 1, false).Should().Be((VblankStep.WaitTarget, 110_000L, "compose"));

        VblankWaitAttempt? recorded = null;
        long clock = 100_600;
        gate.Wait(110_000, "compose", () => clock, (start, due) =>
        {
            start.Should().Be(100_600);
            due.Should().Be(110_000);
            clock = 110_050;
            return VblankWaitResult.Reached;
        }, a => recorded = a).Should().BeTrue();
        recorded.Should().Be(new VblankWaitAttempt(100_600, 110_050, 110_000, "compose", "compose", 9_400, 0));

        gate.Decide(16_667, 116_700, 133_334, 2, false).Should().Be((VblankStep.WaitTarget, 130_334L, "target"));
        clock = 116_700;
        gate.Wait(130_334, "target", () => clock, (_, _) => { clock = 130_400; return VblankWaitResult.Reached; }, a => recorded = a).Should().BeTrue();
        recorded!.Value.Outcome.Should().Be("target");
        recorded.Value.RequestedMicroseconds.Should().Be(13_634);
        recorded.Value.LatenessMicroseconds.Should().Be(66);
        recorded.Value.DeadlineQpc.Should().Be(130_334);
        FluentActions.Invoking(() => gate.Wait(1, "other", () => 0, (_, _) => VblankWaitResult.Reached, _ => { })).Should().Throw<ArgumentException>();
        gate.Decide(16_667, 130_400, 133_334, 2, false).Step.Should().Be(VblankStep.Present);
    }

    [Fact]
    public void StopPriorityAndError_StopWinsAndErrorsAreRecorded()
    {
        var gate = Gate();
        gate.Decide(0, 100, 16_667, 1, true).Step.Should().Be(VblankStep.Stop);
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, true).Step.Should().Be(VblankStep.Stop);

        VblankWaitAttempt? recorded = null;
        gate.Wait(113_667, "target", () => 100_600, (_, _) => VblankWaitResult.Cancelled, a => recorded = a).Should().BeFalse();
        recorded!.Value.Outcome.Should().Be("cancelled");
        recorded.Value.LatenessMicroseconds.Should().Be(0);
        recorded.Value.RequestedMicroseconds.Should().Be(13_067);

        bool recordedError = false;
        FluentActions.Invoking(() => gate.Wait(113_667, "target", () => 100_600,
            (_, _) => throw new ApplicationException("Wait failure"), a => recordedError = a.Outcome == "error")).Should().Throw<ApplicationException>();
        recordedError.Should().BeTrue();
        FluentActions.Invoking(() => gate.BeginAttempt(0)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void BootstrapToNormalTransition_SwitchesToPredictionAfterFirstScanout()
    {
        var gate = Gate();
        gate.Decide(long.MinValue, 100, 16_667, 1, false).Step.Should().Be(VblankStep.Idle);
        gate.Decide(0, 100, 16_667, 0, false).Step.Should().Be(VblankStep.Idle);
        gate.Decide(0, 100, 16_667, 1, false).Step.Should().Be(VblankStep.Bootstrap);
        gate.AttemptPrediction.Should().BeNull();

        gate.BeginAttempt(0);
        gate.Presented(1);
        gate.LastPresentedVblankQpc.Should().Be(long.MinValue);
        gate.Decide(0, 200, 16_667, 1, false).Step.Should().Be(VblankStep.Idle);
        gate.Decide(16_667, 16_700, 33_334, 2, false).Step.Should().Be(VblankStep.Bootstrap);
        gate.Defer();
        gate.Decide(16_667, 16_800, 33_334, 2, false).Step.Should().Be(VblankStep.Bootstrap);
        gate.BeginAttempt(16_667);
        gate.Presented(2);
        gate.ObserveScanout(20_000, 5);
        gate.Decide(33_334, 33_400, 50_001, 3, false).Should().Be((VblankStep.WaitTarget, 33_667L, "target"));
        gate.Decide(33_334, 33_700, 50_001, 3, false).Step.Should().Be(VblankStep.Present);
        gate.AttemptPrediction?.PredictedRefresh.Should().Be(6);
        gate.BeginAttempt(33_334);
        gate.Presented(3);
        gate.LastPresentedVblankQpc.Should().Be(36_667);
        gate.LastPresentedId.Should().Be(3);
    }

    [Fact]
    public void IdleWithoutNewerImage_AndDeferTargetsLaterVblank()
    {
        var gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_000, 1, false);
        gate.Decide(0, 113_700, 116_000, 1, false);
        gate.BeginAttempt(0);
        gate.Presented(1);
        gate.Decide(16_667, 116_700, 133_334, 1, false).Step.Should().Be(VblankStep.Idle);
        gate.Pending.Should().BeNull();
        gate.Decide(16_667, 116_700, 133_334, 2, false).Step.Should().Be(VblankStep.WaitTarget);
        gate.Decide(16_667, 130_400, 133_334, 2, false).Step.Should().Be(VblankStep.Present);
        gate.Defer();
        gate.AttemptPrediction.Should().BeNull();
        gate.Pending.Should().BeNull();
        // 目標 130_334 を 0.1ms 過ぎただけ。vblank 133_334 は lead 以上先なので即時 Present。
        var next = gate.Decide(16_667, 130_450, 133_334, 2, false);
        next.Should().Be((VblankStep.Present, 133_334L, ""));
        gate.Pending.Should().BeNull();
        gate.AttemptPrediction?.PredictedRefresh.Should().Be(12);
    }

    [Fact]
    public void PassedTargetWithReachableVblank_PresentsBeforeDueCompose()
    {
        var gate = Gate();
        gate.LeadTicks.Should().Be(1000);
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 113_967, 1, false).Should().Be((VblankStep.WaitTarget, 113_667L, "target"));
        var step = gate.Decide(0, 114_067, 113_967, 1, false);
        step.Should().Be((VblankStep.Present, 116_667L, ""));
        gate.AttemptPrediction?.VblankQpc.Should().Be(116_667);
        gate.BeginAttempt(0);
        gate.Presented(1);
        gate.LastPresentedVblankQpc.Should().Be(116_667);
        gate.Decide(0, 114_100, 113_967, 1, false).Step.Should().Be(VblankStep.Compose);
        gate.Decide(0, 114_100, 113_967, 2, false).Step.Should().Be(VblankStep.Compose);

        gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 113_967, 1, false);
        gate.Decide(0, 115_667, 113_967, 0, false).Step.Should().Be(VblankStep.Compose);
        gate.Pending?.VblankQpc.Should().Be(116_667);
        gate.Decide(0, 115_667, 113_967, 1, false).Step.Should().Be(VblankStep.Present);

        gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_100, 1, false);
        gate.Decide(0, 116_167, 116_100, 1, false).Step.Should().Be(VblankStep.Compose);
        gate.Pending.Should().BeNull();
        gate.AttemptPrediction.Should().BeNull();
        gate.Decide(16_667, 116_700, 133_334, 2, false).Should().Be((VblankStep.WaitTarget, 130_334L, "target"));
        gate.Pending?.PredictedRefresh.Should().Be(12);

        gate = Gate();
        gate.ObserveScanout(100_000, 10);
        gate.Decide(0, 100_500, 116_500, 1, false);
        gate.Decide(0, 116_167, 116_500, 1, false).Should().Be((VblankStep.WaitTarget, 116_500L, "compose"));
        gate.Pending?.VblankQpc.Should().Be(133_334);
    }

    [Fact]
    public void PassedTargetWithLeadRemaining_ImmediatePresentsWithoutPending()
    {
        // margin 5ms / lead 1ms。目標 111_667 を 2ms 過ぎた now=113_667 でも vblank は 3ms 先。
        var gate = new VblankDisplayGate(5, 60, Frequency);
        gate.ObserveScanout(100_000, 10);
        gate.Predict(113_667).VblankQpc.Should().Be(116_667);

        var step = gate.Decide(0, 113_667, 133_334, 1, false);

        step.Should().Be((VblankStep.Present, 116_667L, ""));
        gate.AttemptPrediction?.VblankQpc.Should().Be(116_667);
        gate.Pending.Should().BeNull();
        gate.BeginAttempt(0);
        gate.Presented(1);
        gate.LastPresentedVblankQpc.Should().Be(116_667);
    }

    [Fact]
    public void TimeoutUntilMs_ClampsToTheVblankLeadWindow()
    {
        VblankDisplayGate.TimeoutUntilMs(100_000, 102_500, Frequency, 4).Should().Be(2);
        VblankDisplayGate.TimeoutUntilMs(100_000, 100_000, Frequency, 4).Should().Be(0);
        VblankDisplayGate.TimeoutUntilMs(100_000, 99_000, Frequency, 4).Should().Be(0);
        VblankDisplayGate.TimeoutUntilMs(100_000, 1_000_000, Frequency, 4).Should().Be(4);
        VblankDisplayGate.TimeoutUntilMs(100_000, 1_000_000, Frequency, 0).Should().Be(0);
    }

    [Fact]
    public void NotReadyGate_RecordsAtMostOncePerComposeTick()
    {
        var gate = new VblankNotReadyGate();
        gate.ShouldRecord(10).Should().BeTrue();
        gate.ShouldRecord(10).Should().BeFalse();
        gate.ShouldRecord(10).Should().BeFalse();
        gate.ShouldRecord(16_667).Should().BeTrue();
        gate.ShouldRecord(16_667).Should().BeFalse();
    }

    [Fact]
    public void WaitableTimer_ReachesDeadlineAndStopWins()
    {
        using var timer = new VblankWaitTimer();
        using var stop = new ManualResetEvent(false);
        long now = Stopwatch.GetTimestamp();
        long due = now + Stopwatch.Frequency / 500; // 2 ms.
        timer.WaitUntilOrStop(stop, now, due, Stopwatch.Frequency).Should().Be(VblankWaitResult.Reached);
        Stopwatch.GetTimestamp().Should().BeGreaterThanOrEqualTo(due);
        stop.Set();
        timer.WaitUntilOrStop(stop, Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp() + Stopwatch.Frequency, Stopwatch.Frequency)
            .Should().Be(VblankWaitResult.Cancelled);
    }
}
