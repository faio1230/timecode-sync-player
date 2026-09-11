using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class GpuRecoveryStateTests
{
    [Fact]
    public void TransitionTable_CoversEveryCell()
    {
        foreach ((GpuRecoveryPhase phase, GpuRecoveryAction action, bool autoUsed,
                  GpuRecoveryPhase expectedPhase, bool expectedAccepted) in TransitionCells())
        {
            var state = new GpuRecoveryState();
            Arrange(state, phase, autoUsed);

            bool accepted = action switch
            {
                GpuRecoveryAction.DeviceLost => ApplyDeviceLost(state),
                GpuRecoveryAction.AutoRecover => state.TryAutoRecover(),
                GpuRecoveryAction.Recovered => Apply(state.OnRecovered),
                GpuRecoveryAction.RecoveryFailed => Apply(state.OnRecoveryFailed),
                GpuRecoveryAction.ManualRetry => state.TryManualRetry(),
                _ => false,
            };

            state.Phase.Should().Be(expectedPhase, $"{phase} × {action}（autoUsed={autoUsed}）の次状態");
            accepted.Should().Be(expectedAccepted, $"{phase} × {action}（autoUsed={autoUsed}）の受理");
        }
    }

    private static bool Apply(Action action) { action(); return true; }

    private static bool ApplyDeviceLost(GpuRecoveryState state)
    {
        GpuRecoveryPhase before = state.Phase;
        state.OnDeviceLost();
        return state.Phase != before;
    }

    public enum GpuRecoveryAction { DeviceLost, AutoRecover, Recovered, RecoveryFailed, ManualRetry }

    // 5.2 の遷移表の全セル。autoUsed は「初回の自動復旧を使い終えたか」。
    private static IEnumerable<(GpuRecoveryPhase, GpuRecoveryAction, bool, GpuRecoveryPhase, bool)> TransitionCells()
    {
        // GpuDeviceLostException（worker）
        yield return (GpuRecoveryPhase.Running, GpuRecoveryAction.DeviceLost, false, GpuRecoveryPhase.Lost, true);
        yield return (GpuRecoveryPhase.Lost, GpuRecoveryAction.DeviceLost, false, GpuRecoveryPhase.Lost, false);
        yield return (GpuRecoveryPhase.Recovering, GpuRecoveryAction.DeviceLost, false, GpuRecoveryPhase.Failed, true);
        yield return (GpuRecoveryPhase.Failed, GpuRecoveryAction.DeviceLost, true, GpuRecoveryPhase.Failed, false);

        // 自動復旧可（初回）
        yield return (GpuRecoveryPhase.Running, GpuRecoveryAction.AutoRecover, false, GpuRecoveryPhase.Running, false);
        yield return (GpuRecoveryPhase.Lost, GpuRecoveryAction.AutoRecover, false, GpuRecoveryPhase.Recovering, true);
        yield return (GpuRecoveryPhase.Recovering, GpuRecoveryAction.AutoRecover, false, GpuRecoveryPhase.Recovering, false);
        yield return (GpuRecoveryPhase.Failed, GpuRecoveryAction.AutoRecover, false, GpuRecoveryPhase.Failed, false);

        // 自動復旧不可（2 回目以降）
        yield return (GpuRecoveryPhase.Lost, GpuRecoveryAction.AutoRecover, true, GpuRecoveryPhase.Failed, false);

        // 復旧成功／失敗
        yield return (GpuRecoveryPhase.Recovering, GpuRecoveryAction.Recovered, false, GpuRecoveryPhase.Running, true);
        yield return (GpuRecoveryPhase.Running, GpuRecoveryAction.Recovered, false, GpuRecoveryPhase.Running, true);
        yield return (GpuRecoveryPhase.Recovering, GpuRecoveryAction.RecoveryFailed, false, GpuRecoveryPhase.Failed, true);
        yield return (GpuRecoveryPhase.Running, GpuRecoveryAction.RecoveryFailed, false, GpuRecoveryPhase.Running, true);

        // 手動再試行（BtnGpuRetry）
        yield return (GpuRecoveryPhase.Running, GpuRecoveryAction.ManualRetry, false, GpuRecoveryPhase.Running, false);
        yield return (GpuRecoveryPhase.Lost, GpuRecoveryAction.ManualRetry, false, GpuRecoveryPhase.Lost, false);
        yield return (GpuRecoveryPhase.Recovering, GpuRecoveryAction.ManualRetry, false, GpuRecoveryPhase.Recovering, false);
        yield return (GpuRecoveryPhase.Failed, GpuRecoveryAction.ManualRetry, true, GpuRecoveryPhase.Recovering, true);
    }

    private static void Arrange(GpuRecoveryState state, GpuRecoveryPhase phase, bool autoUsed)
    {
        if (phase == GpuRecoveryPhase.Running) return;
        state.OnDeviceLost();
        if (phase == GpuRecoveryPhase.Recovering)
        {
            state.TryAutoRecover();
            return;
        }
        if (phase == GpuRecoveryPhase.Failed)
        {
            state.TryAutoRecover();
            state.OnRecoveryFailed();
            return;
        }
        // Lost。autoUsed=true は「一度復旧して Running に戻り、再び消失した」状態。
        if (autoUsed)
        {
            state.TryAutoRecover();
            state.OnRecovered();
            state.OnDeviceLost();
        }
    }

    [Fact]
    public void AutoRecovery_IsLimitedToOnePerProcess()
    {
        var state = new GpuRecoveryState();

        state.OnDeviceLost().Should().Be(GpuRecoveryPhase.Lost);
        state.TryAutoRecover().Should().BeTrue();
        state.OnRecovered();
        state.Phase.Should().Be(GpuRecoveryPhase.Running);
        state.AutoRecoveryUsed.Should().BeTrue();

        // 2 回目の消失は自動復旧不可で Failed。
        state.OnDeviceLost().Should().Be(GpuRecoveryPhase.Lost);
        state.TryAutoRecover().Should().BeFalse();
        state.Phase.Should().Be(GpuRecoveryPhase.Failed);
    }

    [Fact]
    public void ManualRetry_RecoversOnlyFromFailed()
    {
        var state = new GpuRecoveryState();
        state.TryManualRetry().Should().BeFalse();

        state.OnDeviceLost();
        state.TryAutoRecover().Should().BeTrue();
        state.OnRecoveryFailed();
        state.Phase.Should().Be(GpuRecoveryPhase.Failed);

        state.TryManualRetry().Should().BeTrue();
        state.Phase.Should().Be(GpuRecoveryPhase.Recovering);
        state.OnRecovered();
        state.Phase.Should().Be(GpuRecoveryPhase.Running);
    }

    [Fact]
    public void DeviceLostDuringRecovery_GoesToFailed()
    {
        var state = new GpuRecoveryState();
        state.OnDeviceLost();
        state.TryAutoRecover();
        state.OnDeviceLost().Should().Be(GpuRecoveryPhase.Failed);
    }
}
