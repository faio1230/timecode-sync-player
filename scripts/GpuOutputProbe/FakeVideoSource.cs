using System.Collections.Concurrent;

namespace GpuOutputProbe;

// Contract fake: wraps SourceImageRing with a decode "thread" driven by explicit Tick(nowQpc) calls instead of a real thread.
// One image is produced per due source period (origin + i/fps, late periods are skipped like TickSchedule, never caught up) with
// PositionSeconds = (scheduled - origin) / frequency and DecodedQpc = the tick time. Slots are owned here: `render(slot, stamp)`
// fills a free slot before it is offered (a Surface via ShaderPipeline.Compose in the engine; no-op with int slots in tests).
// The decoder-side pool is one slot larger than the ring (slots.Count - 1 images are retained, at least 3), so a full ring still
// leaves one slot to render into; the offer then replaces the oldest un-leased image, whose slot comes back as free. A slot is
// free when it is not in the ring and no lease holds it, so a leased texture is never written; with every retained image leased
// the offer is dropped by the ring and the slot is kept for the next period. Stamps carry the ring's current generation.
internal sealed class FakeVideoSource<TSlot> : IVideoSource
{
    public const double DefaultFps = 30;
    private readonly SourceImageRing<TSlot> ring;
    private readonly ConcurrentQueue<TSlot> free = new();
    private readonly TickSchedule schedule;
    private readonly long origin, frequency;
    private readonly double endPosition;
    private readonly Action<TSlot, SourceImageStamp>? render;
    private readonly string decoder, gpu;
    private long sequence, starved, produced, skipped;
    private bool endSignalled;
    public FakeVideoSource(IReadOnlyList<TSlot> slots, long originQpc, long frequency, double fps = DefaultFps, double endPositionSeconds = double.PositiveInfinity,
        Action<TSlot, SourceImageStamp>? render = null, Func<TSlot, SourceImageDescription>? describe = null, string decoder = "fake-pattern", string gpu = "none")
    {
        if (!double.IsFinite(fps) || fps <= 0 || frequency <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        ring = new SourceImageRing<TSlot>(slots.Count - 1, describe, free.Enqueue);
        foreach (var slot in slots) free.Enqueue(slot);
        schedule = new TickSchedule(originQpc, fps, frequency);
        origin = originQpc; this.frequency = frequency; endPosition = endPositionSeconds; this.render = render; this.decoder = decoder; this.gpu = gpu;
    }
    public long Produced => Volatile.Read(ref produced);
    public long Starved => Volatile.Read(ref starved);
    public long SkippedPeriods => Volatile.Read(ref skipped);
    public int FreeSlots => free.Count;
    // Decode side. Returns true when an image was produced and offered. Positions at/after the end produce nothing and signal the end once.
    public bool Tick(long nowQpc)
    {
        if (nowQpc < schedule.DueQpc) return false;
        var (scheduled, late) = schedule.Take(nowQpc);
        skipped += late;
        double position = (scheduled - origin) / (double)frequency;
        if (position >= endPosition) { if (!endSignalled) { ring.SignalEnd(endPosition); endSignalled = true; } return false; }
        if (!free.TryDequeue(out var slot)) { starved++; return false; }
        var stamp = new SourceImageStamp(ring.Generation, ++sequence, position, nowQpc);
        render?.Invoke(slot, stamp);
        if (!ring.Offer(stamp, slot)) { free.Enqueue(slot); return false; }
        produced++; return true;
    }
    public void SetGeneration(int generation) { ring.SetGeneration(generation); endSignalled = false; }
    public SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease) => ring.TryAcquire(generation, positionSeconds, out lease);
    public SourceDiagnostics Diagnostics => ring.Diagnostics(decoder, gpu, "BGRA8_UNORM");
    public bool TryDispose() => ring.TryDispose();
    public void Dispose() => ring.Dispose();
}
