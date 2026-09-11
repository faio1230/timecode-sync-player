using FluentAssertions;
using TimecodeSyncPlayer.Output;

namespace TimecodeSyncPlayer.Tests;

public class OutputPoolTests
{
    private static int Publish(LatestPool pool, long id)
    {
        int slot = pool.TryBeginWrite();
        slot.Should().BeGreaterThanOrEqualTo(0);
        pool.Publish(slot, new ImageStamp(id, id * 17), true);
        return slot;
    }

    [Fact]
    public void LatestPool_ReplacesLatestAndRetainsBorrowedImages()
    {
        var pool = new LatestPool(3);
        pool.AcquireLatest().Should().BeNull();

        int first = Publish(pool, 1);
        using var held = pool.AcquireLatest()!;
        Publish(pool, 2);
        Publish(pool, 3);
        using var latest = pool.AcquireLatest()!;

        held.Slot.Should().Be(first);
        held.Stamp.Id.Should().Be(1);
        latest.Stamp.Id.Should().Be(3);
        int spare = pool.TryBeginWrite();
        spare.Should().NotBe(first).And.NotBe(latest.Slot);
        pool.AbortWrite(spare, true);
    }

    [Fact]
    public void LatestPool_GuardsPublicationLeaseAndGpuUse()
    {
        var pool = new LatestPool(3);
        int slot = pool.TryBeginWrite();
        FluentActions.Invoking(() => pool.Publish(slot, new ImageStamp(1, 17), false)).Should().Throw<InvalidOperationException>();
        pool.AcquireLatest().Should().BeNull();
        FluentActions.Invoking(() => pool.AbortWrite(slot, false)).Should().Throw<InvalidOperationException>();
        pool.Publish(slot, new ImageStamp(1, 17), true);

        var lease = pool.AcquireLatest()!;
        FluentActions.Invoking(() => lease.CompleteGpuUse()).Should().Throw<InvalidOperationException>();
        lease.BeginGpuUse();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => lease.Dispose()).Should().Throw<InvalidOperationException>();
        lease.CompleteGpuUse();
        lease.Dispose();
        lease.Dispose();
        FluentActions.Invoking(() => lease.BeginGpuUse()).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void LatestPool_RefusesReuseWhileAllSlotsAreOccupied()
    {
        var pool = new LatestPool(3);
        int first = Publish(pool, 1);
        var one = pool.AcquireLatest()!;
        Publish(pool, 2);
        using var two = pool.AcquireLatest()!;
        Publish(pool, 3);

        pool.TryBeginWrite().Should().Be(-1);
        one.Dispose();
        int free = pool.TryBeginWrite();
        free.Should().Be(first);
        pool.AbortWrite(free, true);
        pool.PeakOccupied.Should().Be(3);
    }

    [Fact]
    public void LatestPool_MultipleReadersKeepSlotUntilLastRelease()
    {
        var pool = new LatestPool(2);
        int first = Publish(pool, 1);
        var a = pool.AcquireLatest()!;
        var b = pool.AcquireLatest()!;
        Publish(pool, 2);
        a.Dispose();
        pool.TryBeginWrite().Should().Be(-1);
        b.Dispose();
        int free = pool.TryBeginWrite();
        free.Should().Be(first);
        pool.AbortWrite(free, true);
        pool.PeakReaders.Should().Be(2);
    }

    [Fact]
    public async Task LatestPool_ConcurrentReadersKeepStampConsistent()
    {
        var pool = new LatestPool(3);
        Publish(pool, 1);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        int finished = 0, reads = 0;
        var readers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int i = 0; i < 5000 && (i == 0 || Volatile.Read(ref finished) == 0); i++)
                {
                    using var lease = pool.AcquireLatest()!;
                    var stamp = lease.Stamp;
                    lease.BeginGpuUse();
                    Thread.Yield();
                    (lease.Stamp == stamp && stamp.GeneratedQpc == stamp.Id * 17).Should().BeTrue();
                    lease.CompleteGpuUse();
                    Interlocked.Increment(ref reads);
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        })).ToArray();
        var writer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (long id = 2; id < 5000; id++)
                {
                    int slot = pool.TryBeginWrite();
                    if (slot >= 0) pool.Publish(slot, new ImageStamp(id, id * 17), true);
                    Thread.Yield();
                }
            }
            catch (Exception ex) { errors.Enqueue(ex); }
            finally { Volatile.Write(ref finished, 1); }
        });
        start.Set();
        await Task.WhenAll(readers.Append(writer)).WaitAsync(TimeSpan.FromSeconds(30));
        errors.Should().BeEmpty();
        reads.Should().BeGreaterThan(0);
        pool.PeakOccupied.Should().BeLessThanOrEqualTo(3);
    }
}
