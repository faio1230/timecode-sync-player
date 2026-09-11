using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public class ExitCoordinatorTests
{
    [Fact]
    public void TransitionTable_CoversEveryCell()
    {
        foreach ((ExitPhase phase, ExitInput input, bool hasMoreSteps,
                  ExitPhase expectedPhase, bool expectedAccepted, bool expectedCancelClose) in AllTransitions())
        {
            ExitTransition result = ExitTransitions.Decide(phase, input, hasMoreSteps);

            result.NextPhase.Should().Be(expectedPhase, $"{phase} × {input} の次状態");
            result.Accepted.Should().Be(expectedAccepted, $"{phase} × {input} の受理");
            result.CancelClose.Should().Be(expectedCancelClose, $"{phase} × {input} の Closing キャンセル");
        }
    }

    // 5 状態 × 5 入力の全セル（StepCompleted は手順残あり/なしの 2 通り）。
    private static IEnumerable<(ExitPhase, ExitInput, bool, ExitPhase, bool, bool)> AllTransitions()
    {
        // × ／ Alt+F4 ／ Closing
        yield return (ExitPhase.Running, ExitInput.CloseRequested, true, ExitPhase.Confirming, true, true);
        yield return (ExitPhase.Confirming, ExitInput.CloseRequested, true, ExitPhase.Confirming, false, true);
        yield return (ExitPhase.ShuttingDown, ExitInput.CloseRequested, true, ExitPhase.ShuttingDown, false, true);
        yield return (ExitPhase.Forcing, ExitInput.CloseRequested, true, ExitPhase.Forcing, false, true);
        yield return (ExitPhase.Exited, ExitInput.CloseRequested, true, ExitPhase.Exited, false, false);

        // キャンセル（既定ボタン・Enter・Esc）
        yield return (ExitPhase.Running, ExitInput.Cancel, true, ExitPhase.Running, false, false);
        yield return (ExitPhase.Confirming, ExitInput.Cancel, true, ExitPhase.Running, true, false);
        yield return (ExitPhase.ShuttingDown, ExitInput.Cancel, true, ExitPhase.ShuttingDown, false, false);
        yield return (ExitPhase.Forcing, ExitInput.Cancel, true, ExitPhase.Forcing, false, false);
        yield return (ExitPhase.Exited, ExitInput.Cancel, true, ExitPhase.Exited, false, false);

        // 通常終了
        yield return (ExitPhase.Running, ExitInput.NormalExit, true, ExitPhase.Running, false, false);
        yield return (ExitPhase.Confirming, ExitInput.NormalExit, true, ExitPhase.ShuttingDown, true, false);
        yield return (ExitPhase.ShuttingDown, ExitInput.NormalExit, true, ExitPhase.ShuttingDown, false, false);
        yield return (ExitPhase.Forcing, ExitInput.NormalExit, true, ExitPhase.Forcing, false, false);
        yield return (ExitPhase.Exited, ExitInput.NormalExit, true, ExitPhase.Exited, false, false);

        // 強制終了
        yield return (ExitPhase.Running, ExitInput.Force, true, ExitPhase.Running, false, false);
        yield return (ExitPhase.Confirming, ExitInput.Force, true, ExitPhase.Forcing, true, false);
        yield return (ExitPhase.ShuttingDown, ExitInput.Force, true, ExitPhase.Forcing, true, false);
        yield return (ExitPhase.Forcing, ExitInput.Force, true, ExitPhase.Forcing, false, false);
        yield return (ExitPhase.Exited, ExitInput.Force, true, ExitPhase.Exited, false, false);

        // 手順完了
        yield return (ExitPhase.Running, ExitInput.StepCompleted, true, ExitPhase.Running, false, false);
        yield return (ExitPhase.Confirming, ExitInput.StepCompleted, true, ExitPhase.Confirming, false, false);
        yield return (ExitPhase.ShuttingDown, ExitInput.StepCompleted, true, ExitPhase.ShuttingDown, true, false);
        yield return (ExitPhase.ShuttingDown, ExitInput.StepCompleted, false, ExitPhase.Exited, true, false);
        yield return (ExitPhase.Forcing, ExitInput.StepCompleted, true, ExitPhase.Forcing, false, false);
        yield return (ExitPhase.Exited, ExitInput.StepCompleted, true, ExitPhase.Exited, false, false);
    }

    private sealed class FakeExitDialogHost : IExitDialogHost
    {
        public int ConfirmationShown { get; private set; }
        public int ProgressShown { get; private set; }
        public int Closed { get; private set; }
        public string? LastStep { get; private set; }

        public void ShowConfirmation() => ConfirmationShown++;
        public void SwitchToProgress() => ProgressShown++;
        public void UpdateStep(string stepName) => LastStep = stepName;
        public void CloseDialog() => Closed++;
    }

    private static MainWindowResourceDisposer CreateNoOpDisposer(List<string>? calls = null)
        => new(
            disposeTimer: () => calls?.Add("timer"),
            disposeRenderContext: () => calls?.Add("render"),
            disposeMpv: () => calls?.Add("mpv"),
            disposeLtc: () => calls?.Add("ltc"),
            disposeSpout: () => calls?.Add("spout"),
            disposeTimeline: () => calls?.Add("timeline"),
            disposeBuffer: () => calls?.Add("buffer"),
            stopRender: () => calls?.Add("stop"),
            stopAcceptingNewWork: () => calls?.Add("accept"));

    [Fact]
    public void CloseRequest_EntersConfirmingAndCancelsTheClose()
    {
        var host = new FakeExitDialogHost();
        var coordinator = new ExitCoordinator(host, CreateNoOpDisposer(),
            action => { action(); return Task.CompletedTask; }, () => { }, () => { });

        coordinator.OnClosingRequested().Should().BeTrue();
        coordinator.Phase.Should().Be(ExitPhase.Confirming);
        host.ConfirmationShown.Should().Be(1);
        host.ProgressShown.Should().Be(0);
    }

    [Fact]
    public void Cancel_ReturnsToRunningAndClosesTheDialog()
    {
        var host = new FakeExitDialogHost();
        var coordinator = new ExitCoordinator(host, CreateNoOpDisposer(),
            action => { action(); return Task.CompletedTask; }, () => { }, () => { });

        coordinator.OnClosingRequested();
        coordinator.CancelRequested();

        coordinator.Phase.Should().Be(ExitPhase.Running);
        host.Closed.Should().Be(1);
    }

    [Fact]
    public void Force_FromConfirming_DoesNotShowAdditionalConfirmation()
    {
        var host = new FakeExitDialogHost();
        int forceCalls = 0;
        var coordinator = new ExitCoordinator(host, CreateNoOpDisposer(),
            action => { action(); return Task.CompletedTask; }, () => forceCalls++, () => { });

        coordinator.OnClosingRequested();
        coordinator.ForceRequested();

        coordinator.Phase.Should().Be(ExitPhase.Forcing);
        forceCalls.Should().Be(1);
        host.ConfirmationShown.Should().Be(1, "強制終了は追加確認を出さない");
        host.ProgressShown.Should().Be(0);
    }

    [Fact]
    public async Task NormalExit_RunsStagesInI8OrderAndCompletes()
    {
        var calls = new List<string>();
        var host = new FakeExitDialogHost();
        bool completed = false;
        var coordinator = new ExitCoordinator(host, CreateNoOpDisposer(calls),
            action => { action(); return Task.CompletedTask; }, () => calls.Add("force"), () => completed = true);

        coordinator.OnClosingRequested();
        coordinator.NormalExitRequested();

        await WaitUntilAsync(() => completed);
        coordinator.Phase.Should().Be(ExitPhase.Exited);
        calls.Should().Equal("accept", "stop", "timer", "render", "mpv", "ltc", "spout", "timeline", "buffer");
        host.ProgressShown.Should().Be(1);
        host.Closed.Should().Be(1);
        host.LastStep.Should().Be(MainWindowResourceDisposer.ReleaseResourcesStepName);
    }

    [Fact]
    public async Task Force_WhileShuttingDown_StopsStageLoop()
    {
        var calls = new List<string>();
        var host = new FakeExitDialogHost();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool force = false;
        var coordinator = new ExitCoordinator(host, CreateNoOpDisposer(calls),
            async action =>
            {
                await gate.Task;
                action();
            },
            () => force = true, () => { });

        coordinator.OnClosingRequested();
        coordinator.NormalExitRequested();
        coordinator.Phase.Should().Be(ExitPhase.ShuttingDown);
        coordinator.ForceRequested();
        coordinator.Phase.Should().Be(ExitPhase.Forcing);
        force.Should().BeTrue();

        gate.SetResult();
        await WaitUntilAsync(() => calls.Count >= 2);
        calls.Should().Equal("accept", "stop");
        coordinator.Phase.Should().Be(ExitPhase.Forcing);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        condition().Should().BeTrue("条件が時間内に満たされる");
    }
}
