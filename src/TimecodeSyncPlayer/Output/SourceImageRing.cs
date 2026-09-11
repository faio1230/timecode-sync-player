using TimecodeSyncPlayer.Contracts;
using Vortice.Direct3D11;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// ソース契約の GPU 非依存核。stamp + slot の有限リングで規則 1〜4・6 を実装する。
/// デコード側は Offer/SignalEnd（任意スレッド）、合成側は SetGeneration/TryAcquire/TryDispose。
/// すべて単一ロックで、ブロックしない。slot は不透明（エンジンでは Surface、テストでは int）。
/// released(slot) は画像がリングから外れ lease が無くなったときロック内で呼ぶ（安価で再入しないこと）。
/// 試作 scripts/GpuOutputProbe の SourceImageRing を移植。
/// </summary>
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

    // 規則1: next より前の世代は即時退避（lease が返り次第 released）。end 信号は世代に属するためクリアする。
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

    // デコード側: 素材が endPositionSeconds で終わる（それ以降の位置は後続画像が無ければ Ended）。
    public void SignalEnd(double endPositionSeconds)
    {
        if (!double.IsFinite(endPositionSeconds)) throw new ArgumentOutOfRangeException(nameof(endPositionSeconds));
        lock (gate) { ThrowIfDisposed(); ended = true; endPosition = endPositionSeconds; }
    }

    // デコード側。slot は GPU 完了済みでどの lease も持たないこと。リングが満杯なら最も古い未 lease を置換。
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

    // 合成側（規則1〜3）。Ready: 現世代で positionSeconds 以下で最大位置、無ければ直後の最初の画像。
    // Ended: end 通知済みで position が末尾以降かつ後続画像が無い。それ以外は NotReady。
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

    // 規則4: 強制解放しない。outstanding が残る間 false。
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
        public ID3D11Texture2D? Texture => description.Texture;
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
