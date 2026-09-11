using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class PresentReadyGateTests
{
    [Fact]
    public void TimeoutMs_UsesRemainingWholeMilliseconds()
    {
        PresentReadyGate.MaxWaitMs.Should().Be(1);
        PresentReadyGate.TimeoutMs(1, 0, 999, 1_000_000).Should().Be(0);
        PresentReadyGate.TimeoutMs(1, 0, 1000, 1_000_000).Should().Be(1);
        PresentReadyGate.TimeoutMs(0, 0, 100_000, 1_000_000).Should().Be(0);
        var schedule = new TickSchedule(500, 240, 1_000_000);
        _ = schedule.Take(schedule.DueQpc);
        PresentReadyGate.TimeoutMs(1, schedule.DueQpc - 500, schedule.DueQpc, 1_000_000).Should().Be(0);
    }

    [Fact]
    public void TryAcquire_NotReadyAndErrorEvidence()
    {
        var gate = new PresentReadyGate();
        int waits = 0;
        PresentReadyAttempt? attempt = null;
        string? skip = null;
        gate.TryAcquire(1, 1000, 1_000_000, default, () => 0,
            timeout => { waits++; timeout.Should().Be(1); return false; }, a => attempt = a, s => skip = s).Should().BeFalse();
        waits.Should().Be(1);
        skip.Should().Be("present.notReady");
        attempt!.Value.Kind.Should().Be("native");
        attempt.Value.Outcome.Should().Be("notReady");
        attempt.Value.PermissionHeld.Should().BeFalse();

        bool recordedBeforeThrow = false;
        FluentActions.Invoking(() => gate.TryAcquire(1, 1000, 1_000_000, default, () => 0,
            _ => throw new ApplicationException("Wait failed"),
            a => recordedBeforeThrow = a.Outcome == "error" && !a.PermissionHeld, _ => { })).Should().Throw<ApplicationException>();
        recordedBeforeThrow.Should().BeTrue();
        gate.PermissionHeld.Should().BeFalse();
        FluentActions.Invoking(() => gate.ConsumeForPresent()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LateSignal_IsRetainedForTheNewestImage()
    {
        var pool = new LatestPool(3);
        int slot = pool.TryBeginWrite();
        pool.Publish(slot, new ImageStamp(1, 1), true);
        var gate = new PresentReadyGate();
        long now = 0;
        int waits = 0;
        PresentReadyAttempt? attempt = null;
        using (var first = pool.AcquireLatest()!)
        {
            bool allowed = gate.TryAcquire(1, 1000, 1_000_000, default, () => now,
                _ => { waits++; now = 1000; return true; }, a => attempt = a, s => s.Should().Be("present.deadline"));
            allowed.Should().BeFalse();
            gate.PermissionHeld.Should().BeTrue();
            attempt!.Value.Outcome.Should().Be("deadline");
            attempt.Value.PermissionHeld.Should().BeTrue();
        }
        slot = pool.TryBeginWrite();
        pool.Publish(slot, new ImageStamp(2, 2), true);
        using (var latest = pool.AcquireLatest()!)
        {
            latest.Stamp.Id.Should().Be(2);
            now = 1100;
            gate.TryAcquire(1, 2100, 1_000_000, default, () => now,
                _ => throw new InvalidOperationException("Retained permission must not wait again."), a => attempt = a, _ => { }).Should().BeTrue();
            attempt!.Value.Kind.Should().Be("retained");
            attempt.Value.Outcome.Should().Be("retained");
            attempt.Value.TimeoutMs.Should().Be(0);
            waits.Should().Be(1);
            gate.ConsumeForPresent();
        }
        gate.PermissionHeld.Should().BeFalse();
        gate.TryAcquire(0, 3000, 1_000_000, default, () => now,
            timeout => { waits++; timeout.Should().Be(0); return true; }, _ => { }, _ => { }).Should().BeTrue();
        waits.Should().Be(2);
        gate.Discard();
        gate.PermissionHeld.Should().BeFalse();
    }

    [Fact]
    public void CancellationAndDrawDeadline_PreservePermission()
    {
        using var cancellation = new CancellationTokenSource();
        var gate = new PresentReadyGate();
        PresentReadyAttempt? attempt = null;
        gate.TryAcquire(1, 1000, 1_000_000, cancellation.Token, () => 0,
            _ => { cancellation.Cancel(); return true; }, a => attempt = a, s => s.Should().Be("present.cancelled")).Should().BeFalse();
        gate.PermissionHeld.Should().BeTrue();
        attempt!.Value.Outcome.Should().Be("cancelled");
        attempt.Value.PermissionHeld.Should().BeTrue();

        int records = 0;
        gate.TryAcquire(1, 1000, 1_000_000, cancellation.Token, () => 0,
            _ => throw new InvalidOperationException("Cancelled work must not wait."), _ => records++, _ => { }).Should().BeFalse();
        records.Should().Be(0);
        gate.PermissionHeld.Should().BeTrue();

        long now = 100;
        int waits = 0;
        gate.TryAcquire(1, 1000, 1_000_000, default, () => now, _ => { waits++; return false; }, _ => now = 1000, _ => { })
            .Should().BeTrue();
        PresentReadyGate.SkipReason(now, 1000, false).Should().Be("present.deadline");
        gate.PermissionHeld.Should().BeTrue();
        waits.Should().Be(0);
        PresentReadyGate.SkipReason(1001, 1000, false).Should().Be("present.deadline");
        gate.PermissionHeld.Should().BeTrue();
        gate.Discard();
    }

    [Fact]
    public void StaleBeforeWait_ExpiresWithoutPairAndOversleepIsUsable()
    {
        var gate = new PresentReadyGate();
        int records = 0;
        gate.TryAcquire(1, 1000, 1_000_000, default, () => 1000,
            _ => throw new InvalidOperationException("Expired work waited."), _ => records++, s => s.Should().Be("present.deadline")).Should().BeFalse();
        records.Should().Be(0);
        gate.PermissionHeld.Should().BeFalse();

        long now = 0;
        PresentReadyAttempt? attempt = null;
        gate.TryAcquire(1, 10_000, 1_000_000, default, () => now,
            _ => { now = 5000; return true; }, a => attempt = a,
            _ => throw new InvalidOperationException("Oversleep before deadline must be usable.")).Should().BeTrue();
        (attempt!.Value.EndQpc - attempt.Value.StartQpc).Should().Be(5000);
        attempt.Value.TimeoutMs.Should().Be(1);
        gate.PermissionHeld.Should().BeTrue();
    }
}
