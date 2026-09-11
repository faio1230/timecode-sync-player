using System.Collections.Concurrent;
using FluentAssertions;
using TimecodeSyncPlayer.Contracts;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class SourceImageRingTests
{
    private static SourceImageStamp Stamp(int generation, long sequence, double position) => new(generation, sequence, position, (long)(position * 1000));
    private static SourceImageRing<int> Ring(List<int>? released = null) => new(3, released: released == null ? null : released.Add);

    private static (SourceStatus Status, ISourceImageLease? Lease) Acquire(SourceImageRing<int> ring, int generation, double position)
    {
        var status = ring.TryAcquire(generation, position, out var lease);
        return (status, lease);
    }

    [Fact]
    public void GenerationSwitch_RetiresOlderImagesAndRefusesMismatchedRequests()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1);
        ring.Offer(Stamp(1, 1, 0.0), 0).Should().BeTrue();
        ring.Offer(Stamp(1, 2, 0.5), 1).Should().BeTrue();
        ring.Offer(Stamp(0, 3, 0.7), 2).Should().BeFalse();
        ring.Offer(Stamp(2, 4, 0.7), 2).Should().BeFalse();
        Acquire(ring, 2, 0.5).Status.Should().Be(SourceStatus.NotReady);
        Acquire(ring, 0, 0.5).Status.Should().Be(SourceStatus.NotReady);
        var (status, held) = Acquire(ring, 1, 0.5);
        status.Should().Be(SourceStatus.Ready);
        held!.Stamp.Sequence.Should().Be(2);

        ring.SetGeneration(2);
        released.Should().Equal(0);
        Acquire(ring, 2, 0.5).Status.Should().Be(SourceStatus.NotReady);
        Acquire(ring, 1, 0.5).Status.Should().Be(SourceStatus.NotReady);
        held.Dispose();
        released.Should().Equal(0, 1);
        ring.Offer(Stamp(2, 5, 0.6), 1).Should().BeTrue();
        Acquire(ring, 2, 0.6).Lease!.Stamp.Generation.Should().Be(2);
        FluentActions.Invoking(() => ring.SetGeneration(1)).Should().Throw<ArgumentOutOfRangeException>();
        ring.SetGeneration(2);
        var d = ring.Diagnostics("d", "g", "f");
        d.GenerationRejected.Should().Be(4);
        d.NotReady.Should().Be(4);
    }

    [Fact]
    public void TryAcquire_PrefersLatestAtOrBeforePositionElseNext()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        Acquire(ring, 1, 0).Status.Should().Be(SourceStatus.NotReady);
        ring.Offer(Stamp(1, 1, 1.0), 0).Should().BeTrue();
        ring.Offer(Stamp(1, 2, 1.5), 1).Should().BeTrue();
        ring.Offer(Stamp(1, 3, 2.0), 2).Should().BeTrue();
        using (var lease = Acquire(ring, 1, 1.7).Lease!) lease.Stamp.Sequence.Should().Be(2);
        using (var lease = Acquire(ring, 1, 2.0).Lease!) lease.Stamp.Sequence.Should().Be(3);
        using (var lease = Acquire(ring, 1, 9.0).Lease!) lease.Stamp.Sequence.Should().Be(3);
        var (status, next) = Acquire(ring, 1, 0.2);
        status.Should().Be(SourceStatus.Ready);
        next!.Stamp.Sequence.Should().Be(1);
        next.Dispose();
        next.Width.Should().Be(0);
        next.Texture.Should().BeNull();
        next.Format.Should().Be(SourceImageFormat.Bgra8);
        next.IsNv12.Should().BeFalse();
    }

    [Fact]
    public void Ended_RequiresEndPositionWithNoLaterImage()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        ring.Offer(Stamp(1, 1, 9.9), 0);
        ring.SignalEnd(10.0);
        Acquire(ring, 1, 9.95).Status.Should().Be(SourceStatus.Ready);
        Acquire(ring, 1, 10.0).Status.Should().Be(SourceStatus.Ended);
        Acquire(ring, 1, 12).Status.Should().Be(SourceStatus.Ended);
        ring.Offer(Stamp(1, 2, 10.5), 1).Should().BeTrue();
        var later = Acquire(ring, 1, 10.2);
        later.Status.Should().Be(SourceStatus.Ready);
        later.Lease!.Stamp.Sequence.Should().Be(1);

        var empty = Ring();
        empty.SetGeneration(1);
        empty.SignalEnd(0);
        Acquire(empty, 1, 0).Status.Should().Be(SourceStatus.Ended);
        Acquire(empty, 2, 0).Status.Should().Be(SourceStatus.NotReady);
        empty.SetGeneration(2);
        Acquire(empty, 2, 5).Status.Should().Be(SourceStatus.NotReady);
        FluentActions.Invoking(() => empty.SignalEnd(double.NaN)).Should().Throw<ArgumentOutOfRangeException>();
        ring.Diagnostics("d", "g", "f").Ended.Should().Be(2);
    }

    [Fact]
    public void FullRing_ReplacesOldestUnleasedAndDropsWhenAllLeased()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1);
        for (int i = 0; i < 3; i++) ring.Offer(Stamp(1, i + 1, i), i).Should().BeTrue();
        ring.Offer(Stamp(1, 4, 3), 5).Should().BeTrue();
        released.Should().Equal(0);
        var a = Acquire(ring, 1, 1).Lease!;
        var b = Acquire(ring, 1, 2).Lease!;
        var c = Acquire(ring, 1, 3).Lease!;
        a.Stamp.Sequence.Should().Be(2);
        b.Stamp.Sequence.Should().Be(3);
        c.Stamp.Sequence.Should().Be(4);
        ring.Offer(Stamp(1, 5, 4), 6).Should().BeFalse();
        released.Should().HaveCount(1);
        ring.ImageCount.Should().Be(3);
        Acquire(ring, 1, 4).Lease!.Stamp.Sequence.Should().Be(4);
        b.Dispose();
        ring.Offer(Stamp(1, 6, 5), 6).Should().BeTrue();
        released.Should().Equal(0, 2);
        var d = ring.Diagnostics("d", "g", "f");
        d.Replaced.Should().Be(2);
        d.Dropped.Should().Be(1);
        d.Offered.Should().Be(5);
        d.PeakLeases.Should().Be(4);
        FluentActions.Invoking(() => new SourceImageRing<int>(2)).Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Lease_GuardsGpuUseAndDisposesOnce()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        ring.Offer(Stamp(1, 1, 0), 0);
        var lease = Acquire(ring, 1, 0).Lease!;
        FluentActions.Invoking(() => lease.CompleteGpuUse()).Should().Throw<InvalidOperationException>();
        lease.BeginGpuUse();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => lease.Dispose()).Should().Throw<InvalidOperationException>();
        ring.TryDispose().Should().BeFalse();
        lease.CompleteGpuUse();
        lease.Dispose();
        lease.Dispose();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
        ring.Diagnostics("d", "g", "f").PeakLeases.Should().Be(1);
    }

    [Fact]
    public void Dispose_WaitsForOutstandingLeasesWithoutForcing()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1);
        ring.Offer(Stamp(1, 1, 0), 0);
        ring.Offer(Stamp(1, 2, 1), 1);
        var lease = Acquire(ring, 1, 1).Lease!;
        ring.TryDispose().Should().BeFalse();
        released.Should().BeEmpty();
        FluentActions.Invoking(() => ring.Dispose()).Should().Throw<InvalidOperationException>();
        ring.Offer(Stamp(1, 3, 2), 2).Should().BeTrue();
        lease.Dispose();
        ring.TryDispose().Should().BeTrue();
        released.OrderBy(i => i).Should().Equal(0, 1, 2);
        ring.TryDispose().Should().BeTrue();
        ring.Dispose();
        FluentActions.Invoking(() => ring.Offer(Stamp(1, 4, 3), 0)).Should().Throw<ObjectDisposedException>();
        FluentActions.Invoking(() => ring.TryAcquire(1, 0, out _)).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Offer_FromAnotherThreadInterleavesSafely()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        int done = 0;
        var producer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (long s = 1; s <= 20000; s++)
                {
                    ring.Offer(Stamp(1, s, s * 0.01), (int)(s % 3));
                    if ((s & 63) == 0) Thread.Yield();
                }
            }
            catch (Exception e) { errors.Enqueue(e); }
            finally { Volatile.Write(ref done, 1); }
        });
        long acquired = 0, lastSequence = 0;
        start.Set();
        try
        {
            while (Volatile.Read(ref done) == 0 || acquired == 0)
            {
                if (ring.TryAcquire(1, 1000, out var lease) != SourceStatus.Ready) continue;
                using (lease)
                {
                    lease!.BeginGpuUse();
                    lease.Stamp.Sequence.Should().BeGreaterThanOrEqualTo(lastSequence);
                    lease.Stamp.DecodedQpc.Should().Be((long)(lease.Stamp.PositionSeconds * 1000));
                    lastSequence = lease.Stamp.Sequence;
                    acquired++;
                    lease.CompleteGpuUse();
                }
            }
        }
        catch (Exception e) { errors.Enqueue(e); }
        await producer.WaitAsync(TimeSpan.FromSeconds(30));
        errors.Should().BeEmpty();
        var d = ring.Diagnostics("d", "g", "f");
        acquired.Should().BeGreaterThan(0);
        (d.Offered + d.Dropped).Should().Be(20000);
        d.PeakLeases.Should().Be(1);
        ring.ImageCount.Should().Be(3);
        ring.TryDispose().Should().BeTrue();
    }

    [Fact]
    public void FakeSource_ProducesOneImagePerPeriodAndHandlesEnd()
    {
        const long frequency = 3_000_000, origin = 5_000_000; // 30 fps = 100000 ticks per period.
        var fake = new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 30, endPositionSeconds: 0.2);
        fake.SetGeneration(1);
        fake.Tick(origin - 1).Should().BeFalse();
        fake.TryAcquire(1, 0, out _).Should().Be(SourceStatus.NotReady);
        fake.Tick(origin).Should().BeTrue();
        fake.Tick(origin + 30_000).Should().BeFalse();
        fake.TryAcquire(1, 0.01, out var first).Should().Be(SourceStatus.Ready);
        first!.Stamp.Should().Be(new SourceImageStamp(1, 1, 0, origin));
        fake.Tick(origin + 102_000).Should().BeTrue();
        fake.TryAcquire(1, 0.034, out var second).Should().Be(SourceStatus.Ready);
        Math.Abs(second!.Stamp.PositionSeconds - 1 / 30.0).Should().BeLessThan(1e-9);
        fake.Tick(origin + 450_000).Should().BeTrue();
        fake.SkippedPeriods.Should().Be(2);
        fake.TryAcquire(1, 0.15, out var late).Should().Be(SourceStatus.Ready);
        Math.Abs(late!.Stamp.PositionSeconds - 4 / 30.0).Should().BeLessThan(1e-9);
        first.Dispose(); second.Dispose(); late.Dispose();
        fake.TryAcquire(1, 0.15, out var held).Should().Be(SourceStatus.Ready);
        held!.Stamp.Sequence.Should().Be(3);
        fake.FreeSlots.Should().Be(1);
        fake.Tick(origin + 510_000).Should().BeTrue();
        fake.Produced.Should().Be(4);
        fake.FreeSlots.Should().Be(1);
        fake.Tick(origin + 600_000).Should().BeFalse();
        fake.Tick(origin + 690_000).Should().BeFalse();
        fake.TryAcquire(1, 0.2, out _).Should().Be(SourceStatus.Ended);
        fake.TryAcquire(1, 0.19, out var lastLease).Should().Be(SourceStatus.Ready);
        lastLease!.Stamp.Sequence.Should().Be(4);
        held.Dispose();
        lastLease.Dispose();
        fake.SetGeneration(2);
        fake.TryAcquire(2, 0.3, out _).Should().Be(SourceStatus.NotReady);
        fake.FreeSlots.Should().Be(4);
        fake.Diagnostics.Should().Match<SourceDiagnostics>(d => d.Decoder == "fake-pattern" && d.Format == "BGRA8_UNORM" && d.Ended == 1 && d.Replaced == 1);

        var busy = new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 30);
        busy.SetGeneration(1);
        var leases = new List<ISourceImageLease>();
        for (int i = 0; i < 3; i++)
        {
            busy.Tick(origin + i * 120_000).Should().BeTrue();
            busy.TryAcquire(1, i * 0.04, out var lease);
            leases.Add(lease!);
        }
        leases.Select(l => l.Stamp.Sequence).Should().Equal(1, 2, 3);
        busy.FreeSlots.Should().Be(1);
        busy.Tick(origin + 300_000).Should().BeFalse();
        busy.FreeSlots.Should().Be(1);
        busy.Diagnostics.Dropped.Should().Be(1);
        busy.Starved.Should().Be(0);
        leases[0].Dispose();
        busy.Tick(origin + 400_000).Should().BeTrue();
        busy.Produced.Should().Be(4);
        busy.FreeSlots.Should().Be(1);
        busy.TryDispose().Should().BeFalse();
        leases[1].Dispose();
        leases[2].Dispose();
        busy.TryDispose().Should().BeTrue();
        busy.FreeSlots.Should().Be(4);
        FluentActions.Invoking(() => new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 0)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => new FakeVideoSource<int>([0, 1, 2], origin, frequency)).Should().Throw<ArgumentOutOfRangeException>();
    }
}
