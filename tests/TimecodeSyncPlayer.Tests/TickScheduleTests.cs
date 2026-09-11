using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class TickScheduleTests
{
    [Fact]
    public void Schedule_SkipsLateTicksWithoutCatchUp()
    {
        var s = new TickSchedule(1000, 60, 6000);
        FluentActions.Invoking(() => s.Take(999)).Should().Throw<InvalidOperationException>();
        s.Take(1000).Should().Be((1000L, 0L));
        s.Take(1450).Should().Be((1400L, 3L));
        s.DueQpc.Should().Be(1500);
        FluentActions.Invoking(() => s.Take(1450)).Should().Throw<InvalidOperationException>();
        s.Take(1500).Should().Be((1500L, 0L));
    }

    [Fact]
    public void Schedule_PreservesFractionalRateFutureDeadlines()
    {
        var s = new TickSchedule(500, 59.94, 10_000_000);
        for (int i = 0; i < 1000; i++)
        {
            long due = s.DueQpc;
            var result = s.Take(due);
            result.Scheduled.Should().Be(due);
            result.Skipped.Should().Be(0);
            s.DueQpc.Should().BeGreaterThan(due);
        }
        long late = s.DueQpc + 10_000_000;
        s.Take(late).Skipped.Should().BeGreaterThanOrEqualTo(59);
        s.DueQpc.Should().BeGreaterThan(late);
    }

    [Fact]
    public void OffsetSchedule_NeverRegressesAndUsesSampledOffset()
    {
        var offset = new ScheduleOffset();
        var s = new TickSchedule(1000, 60, 6000, offset); // Period 100.
        s.DueQpc.Should().Be(1000);
        s.Take(1000).Should().Be((1000L, 0L));
        s.DueQpc.Should().Be(1100);

        offset.Add(-30);
        s.DueQpc.Should().Be(1070);
        s.Take(1070).Should().Be((1070L, 0L));
        offset.Add(50);
        s.DueQpc.Should().Be(1220);
        s.Take(1225).Should().Be((1220L, 0L));

        long due = s.DueQpc;
        offset.Add(40);
        due.Should().Be(1320);
        s.Take(1320).Should().Be((1320L, 0L));
        s.DueQpc.Should().Be(1460);
        offset.Add(-40);
        FluentActions.Invoking(() => s.Take(1459)).Should().Throw<InvalidOperationException>();
        s.DueQpc.Should().Be(1420);
        FluentActions.Invoking(() => s.Take(1419)).Should().Throw<InvalidOperationException>();
        s.Take(1420).Should().Be((1420L, 0L));

        offset.Add(-150);
        s.DueQpc.Should().Be(1470);
        var skipped = s.Take(1471);
        skipped.Scheduled.Should().Be(1470);
        skipped.Skipped.Should().Be(1);
        s.DueQpc.Should().Be(1570);

        var late = s.Take(1925);
        late.Should().Be((1870L, 3L));
        s.DueQpc.Should().Be(1970);
        FluentActions.Invoking(() => s.Take(1969)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void OffsetSchedule_FractionalRateStaysMonotonicUnderWanderingOffset()
    {
        var offset = new ScheduleOffset();
        var random = new Random(7);
        var f = new TickSchedule(500, 59.94, 1_000_000, offset);
        long previous = long.MinValue, clock = 500;
        for (int i = 0; i < 2000; i++)
        {
            offset.Add(random.Next(-500, 501));
            long next = f.DueQpc;
            clock = Math.Max(clock, next) + random.Next(0, 300);
            var tick = f.Take(clock);
            tick.Scheduled.Should().BeGreaterThan(previous);
            tick.Scheduled.Should().BeLessThanOrEqualTo(clock);
            tick.Skipped.Should().Be(0);
            previous = tick.Scheduled;
        }
    }

    [Fact]
    public void LoopIdleWait_UsesTimerAboveFiftyMicroseconds()
    {
        const long f = 10_000_000; // 100 ns ticks: 50 us = 500 ticks.
        LoopIdleWait.UseTimer(1000, 1500, f).Should().BeFalse();
        LoopIdleWait.UseTimer(1000, 1000, f).Should().BeFalse();
        LoopIdleWait.UseTimer(1000, 999, f).Should().BeFalse();
        LoopIdleWait.UseTimer(1000, 1501, f).Should().BeTrue();
        LoopIdleWait.UseTimer(1000, 1000 + 16_667 * 10, f).Should().BeTrue();
        LoopIdleWait.UseTimer(0, 50, 1_000_000).Should().BeFalse();
        LoopIdleWait.UseTimer(0, 51, 1_000_000).Should().BeTrue();
    }
}
