using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class SendMutexGateTests
{
    [Fact]
    public void TimeoutMs_FloorsToRemainingWholeMilliseconds()
    {
        MutexWaitPolicy.MaxWaitMs.Should().Be(8);
        MutexWaitPolicy.TimeoutMs(4, 1501, 2000, 1_000_000).Should().Be(0);
        MutexWaitPolicy.TimeoutMs(4, 0, 2999, 1_000_000).Should().Be(2);
        MutexWaitPolicy.TimeoutMs(1, 0, 20_000, 1_000_000).Should().Be(1);
        MutexWaitPolicy.TimeoutMs(0, 0, 20_000, 1_000_000).Should().Be(0);
        MutexWaitPolicy.TimeoutMs(4, 3000, 2000, 1_000_000).Should().Be(0);
        MutexWaitPolicy.TimeoutMs(8, 0, 7999, 1_000_000).Should().Be(7);
        MutexWaitPolicy.TimeoutMs(8, 0, 20_000, 1_000_000).Should().Be(8);
        FluentActions.Invoking(() => MutexWaitPolicy.TimeoutMs(9, 0, 20_000, 1_000_000)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Acquire_AtDeadlineReleasesOnceAndDoesNotAuthorizeSend()
    {
        const long frequency = 1_000_000;
        long now = 0; int waits = 0, releases = 0; string? reason = null; MutexAttempt? attempt = null;
        var lease = SendMutexGate.Acquire(8, 8000, frequency, default, () => now,
            timeout => { timeout.Should().Be(8); waits++; now = 8000; return true; },
            () => releases++, a => attempt = a, skip => reason = skip);
        lease.Should().BeNull();
        waits.Should().Be(1);
        releases.Should().Be(1);
        reason.Should().Be("send.deadlineExpired");
        attempt!.Value.Outcome.Should().Be("acquired");
        attempt.Value.TimeoutMs.Should().Be(8);
        attempt.Value.EndQpc.Should().Be(8000);
    }

    [Fact]
    public void Acquire_StaleOrCancelledNeverWaits()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            if (cancel) cts.Cancel();
            string? reason = null;
            var lease = SendMutexGate.Acquire(1, 1000, 1_000_000, cts.Token, () => cancel ? 0 : 1000,
                _ => throw new InvalidOperationException("Must not acquire stale/cancelled work."),
                () => throw new InvalidOperationException("Nothing was acquired."),
                _ => throw new InvalidOperationException("No OS wait happened."), x => reason = x);
            lease.Should().BeNull();
            reason.Should().Be(cancel ? "send.cancelled" : "send.deadlineExpired");
        }
    }

    [Fact]
    public void Acquire_LateOrCancelledOwnershipReleasesExactlyOnce()
    {
        foreach (bool cancel in new[] { false, true })
        {
            using var cts = new CancellationTokenSource();
            long now = 0; int releases = 0, waits = 0;
            string? skip = null; MutexAttempt? observed = null;
            var lease = SendMutexGate.Acquire(1, 10_000, 1_000_000, cts.Token, () => now,
                _ => { waits++; now = cancel ? 2000 : 10_000; if (cancel) cts.Cancel(); return true; },
                () => releases++, x => observed = x, x => skip = x);
            lease.Should().BeNull();
            waits.Should().Be(1);
            releases.Should().Be(1);
            skip.Should().Be(cancel ? "send.cancelled" : "send.deadlineExpired");
            observed!.Value.Outcome.Should().Be("acquired");
        }

        long clock = 0; int released = 0; string? reason = null;
        var afterLog = SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => clock, _ => true,
            () => released++, _ => clock = 1000, s => reason = s);
        afterLog.Should().BeNull();
        released.Should().Be(1);
        reason.Should().Be("send.deadlineExpired");
    }

    [Fact]
    public void Acquire_OversleepBeforeDeadlineIsAllowedAndBusyDoesNotRetry()
    {
        long now = 0; int waits = 0, releases = 0; MutexAttempt? attempt = null;
        using (var lease = SendMutexGate.Acquire(1, 10_000, 1_000_000, default, () => now,
            timeout => { timeout.Should().Be(1); waits++; now = 5000; return true; },
            () => releases++, x => attempt = x, _ => throw new InvalidOperationException("An oversleep before deadline should be accepted.")))
        {
            lease.Should().NotBeNull();
            (attempt!.Value.EndQpc - attempt.Value.StartQpc).Should().Be(5000);
            waits.Should().Be(1);
        }
        releases.Should().Be(1);

        string? reason = null; now = 0; waits = 0;
        var busy = SendMutexGate.Acquire(1, 10_000, 1_000_000, default, () => now,
            _ => { waits++; now = 1000; return false; }, () => throw new InvalidOperationException("Busy mutex is not owned."),
            x => x.Outcome.Should().Be("busy"), x => reason = x);
        busy.Should().BeNull();
        waits.Should().Be(1);
        reason.Should().Be("send.accessMutexBusy");
    }

    [Fact]
    public void Acquire_AbandonedAndExceptionalOwnershipReleaseOnce()
    {
        int releases = 0; MutexAttempt? attempt = null;
        FluentActions.Invoking(() => SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => 0,
            _ => throw new AbandonedMutexException(), () => releases++, x => attempt = x,
            _ => throw new InvalidOperationException("Abandonment must fault, not skip."))).Should().Throw<InvalidOperationException>();
        releases.Should().Be(1);
        attempt!.Value.Outcome.Should().Be("abandoned");

        releases = 0;
        FluentActions.Invoking(() => SendMutexGate.Acquire(1, 1000, 1_000_000, default, () => 0,
            _ => true, () => releases++, _ => throw new ApplicationException("Logging callback failed"), _ => { }))
            .Should().Throw<ApplicationException>();
        releases.Should().Be(1);

        int calls = 0;
        var lease = new SendMutexLease(() => { calls++; throw new ApplicationException("Release failed"); });
        FluentActions.Invoking(() => lease.Dispose()).Should().Throw<ApplicationException>();
        lease.Dispose();
        calls.Should().Be(1);
    }
}
