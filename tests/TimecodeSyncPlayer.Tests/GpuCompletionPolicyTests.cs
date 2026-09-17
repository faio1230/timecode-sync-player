using FluentAssertions;
using TimecodeSyncPlayer.Output;
using Xunit;

namespace TimecodeSyncPlayer.Tests;

/// <summary>
/// D28: GPU 完了待ちの共通規則。期限超過（スライス）では fault せず tick を skip できるようにし、
/// 恒久停止（Playback unavailable）はデバイス消失か、未完了が FaultAfterSeconds 秒「連続」したときだけ。
/// </summary>
public class GpuCompletionPolicyTests
{
    private const long Frequency = 10_000_000; // 10MHz（QPC）

    private static GpuCompletionPolicy Create() => new(Frequency);

    private static long AtSeconds(double seconds) => (long)Math.Round(seconds * Frequency);

    [Fact]
    public void Pending_DoesNotReachStuck_BeforeFaultWindow()
    {
        GpuCompletionPolicy policy = Create();

        policy.Decide(completed: false, deviceRemoved: false, nowQpc: 0).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(2.9)).Should().Be(GpuWaitDecision.Pending);
        policy.StuckReported.Should().BeFalse();
    }

    [Fact]
    public void Pending_ReachesStuck_AfterContinuousFaultWindow()
    {
        GpuCompletionPolicy policy = Create();

        policy.Decide(completed: false, deviceRemoved: false, nowQpc: 0).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(3.0)).Should().Be(GpuWaitDecision.Stuck);
        policy.StuckReported.Should().BeTrue();
    }

    [Fact]
    public void Completion_ResetsTheContinuousWindow()
    {
        GpuCompletionPolicy policy = Create();

        policy.Decide(completed: false, deviceRemoved: false, nowQpc: 0).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: true, deviceRemoved: false, nowQpc: AtSeconds(2.9)).Should().Be(GpuWaitDecision.Completed);
        policy.StuckReported.Should().BeFalse();

        // 完了で連続時間は仕切り直し。2.9 秒の未完了ではまだ Stuck にしない。
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(4.0)).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(6.9)).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(7.0)).Should().Be(GpuWaitDecision.Stuck);
    }

    [Fact]
    public void Completion_ClearsStuckReported()
    {
        GpuCompletionPolicy policy = Create();

        policy.Decide(completed: false, deviceRemoved: false, nowQpc: 0).Should().Be(GpuWaitDecision.Pending);
        policy.Decide(completed: false, deviceRemoved: false, nowQpc: AtSeconds(3.0)).Should().Be(GpuWaitDecision.Stuck);
        policy.StuckReported.Should().BeTrue();

        policy.Decide(completed: true, deviceRemoved: false, nowQpc: AtSeconds(3.1)).Should().Be(GpuWaitDecision.Completed);
        policy.StuckReported.Should().BeFalse();
    }

    [Fact]
    public void DeviceRemoved_IsDeviceLost_EvenWhenPendingRecently()
    {
        GpuCompletionPolicy policy = Create();

        policy.Decide(completed: false, deviceRemoved: true, nowQpc: 0).Should().Be(GpuWaitDecision.DeviceLost);
        policy.Decide(completed: false, deviceRemoved: true, nowQpc: AtSeconds(0.1)).Should().Be(GpuWaitDecision.DeviceLost);
    }

    [Fact]
    public void SliceExpired_OnlyAfterSliceMilliseconds()
    {
        GpuCompletionPolicy policy = Create();

        policy.SliceExpired(sliceStartQpc: 0, nowQpc: AtSeconds(0.099)).Should().BeFalse();
        policy.SliceExpired(sliceStartQpc: 0, nowQpc: AtSeconds(0.100)).Should().BeTrue();
    }
}
