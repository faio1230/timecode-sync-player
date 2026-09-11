namespace TimecodeSyncPlayer.Output;

/// <summary>合成画像の世代情報。試作 GpuOutputProbe の ImageStamp を移植。</summary>
internal readonly record struct ImageStamp(long Id, long GeneratedQpc);

/// <summary>
/// 合成画像の pool。3枚で最新のみを公開し、読者は lease を取る。
/// 試作 scripts/GpuOutputProbe の LatestPool を移植（docs/OUTPUT-GPU-DESIGN-CONFIRMED.md 参照）。
/// </summary>
internal sealed class LatestPool(int count)
{
    private readonly object gate = new();
    private readonly bool[] writing = new bool[count];
    private readonly int[] readers = new int[count];
    private readonly ImageStamp[] stamps = new ImageStamp[count];
    private int latest = -1;
    public int PeakReaders { get; private set; }
    public int PeakOccupied { get; private set; }
    public int Capacity => count;

    public int TryBeginWrite()
    {
        lock (gate)
        {
            for (int i = 0; i < count; i++)
                if (i != latest && !writing[i] && readers[i] == 0) { writing[i] = true; Track(); return i; }
            return -1;
        }
    }

    public void Publish(int slot, ImageStamp stamp, bool gpuComplete)
    {
        lock (gate)
        {
            if (!writing[slot] || !gpuComplete) throw new InvalidOperationException("Cannot publish incomplete image.");
            writing[slot] = false; stamps[slot] = stamp; latest = slot; Track();
        }
    }

    public void AbortWrite(int slot, bool gpuComplete)
    {
        lock (gate)
        {
            if (!writing[slot] || !gpuComplete) throw new InvalidOperationException("Write not active or GPU still owns write.");
            writing[slot] = false;
        }
    }

    public long LatestId { get { lock (gate) return latest < 0 ? 0 : stamps[latest].Id; } }

    /// <summary>現在保持されている lease 数（キャンバス変更時の旧世代破棄判断に使う）。</summary>
    public int ActiveReaders { get { lock (gate) return readers.Sum(); } }

    public Lease? AcquireLatest()
    {
        lock (gate)
        {
            if (latest < 0) return null;
            readers[latest]++; Track(); return new Lease(this, latest, stamps[latest]);
        }
    }

    private void Track()
    {
        PeakReaders = Math.Max(PeakReaders, readers.Sum());
        PeakOccupied = Math.Max(PeakOccupied, Enumerable.Range(0, count).Count(i => writing[i] || readers[i] > 0 || latest == i));
    }

    public sealed class Lease(LatestPool pool, int slot, ImageStamp stamp) : IDisposable
    {
        public int Slot { get; } = slot;
        public ImageStamp Stamp { get; } = stamp;
        private bool inFlight, disposed;
        public void BeginGpuUse() { if (disposed || inFlight) throw new InvalidOperationException(); inFlight = true; }
        public void CompleteGpuUse() { if (disposed || !inFlight) throw new InvalidOperationException("No active GPU use."); inFlight = false; }
        public void Dispose()
        {
            lock (pool.gate)
            {
                if (inFlight) throw new InvalidOperationException("GPU use must complete before lease return.");
                if (!disposed) { pool.readers[Slot]--; disposed = true; }
            }
        }
    }
}
