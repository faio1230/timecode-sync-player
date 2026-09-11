using System.Collections.Concurrent;

namespace GpuOutputProbe;

// Source contract rules 1-4 and 6 with int slots: no D3D device, texture, or thread other than the lock test's helper task.
internal static class VideoSourceSelfTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static SourceImageStamp Stamp(int generation, long sequence, double position) => new(generation, sequence, position, (long)(position * 1000));
    private static SourceImageRing<int> Ring(List<int>? released = null) => new(3, released: released == null ? null : released.Add);
    private static (SourceStatus Status, ISourceImageLease? Lease) Acquire(SourceImageRing<int> ring, int generation, double position)
    { var status = ring.TryAcquire(generation, position, out var lease); return (status, lease); }

    public static void SourceOptions()
    {
        Check(Options.Parse(Array.Empty<string>()).Source == "pattern", "Default source must be pattern.");
        Check(Options.Parse(["--source", "contract-fake", "--mode", "split", "--output", "both"]).Source == "contract-fake", "contract-fake source not accepted.");
        Check(Options.Parse(["--source", "contract-fake", "--output", "fullscreen", "--display-pacing", "vblank"]).Source == "contract-fake", "contract-fake must combine with any pacing.");
        Throws<ArgumentException>(() => Options.Parse(["--source", "fake"]));
        Throws<ArgumentException>(() => Options.Parse(["--source", "mpv"]));
    }

    public static void GenerationRejection()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1);
        Check(ring.Offer(Stamp(1, 1, 0.0), 0) && ring.Offer(Stamp(1, 2, 0.5), 1), "Current-generation offers refused.");
        Check(!ring.Offer(Stamp(0, 3, 0.7), 2) && !ring.Offer(Stamp(2, 4, 0.7), 2), "An older or future generation offer was accepted.");
        Check(Acquire(ring, 2, 0.5).Status == SourceStatus.NotReady && Acquire(ring, 0, 0.5).Status == SourceStatus.NotReady, "Mismatched generation was not NotReady.");
        var (status, held) = Acquire(ring, 1, 0.5);
        Check(status == SourceStatus.Ready && held!.Stamp.Sequence == 2, "Current generation image not returned.");
        ring.SetGeneration(2);
        Check(released.SequenceEqual([0]), "Un-leased older image must be released at the generation switch, leased one retained.");
        Check(Acquire(ring, 2, 0.5).Status == SourceStatus.NotReady && Acquire(ring, 1, 0.5).Status == SourceStatus.NotReady, "Older generation image was returned after SetGeneration.");
        held!.Dispose();
        Check(released.SequenceEqual([0, 1]), "Retired image was not released on its last lease return.");
        Check(ring.Offer(Stamp(2, 5, 0.6), 1) && Acquire(ring, 2, 0.6).Lease!.Stamp.Generation == 2, "New generation image not returned.");
        Throws<ArgumentOutOfRangeException>(() => ring.SetGeneration(1));
        ring.SetGeneration(2); // Same value: no-op.
        var d = ring.Diagnostics("d", "g", "f");
        Check(d.GenerationRejected == 4 && d.NotReady == 4, $"Generation counters differ (2 offers, 2 retired; 4 NotReady): {d}");
    }

    public static void LatestPreferredSelection()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        Check(Acquire(ring, 1, 0).Status == SourceStatus.NotReady, "Empty ring was not NotReady.");
        Check(ring.Offer(Stamp(1, 1, 1.0), 0) && ring.Offer(Stamp(1, 2, 1.5), 1) && ring.Offer(Stamp(1, 3, 2.0), 2), "Offers refused.");
        using (var lease = Acquire(ring, 1, 1.7).Lease!) Check(lease.Stamp.Sequence == 2, "Greatest position <= p was not chosen.");
        using (var lease = Acquire(ring, 1, 2.0).Lease!) Check(lease.Stamp.Sequence == 3, "Equal position must count as <= p.");
        using (var lease = Acquire(ring, 1, 9.0).Lease!) Check(lease.Stamp.Sequence == 3, "Latest image must serve later positions.");
        var (status, next) = Acquire(ring, 1, 0.2);
        Check(status == SourceStatus.Ready && next!.Stamp.Sequence == 1, "The first image after p was not returned when none is <= p.");
        next!.Dispose();
        Check(next.Width == 0 && next.Texture == null && next.Format == SourceImageFormat.Bgra8 && !next.IsNv12, "Default description differs.");
    }

    public static void EndedSemantics()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        ring.Offer(Stamp(1, 1, 9.9), 0);
        ring.SignalEnd(10.0);
        Check(Acquire(ring, 1, 9.95).Status == SourceStatus.Ready, "Last image before the end must stay Ready.");
        Check(Acquire(ring, 1, 10.0).Status == SourceStatus.Ended && Acquire(ring, 1, 12).Status == SourceStatus.Ended, "Positions at/after the end were not Ended.");
        Check(ring.Offer(Stamp(1, 2, 10.5), 1) && Acquire(ring, 1, 10.2) is { Status: SourceStatus.Ready, Lease.Stamp.Sequence: 1 }, "With a later image present the position is not Ended, and the latest image <= p still wins.");
        var empty = Ring(); empty.SetGeneration(1); empty.SignalEnd(0);
        Check(Acquire(empty, 1, 0).Status == SourceStatus.Ended && Acquire(empty, 2, 0).Status == SourceStatus.NotReady, "Ended must require the current generation.");
        empty.SetGeneration(2);
        Check(Acquire(empty, 2, 5).Status == SourceStatus.NotReady, "The end signal must not survive a generation switch.");
        Throws<ArgumentOutOfRangeException>(() => empty.SignalEnd(double.NaN));
        Check(ring.Diagnostics("d", "g", "f").Ended == 2, "Ended counter differs.");
    }

    public static void FullRingReplacementAndDrop()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1);
        for (int i = 0; i < 3; i++) Check(ring.Offer(Stamp(1, i + 1, i), i), "Fill refused.");
        Check(ring.Offer(Stamp(1, 4, 3), 5) && released.SequenceEqual([0]), "Oldest un-leased image must be replaced and its slot released.");
        var a = Acquire(ring, 1, 1).Lease!; var b = Acquire(ring, 1, 2).Lease!; var c = Acquire(ring, 1, 3).Lease!;
        Check(a.Stamp.Sequence == 2 && b.Stamp.Sequence == 3 && c.Stamp.Sequence == 4, "Unexpected images after replacement.");
        Check(!ring.Offer(Stamp(1, 5, 4), 6) && released.Count == 1, "Offer with every image leased must be dropped without releasing anything.");
        Check(ring.ImageCount == 3 && Acquire(ring, 1, 4).Lease!.Stamp.Sequence == 4, "Dropped offer changed the ring.");
        b.Dispose();
        Check(ring.Offer(Stamp(1, 6, 5), 6) && released.SequenceEqual([0, 2]), "Un-leased image must be replaced once its lease is returned.");
        var d = ring.Diagnostics("d", "g", "f");
        Check(d.Replaced == 2 && d.Dropped == 1 && d.Offered == 5 && d.PeakLeases == 4, $"Replacement counters differ: {d}");
        Throws<ArgumentOutOfRangeException>(() => new SourceImageRing<int>(2));
    }

    public static void LeaseGuards()
    {
        var ring = Ring();
        ring.SetGeneration(1); ring.Offer(Stamp(1, 1, 0), 0);
        var lease = Acquire(ring, 1, 0).Lease!;
        Throws<InvalidOperationException>(() => lease.CompleteGpuUse());
        lease.BeginGpuUse();
        Throws<InvalidOperationException>(() => lease.BeginGpuUse());
        Throws<InvalidOperationException>(() => lease.Dispose());
        Check(!ring.TryDispose(), "Ring disposed while a lease was in GPU use.");
        lease.CompleteGpuUse(); lease.Dispose(); lease.Dispose();
        Throws<InvalidOperationException>(() => lease.BeginGpuUse());
        Check(ring.Diagnostics("d", "g", "f").PeakLeases == 1, "Dispose twice changed the lease count.");
    }

    public static void DisposeWaitsForLeases()
    {
        var released = new List<int>();
        var ring = Ring(released);
        ring.SetGeneration(1); ring.Offer(Stamp(1, 1, 0), 0); ring.Offer(Stamp(1, 2, 1), 1);
        var lease = Acquire(ring, 1, 1).Lease!;
        Check(!ring.TryDispose() && released.Count == 0, "TryDispose must be false with a lease outstanding and release nothing.");
        Throws<InvalidOperationException>(() => ring.Dispose());
        Check(ring.Offer(Stamp(1, 3, 2), 2), "A refused dispose must leave the ring usable.");
        lease.Dispose();
        Check(ring.TryDispose() && released.OrderBy(i => i).SequenceEqual([0, 1, 2]), "Every image must be released when the ring is disposed.");
        Check(ring.TryDispose(), "TryDispose after disposal must stay true.");
        ring.Dispose();
        Throws<ObjectDisposedException>(() => ring.Offer(Stamp(1, 4, 3), 0));
        Throws<ObjectDisposedException>(() => ring.TryAcquire(1, 0, out _));
    }

    public static void OfferFromAnotherThread()
    {
        var ring = Ring();
        ring.SetGeneration(1);
        var errors = new ConcurrentQueue<Exception>();
        using var start = new ManualResetEventSlim(false);
        int done = 0;
        var producer = Task.Run(() =>
        {
            start.Wait();
            try { for (long s = 1; s <= 20000; s++) { ring.Offer(Stamp(1, s, s * 0.01), (int)(s % 3)); if ((s & 63) == 0) Thread.Yield(); } }
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
                    Check(lease.Stamp.Sequence >= lastSequence && lease.Stamp.DecodedQpc == (long)(lease.Stamp.PositionSeconds * 1000), "Torn stamp or a regression in the latest image.");
                    lastSequence = lease.Stamp.Sequence; acquired++;
                    lease.CompleteGpuUse();
                }
            }
        }
        catch (Exception e) { errors.Enqueue(e); }
        Check(producer.Wait(TimeSpan.FromSeconds(10)), "Producer did not finish.");
        if (!errors.IsEmpty) throw new AggregateException(errors);
        var d = ring.Diagnostics("d", "g", "f");
        Check(acquired > 0 && d.Offered + d.Dropped == 20000 && d.PeakLeases == 1 && ring.ImageCount == 3, $"Interleaved offers/acquires differ: {d}");
        Check(ring.TryDispose(), "Ring could not be disposed after every lease was returned.");
    }

    public static void FakeSourceCadence()
    {
        const long frequency = 3_000_000, origin = 5_000_000; // 30 fps = exactly 100000 ticks per source period.
        var fake = new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 30, endPositionSeconds: 0.2);
        fake.SetGeneration(1);
        Check(!fake.Tick(origin - 1) && fake.TryAcquire(1, 0, out _) == SourceStatus.NotReady, "Produced before the origin.");
        Check(fake.Tick(origin) && !fake.Tick(origin + 30_000), "One image per source period.");
        Check(fake.TryAcquire(1, 0.01, out var first) == SourceStatus.Ready && first!.Stamp is { Sequence: 1, Generation: 1, PositionSeconds: 0, DecodedQpc: origin }, "First stamp differs.");
        Check(fake.Tick(origin + 102_000), "Second period was not produced.");
        Check(fake.TryAcquire(1, 0.034, out var second) == SourceStatus.Ready && Math.Abs(second!.Stamp.PositionSeconds - 1 / 30.0) < 1e-9, "Second position is not 1/30 s.");
        Check(fake.Tick(origin + 450_000) && fake.SkippedPeriods == 2, "Late tick must skip periods, not catch up.");
        Check(fake.TryAcquire(1, 0.15, out var late) == SourceStatus.Ready && Math.Abs(late!.Stamp.PositionSeconds - 4 / 30.0) < 1e-9, "Late position differs.");
        first!.Dispose(); second!.Dispose(); late!.Dispose();
        Check(fake.TryAcquire(1, 0.15, out var held) == SourceStatus.Ready && held!.Stamp.Sequence == 3 && fake.FreeSlots == 1, "Latest image was not held or the spare slot is missing.");
        Check(fake.Tick(origin + 510_000) && fake.Produced == 4 && fake.FreeSlots == 1, "Period 5 must replace the oldest un-leased image and free its slot.");
        Check(!fake.Tick(origin + 600_000) && !fake.Tick(origin + 690_000), "Periods at/after the end position must not produce.");
        Check(fake.TryAcquire(1, 0.2, out _) == SourceStatus.Ended, "Position at the end must be Ended.");
        Check(fake.TryAcquire(1, 0.19, out var lastLease) == SourceStatus.Ready && lastLease!.Stamp.Sequence == 4, "Last image must stay Ready before the end position.");
        held!.Dispose(); lastLease!.Dispose();
        fake.SetGeneration(2);
        Check(fake.TryAcquire(2, 0.3, out _) == SourceStatus.NotReady && fake.FreeSlots == 4 && fake.Diagnostics is { Decoder: "fake-pattern", Format: "BGRA8_UNORM", Ended: 1, Replaced: 1 }, "Generation switch/diagnostics differ.");
        // Every retained image leased: the spare slot is rendered but the offer is dropped and the slot kept; a returned lease makes its image replaceable.
        var busy = new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 30);
        busy.SetGeneration(1);
        var leases = new List<ISourceImageLease>();
        for (int i = 0; i < 3; i++) { Check(busy.Tick(origin + i * 120_000), "Fill tick failed."); busy.TryAcquire(1, i * 0.04, out var lease); leases.Add(lease!); }
        Check(leases.Select(l => l.Stamp.Sequence).SequenceEqual([1, 2, 3]) && busy.FreeSlots == 1, "Fill differs.");
        Check(!busy.Tick(origin + 300_000) && busy.FreeSlots == 1 && busy.Diagnostics.Dropped == 1 && busy.Starved == 0, "Offer with every image leased must be dropped and the slot kept.");
        leases[0].Dispose();
        Check(busy.Tick(origin + 400_000) && busy.Produced == 4 && busy.FreeSlots == 1, "Released image must be replaced once its lease returns.");
        Check(!busy.TryDispose(), "Dispose must wait for the two remaining leases.");
        leases[1].Dispose(); leases[2].Dispose();
        Check(busy.TryDispose() && busy.FreeSlots == 4, "All slots must be free after disposal.");
        Throws<ArgumentOutOfRangeException>(() => new FakeVideoSource<int>([0, 1, 2, 3], origin, frequency, fps: 0));
        Throws<ArgumentOutOfRangeException>(() => new FakeVideoSource<int>([0, 1, 2], origin, frequency));
    }
}
