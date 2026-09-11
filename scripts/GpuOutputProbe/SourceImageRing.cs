namespace GpuOutputProbe;

// GPU-free core of the source contract: a bounded ring of decoded images (stamp + slot) with the spec's rules 1-4 and 6.
// The decode side calls Offer/SignalEnd (any thread); the compose side calls SetGeneration/TryAcquire/TryDispose. All state is
// under one lock; nothing blocks. Slots are opaque (a Surface in the engine, an int in tests). `released(slot)` is invoked, under
// the lock, whenever an image leaves the ring and no lease holds it (replaced, retired by generation, ring disposed, or the last
// lease of a retired image returned); it must be cheap and must not call back into the ring.
internal sealed class SourceImageRing<TSlot> : IDisposable
{
    internal sealed class Image(SourceImageStamp stamp, TSlot slot)
    {
        public readonly SourceImageStamp Stamp = stamp;
        public readonly TSlot Slot = slot;
        public int Readers;
        public bool InRing = true;
    }
    private readonly object gate = new();
    private readonly Image?[] ring;
    private readonly Func<TSlot, SourceImageDescription> describe;
    private readonly Action<TSlot>? released;
    private int generation, activeLeases;
    private bool ended, disposed;
    private double endPosition = double.PositiveInfinity;
    private long generationRejected, notReady, replaced, dropped, offered, ready, endedResults;
    public int PeakLeases { get; private set; }
    public int Capacity => ring.Length;
    public SourceImageRing(int capacity, Func<TSlot, SourceImageDescription>? describe = null, Action<TSlot>? released = null)
    {
        if (capacity < 3) throw new ArgumentOutOfRangeException(nameof(capacity), "The source pool holds at least 3 images.");
        ring = new Image?[capacity];
        this.describe = describe ?? (_ => new SourceImageDescription(null, 0, 0, SourceImageFormat.Bgra8));
        this.released = released;
    }
    public int Generation { get { lock (gate) return generation; } }
    public bool Ended { get { lock (gate) return ended; } }
    public int ImageCount { get { lock (gate) return ring.Count(i => i != null); } }
    public SourceDiagnostics Diagnostics(string decoder, string gpu, string format)
    {
        lock (gate) return new(decoder, gpu, format, generationRejected, notReady, replaced, dropped, PeakLeases, offered, ready, endedResults);
    }
    // Rule 1: images of generations before `next` are retired now (released once no lease holds them). The end signal belongs
    // to the generation it was given for, so it is cleared. Going backwards is a caller error; the same value is a no-op.
    public void SetGeneration(int next)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (next < generation) throw new ArgumentOutOfRangeException(nameof(next), "Generations never decrease.");
            if (next == generation) return;
            generation = next; ended = false; endPosition = double.PositiveInfinity;
            for (int i = 0; i < ring.Length; i++)
                if (ring[i] is { } image && image.Stamp.Generation != next) { Retire(i); generationRejected++; }
        }
    }
    // Decode side: the material ends at `endPositionSeconds` (positions at/after it are Ended once no later image exists).
    public void SignalEnd(double endPositionSeconds)
    {
        if (!double.IsFinite(endPositionSeconds)) throw new ArgumentOutOfRangeException(nameof(endPositionSeconds));
        lock (gate) { ThrowIfDisposed(); ended = true; endPosition = endPositionSeconds; }
    }
    // Decode side. The slot must be finished (GPU complete) and not be held by any lease. Returns false when the image is not
    // taken: a non-current generation (counted generationRejected) or every image leased (counted dropped; the newest decode is
    // the one lost, never a leased texture). When the ring is full the oldest un-leased image is replaced (counted replaced).
    public bool Offer(SourceImageStamp stamp, TSlot slot)
    {
        lock (gate)
        {
            ThrowIfDisposed();
            if (stamp.Generation != generation) { generationRejected++; return false; }
            int target = Array.IndexOf(ring, null);
            if (target < 0)
            {
                for (int i = 0; i < ring.Length; i++)
                    if (ring[i]!.Readers == 0 && (target < 0 || ring[i]!.Stamp.Sequence < ring[target]!.Stamp.Sequence)) target = i;
                if (target < 0) { dropped++; return false; }
                Retire(target); replaced++;
            }
            ring[target] = new Image(stamp, slot); offered++;
            return true;
        }
    }
    // Compose side (rules 1-3). Ready: the current-generation image with the greatest position <= positionSeconds, else the first
    // image after it. Ended: end signalled, positionSeconds at/after the end position, and no later image. Otherwise NotReady
    // (including a generation mismatch). Never blocks and never substitutes a black, previous, or error image.
    public SourceStatus TryAcquire(int requestedGeneration, double positionSeconds, out ISourceImageLease? lease)
    {
        lease = null;
        lock (gate)
        {
            ThrowIfDisposed();
            if (requestedGeneration != generation) { notReady++; return SourceStatus.NotReady; }
            Image? best = null, next = null;
            foreach (var image in ring)
            {
                if (image == null) continue;
                if (image.Stamp.PositionSeconds <= positionSeconds) { if (best == null || image.Stamp.PositionSeconds > best.Stamp.PositionSeconds) best = image; }
                else if (next == null || image.Stamp.PositionSeconds < next.Stamp.PositionSeconds) next = image;
            }
            if (next == null && ended && positionSeconds >= endPosition) { endedResults++; return SourceStatus.Ended; }
            var chosen = best ?? next;
            if (chosen == null) { notReady++; return SourceStatus.NotReady; }
            chosen.Readers++; activeLeases++; PeakLeases = Math.Max(PeakLeases, activeLeases); ready++;
            lease = new Lease(this, chosen, describe(chosen.Slot));
            return SourceStatus.Ready;
        }
    }
    // Rule 4: never force-release. False while any lease is outstanding (the caller polls); true once the ring is disposed and
    // every image was released to the decode side.
    public bool TryDispose()
    {
        lock (gate)
        {
            if (disposed) return true;
            if (activeLeases > 0) return false;
            for (int i = 0; i < ring.Length; i++) if (ring[i] != null) Retire(i);
            disposed = true; return true;
        }
    }
    public void Dispose() { if (!TryDispose()) throw new InvalidOperationException("Source images are still leased; return every lease, then dispose."); }
    private void Retire(int index)
    {
        var image = ring[index]!; ring[index] = null; image.InRing = false;
        if (image.Readers == 0) released?.Invoke(image.Slot);
    }
    private void ThrowIfDisposed() { if (disposed) throw new ObjectDisposedException(nameof(SourceImageRing<TSlot>)); }

    public sealed class Lease : ISourceImageLease
    {
        private readonly SourceImageRing<TSlot> ring;
        private readonly Image image;
        private readonly SourceImageDescription description;
        private bool inFlight, disposed;
        internal Lease(SourceImageRing<TSlot> ring, Image image, SourceImageDescription description) { this.ring = ring; this.image = image; this.description = description; }
        public TSlot Slot => image.Slot;
        public SourceImageStamp Stamp => image.Stamp;
        public Vortice.Direct3D11.ID3D11Texture2D? Texture => description.Texture;
        public int Width => description.Width;
        public int Height => description.Height;
        public SourceImageFormat Format => description.Format;
        public void BeginGpuUse() { if (disposed || inFlight) throw new InvalidOperationException("Lease is returned or already in GPU use."); inFlight = true; }
        public void CompleteGpuUse() { if (disposed || !inFlight) throw new InvalidOperationException("No active GPU use."); inFlight = false; }
        public void Dispose()
        {
            lock (ring.gate)
            {
                if (inFlight) throw new InvalidOperationException("GPU use must complete before lease return.");
                if (disposed) return;
                disposed = true; image.Readers--; ring.activeLeases--;
                if (!image.InRing && image.Readers == 0) ring.released?.Invoke(image.Slot);
            }
        }
    }
}
