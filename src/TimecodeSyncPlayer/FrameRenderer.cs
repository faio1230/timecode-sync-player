using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// WriteableBitmap を保持し、各種レンダリングポリシーを実行する。
/// BitmapChanged は全解像度の外部表示面を通知する。主画面プレビューは別経路。
/// UIスレッド上でのみ呼び出すこと。
/// </summary>
internal sealed class FrameRenderer
{
    private readonly PixelBufferManager _bufferManager;
    private readonly ISpoutOutput _spoutOutput;
    private readonly SyncAccuracyTrace _accuracyTrace;
    private readonly Action<WriteableBitmap, string>? _queuePreview;
    private readonly Action? _cancelPreview;
    private readonly Action<WriteableBitmap> _unlockCombinedBitmap;
    private readonly Func<WriteableBitmap, bool> _tryLockCombinedBitmap;
    private bool _combinedPublicationActive;
    private WriteableBitmap? _bitmap;
    private const double DefaultDpi = 96;

    public FrameRenderer(PixelBufferManager bufferManager, ISpoutOutput spoutOutput, SyncAccuracyTrace? accuracyTrace = null,
        Action<WriteableBitmap, string>? queuePreview = null, Action? cancelPreview = null,
        Action<WriteableBitmap>? unlockCombinedBitmap = null,
        Func<WriteableBitmap, bool>? tryLockCombinedBitmap = null)
    {
        _bufferManager = bufferManager;
        _spoutOutput   = spoutOutput;
        _accuracyTrace = accuracyTrace ?? SyncAccuracyTrace.Current;
        _queuePreview = queuePreview;
        _cancelPreview = cancelPreview;
        _unlockCombinedBitmap = unlockCombinedBitmap ?? (bitmap => bitmap.Unlock());
        _tryLockCombinedBitmap = tryLockCombinedBitmap ?? (bitmap => bitmap.TryLock(new System.Windows.Duration(TimeSpan.Zero)));
    }

    /// <summary>WriteableBitmap が新規作成またはリサイズされたときに発火する。</summary>
    public event Action<WriteableBitmap>? BitmapChanged;
    public WriteableBitmap? CurrentBitmap => _bitmap;

    /// <summary>
    /// UI thread only, after full-resolution publication. Notify the latest
    /// external bitmap; the presenter reads it only on a later preview tick.
    /// No pixel copy, pointer retention, lock or write occurs in this notification.
    /// </summary>
    public void QueuePreviewFromCurrentBitmap(string kind)
    {
        if (_queuePreview == null || _bitmap == null) return;
        try
        {
            _queuePreview(_bitmap, kind);
        }
        catch (Exception ex)
        {
            try { Log.Warning(ex, "Preview preparation failed after external bitmap publication"); }
            catch (Exception) { /* A logging failure must not interrupt external output. */ }
        }
    }

    /// <summary>_bufferManager.PixelBuffer の内容を WriteableBitmap に書き込む（RenderFrame 用）。</summary>
    public void UpdateFromPixelBuffer(int w, int h)
        => UpdateFromPixelBuffer(w, h, "normal");

    private void UpdateFromPixelBuffer(int w, int h, string kind)
    {
        ThrowIfCombinedPublicationActive();
        if (_bufferManager.PixelBuffer is { } pixels) UpdateFromPixels(pixels, w, h, kind);
    }

    /// <summary>Copies a caller-owned snapshot directly to the bitmap during its UI lease.</summary>
    public void UpdateFromPixels(byte[] pixels, int w, int h) => UpdateFromPixels(pixels, w, h, "normal");

    private void UpdateFromPixels(byte[] pixels, int w, int h, string kind, bool useBitmapStageTrace = true)
    {
        ThrowIfCombinedPublicationActive();
        try { UpdateFromPixelsCore(pixels, w, h, kind, useBitmapStageTrace); }
        catch { CancelPendingPreview(); throw; }
    }

    private void CancelPendingPreview()
    {
        try { _cancelPreview?.Invoke(); }
        catch (Exception ex)
        {
            try { Log.Warning(ex, "Preview cancellation failed after external frame failure"); }
            catch (Exception) { }
        }
    }

    private void SendFrameWithPreviewCancellation(IntPtr pixels, int width, int height)
    {
        try { _spoutOutput.SendFrame(pixels, width, height); }
        catch { CancelPendingPreview(); throw; }
    }

    private void UpdateFromPixelsCore(byte[] pixels, int w, int h, string kind, bool useBitmapStageTrace)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int byteCount)) return;
        // Consume before EnsureBitmap, whose BitmapChanged callback may reenter.
        var timing = useBitmapStageTrace && _accuracyTrace.IsEnabled ? BitmapRenderTraceScope.Take(_accuracyTrace, w, h) : null;
        EnsureBitmap(w, h);
        if (timing != null)
        {
            UpdateBitmapTraced(pixels, w, h, byteCount, timing);
            RecordPublication(kind);
            return;
        }
        _bitmap!.Lock();
        try
        {
            byteCount = Math.Min(pixels.Length, byteCount);
            Marshal.Copy(pixels, 0, _bitmap.BackBuffer, byteCount);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bitmap.Unlock();
        }
        RecordPublication(kind);
    }

    private void UpdateBitmapTraced(byte[] pixels, int w, int h, int byteCount, BitmapRenderTraceScope timing)
    {
        long lockStart = Stopwatch.GetTimestamp(), lockEnd = 0;
        long copyStart = 0, copyEnd = 0, unlockStart = 0, unlockEnd = 0;
        bool locked = false, copied = false, unlocked = false;
        try
        {
            try { _bitmap!.Lock(); locked = true; }
            finally { lockEnd = Stopwatch.GetTimestamp(); }
            try
            {
                copyStart = Stopwatch.GetTimestamp();
                byteCount = Math.Min(pixels.Length, byteCount);
                Marshal.Copy(pixels, 0, _bitmap!.BackBuffer, byteCount);
                _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
                copied = true;
            }
            finally
            {
                copyEnd = Stopwatch.GetTimestamp();
                unlockStart = Stopwatch.GetTimestamp();
                try { _bitmap!.Unlock(); unlocked = true; }
                finally { unlockEnd = Stopwatch.GetTimestamp(); }
            }
        }
        finally
        {
            // Defer event allocation/queueing until Unlock has been attempted
            // (or Lock failed). Intervals exclude observer work, unlike bitmap.
            timing.Record("bitmap-lock", locked, lockStart, lockEnd);
            if (copyStart != 0) timing.Record("bitmap-copy-dirty", copied, copyStart, copyEnd);
            if (unlockStart != 0) timing.Record("bitmap-unlock", unlocked, unlockStart, unlockEnd);
        }
    }

    /// <summary>
    /// A synchronous normal-frame candidate: if WPF is busy, send the snapshot
    /// before waiting for its lock; otherwise copy and send while holding it.
    /// BitmapMs sums disjoint WPF intervals, excluding send in either branch.
    /// </summary>
    public (double BitmapMs, double SpoutMs) UpdateFromPixelsWithSpout(byte[] pixels, int w, int h, Func<double> send)
    {
        ThrowIfCombinedPublicationActive();
        ArgumentNullException.ThrowIfNull(send);
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int byteCount))
            throw new ArgumentOutOfRangeException(nameof(w), "Invalid bitmap dimensions.");
        var timing = _accuracyTrace.IsEnabled ? BitmapRenderTraceScope.Take(_accuracyTrace, w, h) : null;
        _combinedPublicationActive = true;
        try
        {
            ArgumentNullException.ThrowIfNull(pixels);
            // BitmapChanged observers run here, before taking the WPF lock.
            EnsureBitmap(w, h);
            WriteableBitmap bitmap = _bitmap!;
            long tryStart = Stopwatch.GetTimestamp(), tryEnd = 0, lockStart = 0, lockEnd = 0;
            long copyStart = 0, copyEnd = 0, unlockStart = 0, unlockEnd = 0;
            bool locked = false, copied = false, unlocked = false;
            string tryOutcome = "exception";
            bool sentBeforeLock = false;
            double spoutMs = 0;
            Exception? failure = null;
            try
            {
                try
                {
                    locked = _tryLockCombinedBitmap(bitmap);
                    tryOutcome = locked ? "acquired" : "busy";
                }
                finally { tryEnd = Stopwatch.GetTimestamp(); }
                if (!locked)
                {
                    spoutMs = send();
                    sentBeforeLock = true;
                    lockStart = Stopwatch.GetTimestamp();
                    try { bitmap.Lock(); locked = true; }
                    finally { lockEnd = Stopwatch.GetTimestamp(); }
                }
                copyStart = Stopwatch.GetTimestamp();
                try
                {
                    Marshal.Copy(pixels, 0, bitmap.BackBuffer, Math.Min(pixels.Length, byteCount));
                    bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
                    copied = true;
                }
                finally { copyEnd = Stopwatch.GetTimestamp(); }
                if (!sentBeforeLock) spoutMs = send();
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                if (locked)
                {
                    unlockStart = Stopwatch.GetTimestamp();
                    try { _unlockCombinedBitmap(bitmap); unlocked = true; }
                    catch (Exception ex)
                    {
                        failure = failure == null ? ex : new AggregateException("Frame publication and bitmap Unlock both failed", failure, ex);
                    }
                    finally { unlockEnd = Stopwatch.GetTimestamp(); }
                }
                // Bitmap stage observers run only after Unlock has been attempted.
                timing?.Record("bitmap-try-lock", tryOutcome, tryStart, tryEnd);
                if (lockStart != 0) timing?.Record("bitmap-lock", locked, lockStart, lockEnd);
                if (copyStart != 0) timing?.Record("bitmap-copy-dirty", copied, copyStart, copyEnd);
                if (unlockStart != 0) timing?.Record("bitmap-unlock", unlocked, unlockStart, unlockEnd);
            }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            RecordPublication("normal"); // Full-resolution frame time remains after actual Unlock.
            long bitmapTicks = tryEnd - tryStart + lockEnd - lockStart + copyEnd - copyStart + unlockEnd - unlockStart;
            return (bitmapTicks * 1000.0 / Stopwatch.Frequency, spoutMs);
        }
        catch { CancelPendingPreview(); throw; }
        finally { _combinedPublicationActive = false; }
    }

    private void ThrowIfCombinedPublicationActive()
    {
        if (_combinedPublicationActive)
            throw new InvalidOperationException("A combined bitmap/send callback must not reenter the same FrameRenderer.");
    }

    /// <summary>黒フレームを描画して Spout 送信する。</summary>
    public void RenderBlack(int videoWidth, int videoHeight)
    {
        ThrowIfCombinedPublicationActive();
        (int w, int h) = BlackFrameRenderPolicy.ResolveSize(videoWidth, videoHeight);
        _bufferManager.EnsurePixelBuffer(w, h);
        _bufferManager.ClearPixelBuffer();
        UpdateFromPixelBuffer(w, h, "black");
        SendFrameWithPreviewCancellation(_bufferManager.PixelPtr, w, h);
        QueuePreviewFromCurrentBitmap("black");
    }

    /// <summary>FrozenFrameBuffer の内容を描画する。利用不可なら黒フレームにフォールバック。</summary>
    public void RenderFrozen(int videoWidth, int videoHeight)
    {
        ThrowIfCombinedPublicationActive();
        if (_bufferManager.FrozenFrameBuffer == null || videoWidth <= 0 || videoHeight <= 0)
        {
            Log.Debug("Continue mode: frozen frame unavailable, rendering black frame");
            RenderBlack(videoWidth, videoHeight);
            return;
        }
        int w = videoWidth;
        int h = videoHeight;
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int frameNeeded)) return;
        if (_bufferManager.FrozenFrameBuffer!.Length < frameNeeded)
        {
            RenderBlack(videoWidth, videoHeight);
            return;
        }
        // The legacy frozen/buffered routes never consumed normal-publication attribution.
        UpdateFromPixels(_bufferManager.FrozenFrameBuffer, w, h, "frozen", useBitmapStageTrace: false);
        SendFrameWithPreviewCancellation(_bufferManager.FrozenFramePtr, w, h);
        QueuePreviewFromCurrentBitmap("frozen");
    }

    /// <summary>CachedGapFreezeFrame があれば描画、なければ FrozenFrame にフォールバック。</summary>
    public void RenderGapFreeze(int videoWidth, int videoHeight)
    {
        ThrowIfCombinedPublicationActive();
        if (_bufferManager.CachedGapFreezeFrameBuffer != null &&
            _bufferManager.CachedGapFreezeFrameWidth > 0 &&
            _bufferManager.CachedGapFreezeFrameHeight > 0)
        {
            RenderBuffered(
                _bufferManager.CachedGapFreezeFrameBuffer,
                _bufferManager.CachedGapFreezeFramePtr,
                _bufferManager.CachedGapFreezeFrameWidth,
                _bufferManager.CachedGapFreezeFrameHeight);
            return;
        }
        RenderFrozen(videoWidth, videoHeight);
    }

    /// <summary>任意のバイト配列を描画して Spout 送信する。</summary>
    public void RenderBuffered(byte[] buffer, IntPtr handle, int width, int height)
    {
        ThrowIfCombinedPublicationActive();
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int frameNeeded)) return;
        if (buffer.Length < frameNeeded)
            return;
        UpdateFromPixels(buffer, width, height, "buffered", useBitmapStageTrace: false);
        if (handle != IntPtr.Zero)
            SendFrameWithPreviewCancellation(handle, width, height);
        QueuePreviewFromCurrentBitmap("buffered");
    }

    private void RecordPublication(string kind)
    {
        if (!_accuracyTrace.IsEnabled) return;
        long publishedTicks = Stopwatch.GetTimestamp();
        // Read the pixels just copied into the bitmap, including partial-source-buffer cases.
        // The bitmap stays alive on this UI thread; the probe performs no full-frame copy.
        _accuracyTrace.RecordFrame(kind, _bitmap!.BackBuffer, _bitmap.PixelWidth,
            _bitmap.PixelHeight, _bitmap.BackBufferStride, publishedTicks);
    }

    private void EnsureBitmap(int w, int h)
    {
        if (_bitmap != null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h) return;
        _bitmap = new WriteableBitmap(w, h, DefaultDpi, DefaultDpi, PixelFormats.Bgr32, null);
        BitmapChanged?.Invoke(_bitmap);
    }
}
