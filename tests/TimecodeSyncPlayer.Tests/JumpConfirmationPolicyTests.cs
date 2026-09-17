using System.Diagnostics;
using FluentAssertions;

namespace TimecodeSyncPlayer.Tests;

public sealed class JumpConfirmationPolicyTests
{
    [Theory]
    [InlineData(TimecodeFpsMode.Fixed25, 24.0, 25.0, true)]
    [InlineData(TimecodeFpsMode.Fixed25, 25.0, 25.0, false)]
    [InlineData(TimecodeFpsMode.Fixed30, 30.0, 30.0, false)]
    [InlineData(TimecodeFpsMode.Fixed30, 24.0, 30.0, true)]
    [InlineData(TimecodeFpsMode.Auto, 30.0, 25.0, false)]
    [InlineData(TimecodeFpsMode.Auto, 24.0, 25.0, false)]
    [InlineData(TimecodeFpsMode.Fixed29_97, 30.0, 30000.0 / 1001.0, false)]
    public void DetectedFpsSuspect_FlagsOnlyFixedModeMismatch(
        TimecodeFpsMode mode, double detectedFps, double resolvedFps, bool expected) =>
        JumpConfirmationPolicy.IsDetectedFpsSuspect(mode, detectedFps, resolvedFps).Should().Be(expected);

    [Theory]
    [InlineData(12.0, 12.0, TimecodeFrameDiagnosticStatus.Duplicate, true)]
    [InlineData(12.0, 12.04, TimecodeFrameDiagnosticStatus.Normal, true)]
    [InlineData(12.0, 12.08, TimecodeFrameDiagnosticStatus.Normal, false)]
    [InlineData(12.0, 12.04, TimecodeFrameDiagnosticStatus.Jump, false)]
    [InlineData(12.0, 11.96, TimecodeFrameDiagnosticStatus.Normal, false)]
    [InlineData(12.0, 13.0, TimecodeFrameDiagnosticStatus.Duplicate, false)]
    public void Confirmation_AcceptsSameValueOrExactlyOneFrameAhead(
        double pendingSeconds, double currentSeconds, TimecodeFrameDiagnosticStatus status, bool expected) =>
        JumpConfirmationPolicy.IsConfirmedBy(pendingSeconds, currentSeconds, 25, status).Should().Be(expected);

    [Fact]
    public void ConfirmationWindow_PrefersTheSampleClockOverTheWallClock()
    {
        long pending = 1_000_000;
        long oneFrame = Stopwatch.Frequency / 25;

        // 壁時計は 203ms 離れている（音声コールバックのバースト）が、サンプル時計は 1 フレーム差。
        JumpConfirmationPolicy.IsWithinConfirmationWindow(
            pending, pending + oneFrame, 10_000, 10_203, 25).Should().BeTrue();

        // 壁時計は 10ms でも、サンプル時計が 600ms 空いていれば（無音を挟んだ保留）不成立。
        JumpConfirmationPolicy.IsWithinConfirmationWindow(
            pending, pending + (long)(Stopwatch.Frequency * 0.6), 10_000, 10_010, 25).Should().BeFalse();
    }

    [Fact]
    public void ConfirmationWindow_FallsBackToTheWallClockWithoutSampleClock()
    {
        JumpConfirmationPolicy.IsWithinConfirmationWindow(0, 0, 10_000, 10_040, 25).Should().BeTrue();
        JumpConfirmationPolicy.IsWithinConfirmationWindow(0, 0, 10_000, 10_100, 25).Should().BeTrue();
        JumpConfirmationPolicy.IsWithinConfirmationWindow(0, 0, 10_000, 10_600, 25).Should().BeFalse();

        // 片方だけ 0 でも壁時計へフォールバックする。
        JumpConfirmationPolicy.IsWithinConfirmationWindow(0, 1_000_000, 10_000, 10_203, 25).Should().BeFalse();
        JumpConfirmationPolicy.IsWithinConfirmationWindow(1_000_000, 0, 10_000, 10_080, 25).Should().BeTrue();
    }
}
