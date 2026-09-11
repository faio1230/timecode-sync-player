using System.Collections.Concurrent;
using System.Diagnostics;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// UI が Retain した RenderedFrameSnapshot を GPU worker へ渡す最新1件の mailbox。
/// 置き換え時と Dispose 時に古いフレームの lease を返す。
/// </summary>
internal sealed class SnapshotInputMailbox : IDisposable
{
    private readonly object gate = new();
    private readonly AutoResetEvent ready = new(false);
    private RenderedFrameSnapshot? latest;
    private double positionSeconds;
    private int generation;
    private bool disposed;

    /// <summary>GPU worker の空き時間待ちで使う到着通知。Publish ごとに Set する。</summary>
    public WaitHandle ReadyHandle => ready;

    public void Publish(RenderedFrameSnapshot frame, int generation, double positionSeconds)
    {
        RenderedFrameSnapshot? replaced;
        lock (gate)
        {
            if (disposed) { replaced = frame; }
            else
            {
                replaced = latest;
                latest = frame;
                this.generation = generation;
                this.positionSeconds = positionSeconds;
            }
        }
        replaced?.Dispose();
        if (!disposed) ready.Set();
    }

    public bool TryTake(out RenderedFrameSnapshot? frame, out int generation, out double positionSeconds)
    {
        lock (gate)
        {
            frame = latest;
            generation = this.generation;
            positionSeconds = this.positionSeconds;
            latest = null;
            return frame != null;
        }
    }

    public void Dispose()
    {
        RenderedFrameSnapshot? pending;
        lock (gate)
        {
            disposed = true;
            pending = latest;
            latest = null;
        }
        pending?.Dispose();
        ready.Dispose();
    }
}

/// <summary>
/// MpvSnapshotSource。RenderedFrameSnapshot（BGR0 の配列）を GPU worker でアップロードし、
/// 3 枚のリングで合成層へ供給する。ソース契約（docs/GPU-SOURCE-CONTRACT-SPEC.md）の
/// 世代排除・最新優先・有限 lease・非ブロッキングを満たす。
/// アップロードと describe は注入し、GPU 無しで契約テストできるようにする。
/// </summary>
internal sealed class MpvSnapshotSource<TSlot> : IVideoSource
{
    private readonly SourceImageRing<TSlot> ring;
    private readonly ConcurrentQueue<TSlot> free = new();
    private readonly Action<TSlot, byte[], int, int> upload;
    private readonly Func<TSlot, SourceImageDescription> describe;
    private readonly string decoder, gpu;
    private long sequence, uploaded, dropped;

    // リングは slots.Count - 1 枚を保持し、残り 1 枚を描画先に確保する（試作 FakeVideoSource と同じ）。
    // これで全画像が lease 中でもアップロード先が残り、置換・破棄の規則を守れる。
    public MpvSnapshotSource(
        IReadOnlyList<TSlot> slots,
        Action<TSlot, byte[], int, int> upload,
        Func<TSlot, SourceImageDescription>? describe = null,
        string decoder = "mpv-bgra",
        string gpu = "")
    {
        if (slots.Count < 4) throw new ArgumentOutOfRangeException(nameof(slots), "The snapshot pool needs at least 4 slots (ring 3 + one render slot).");
        ring = new SourceImageRing<TSlot>(slots.Count - 1, describe, free.Enqueue);
        foreach (var slot in slots) free.Enqueue(slot);
        this.upload = upload;
        this.describe = describe ?? (_ => new SourceImageDescription(null, 0, 0, SourceImageFormat.Bgra8));
        this.decoder = decoder; this.gpu = gpu;
    }

    public long Uploaded => Volatile.Read(ref uploaded);
    public long DroppedUploads => Volatile.Read(ref dropped);

    /// <summary>リング slot からテクスチャ等を取り出す（合成層が描画に使う）。</summary>
    public TSlot SlotOf(ISourceImageLease lease) => ((SourceImageRing<TSlot>.Lease)lease).Slot;

    /// <summary>
    /// GPU worker 専用。Retain 済みフレームを現世代のリングへアップロードして Offer する。
    /// 世代不一致・全 slot lease 中は捨てる（新しいデコード結果を守る）。frame はここで返却する。
    /// アップロード完了後に lease を返す（契約どおり）。
    /// </summary>
    public bool TryUpload(RenderedFrameSnapshot frame, int generation, double positionSeconds)
    {
        try
        {
            if (generation != ring.Generation) { dropped++; return false; }
            if (!free.TryDequeue(out TSlot? slot)) { dropped++; return false; }
            upload(slot!, frame.Pixels, frame.Width, frame.Height);
            var stamp = new SourceImageStamp(generation, ++sequence, positionSeconds, Stopwatch.GetTimestamp());
            if (!ring.Offer(stamp, slot!)) { free.Enqueue(slot!); dropped++; return false; }
            uploaded++;
            return true;
        }
        finally
        {
            frame.Dispose();
        }
    }

    public void SetGeneration(int generation) => ring.SetGeneration(generation);
    public SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease)
        => ring.TryAcquire(generation, positionSeconds, out lease);
    public SourceDiagnostics Diagnostics => ring.Diagnostics(decoder, gpu, "BGRA8_UNORM");
    public bool TryDispose() => ring.TryDispose();
    public void Dispose() => ring.Dispose();
}
