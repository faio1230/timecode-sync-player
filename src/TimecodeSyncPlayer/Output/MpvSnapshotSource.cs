using System.Collections.Concurrent;
using System.Diagnostics;
using TimecodeSyncPlayer.Contracts;

namespace TimecodeSyncPlayer.Output;

/// <summary>
/// アップロードの GPU コピー完了確認。完了まではリングへ公開しないための、ブロックしない確認。
/// 試作の GpuFence と違い待たず、GetData(DoNotFlush) だけで判定する。
/// </summary>
internal interface IUploadCompletion : IDisposable
{
    bool TryComplete();
}

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
/// アップロードは専用の完了クエリで GPU コピー完了を確認してからリングへ Offer し、
/// 完了までは TryAcquire に出さない（合成のフェンス待ちにアップロードを含めない）。
/// </summary>
internal sealed class MpvSnapshotSource<TSlot> : IVideoSource
{
    private readonly SourceImageRing<TSlot> ring;
    private readonly ConcurrentQueue<TSlot> free = new();
    private readonly Queue<Pending> pending = new();
    private readonly Func<TSlot, byte[], int, int, IUploadCompletion?> upload;
    private readonly Func<TSlot, SourceImageDescription> describe;
    private readonly string decoder, gpu;
    private long sequence, uploaded, dropped;

    private sealed class Pending(SourceImageStamp stamp, TSlot slot, IUploadCompletion? completion)
    {
        public readonly SourceImageStamp Stamp = stamp;
        public readonly TSlot Slot = slot;
        public readonly IUploadCompletion? Completion = completion;
    }

    // リングは slots.Count - 1 枚を保持し、残り 1 枚を描画先に確保する（試作 FakeVideoSource と同じ）。
    // これで全画像が lease 中でもアップロード先が残り、置換・破棄の規則を守れる。
    public MpvSnapshotSource(
        IReadOnlyList<TSlot> slots,
        Func<TSlot, byte[], int, int, IUploadCompletion?> upload,
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
    public int PendingUploads { get { lock (pending) return pending.Count; } }

    /// <summary>リング slot からテクスチャ等を取り出す（合成層が描画に使う）。</summary>
    public TSlot SlotOf(ISourceImageLease lease) => ((SourceImageRing<TSlot>.Lease)lease).Slot;

    /// <summary>
    /// GPU worker 専用。Retain 済みフレームを現世代のリングへアップロードキューへ積む。
    /// 世代不一致・全 slot 使用中は捨てる。frame はここで返却する。
    /// GPU コピー完了は完了クエリで確認し、完了した画像だけが TryAcquire に出る。
    /// </summary>
    public bool TryUpload(RenderedFrameSnapshot frame, int generation, double positionSeconds)
    {
        try
        {
            PollUploads();
            if (generation != ring.Generation) { dropped++; return false; }
            if (!free.TryDequeue(out TSlot? slot)) { dropped++; return false; }
            var stamp = new SourceImageStamp(generation, ++sequence, positionSeconds, Stopwatch.GetTimestamp());
            IUploadCompletion? completion;
            try { completion = upload(slot!, frame.Pixels, frame.Width, frame.Height); }
            catch
            {
                free.Enqueue(slot!);
                throw;
            }
            lock (pending) pending.Enqueue(new Pending(stamp, slot!, completion));
            PollUploads();
            return true;
        }
        finally
        {
            frame.Dispose();
        }
    }

    /// <summary>
    /// GPU worker 専用。完了したアップロードだけを順序どおりリングへ公開する（ブロックしない）。
    /// 世代が変わっていた場合は slot を free へ戻して破棄する。
    /// </summary>
    public void PollUploads()
    {
        while (true)
        {
            Pending item;
            lock (pending)
            {
                if (pending.Count == 0) return;
                item = pending.Peek();
            }
            if (item.Completion != null && !item.Completion.TryComplete()) return;
            lock (pending) pending.Dequeue();
            item.Completion?.Dispose();
            if (!ring.Offer(item.Stamp, item.Slot)) { dropped++; free.Enqueue(item.Slot); }
            else uploaded++;
        }
    }

    /// <summary>
    /// 停止時（GPU ドレイン後）。完了待ちのクエリと保持中のリースを解放する。
    /// GStreamerSource の全 lease を shim player destroy より先に返すために使う。
    /// </summary>
    public void DrainPendingForStop()
    {
        lock (pending)
        {
            while (pending.Count > 0) pending.Dequeue().Completion?.Dispose();
        }
    }

    public void SetGeneration(int generation) => ring.SetGeneration(generation);
    public SourceStatus TryAcquire(int generation, double positionSeconds, out ISourceImageLease? lease)
        => ring.TryAcquire(generation, positionSeconds, out lease);
    public SourceDiagnostics Diagnostics => ring.Diagnostics(decoder, gpu, "BGRA8_UNORM");
    public bool TryDispose() => ring.TryDispose();

    public void Dispose()
    {
        lock (pending)
        {
            while (pending.Count > 0) pending.Dequeue().Completion?.Dispose();
        }
        ring.Dispose();
    }
}
