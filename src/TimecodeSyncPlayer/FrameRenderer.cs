using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace TimecodeSyncPlayer;

/// <summary>
/// WriteableBitmap を保持し、各種レンダリングポリシーを実行する。
/// BitmapChanged イベントで呼び出し元が VideoImage.Source を更新する。
/// UIスレッド上でのみ呼び出すこと。
/// </summary>
internal sealed class FrameRenderer
{
    private readonly PixelBufferManager _bufferManager;
    private readonly ISpoutOutput _spoutOutput;
    private readonly SyncAccuracyTrace _accuracyTrace;
    private WriteableBitmap? _bitmap;
    private const double DefaultDpi = 96;

    public FrameRenderer(PixelBufferManager bufferManager, ISpoutOutput spoutOutput, SyncAccuracyTrace? accuracyTrace = null)
    {
        _bufferManager = bufferManager;
        _spoutOutput   = spoutOutput;
        _accuracyTrace = accuracyTrace ?? SyncAccuracyTrace.Current;
    }

    /// <summary>WriteableBitmap が新規作成またはリサイズされたときに発火する。</summary>
    public event Action<WriteableBitmap>? BitmapChanged;

    /// <summary>_bufferManager.PixelBuffer の内容を WriteableBitmap に書き込む（RenderFrame 用）。</summary>
    public void UpdateFromPixelBuffer(int w, int h)
        => UpdateFromPixelBuffer(w, h, "normal");

    private void UpdateFromPixelBuffer(int w, int h, string kind)
    {
        if (_bufferManager.PixelBuffer is { } pixels) UpdateFromPixels(pixels, w, h, kind);
    }

    /// <summary>Copies a caller-owned snapshot directly to the bitmap during its UI lease.</summary>
    public void UpdateFromPixels(byte[] pixels, int w, int h) => UpdateFromPixels(pixels, w, h, "normal");

    private void UpdateFromPixels(byte[] pixels, int w, int h, string kind)
    {
        if (!FrameBufferSize.TryGetRequiredByteCount(w, h, out int byteCount)) return;
        // Consume before EnsureBitmap, whose BitmapChanged callback may reenter.
        var timing = _accuracyTrace.IsEnabled ? BitmapRenderTraceScope.Take(_accuracyTrace, w, h) : null;
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

    /// <summary>黒フレームを描画して Spout 送信する。</summary>
    public void RenderBlack(int videoWidth, int videoHeight)
    {
        (int w, int h) = BlackFrameRenderPolicy.ResolveSize(videoWidth, videoHeight);
        _bufferManager.EnsurePixelBuffer(w, h);
        _bufferManager.ClearPixelBuffer();
        UpdateFromPixelBuffer(w, h, "black");
        _spoutOutput.SendFrame(_bufferManager.PixelPtr, w, h);
    }

    /// <summary>FrozenFrameBuffer の内容を描画する。利用不可なら黒フレームにフォールバック。</summary>
    public void RenderFrozen(int videoWidth, int videoHeight)
    {
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
        EnsureBitmap(w, h);
        _bitmap!.Lock();
        try
        {
            Marshal.Copy(_bufferManager.FrozenFrameBuffer, 0, _bitmap.BackBuffer, frameNeeded);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, w, h));
        }
        finally
        {
            _bitmap.Unlock();
        }
        RecordPublication("frozen");
        _spoutOutput.SendFrame(_bufferManager.FrozenFramePtr, w, h);
    }

    /// <summary>CachedGapFreezeFrame があれば描画、なければ FrozenFrame にフォールバック。</summary>
    public void RenderGapFreeze(int videoWidth, int videoHeight)
    {
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
        if (!FrameBufferSize.TryGetRequiredByteCount(width, height, out int frameNeeded)) return;
        if (buffer.Length < frameNeeded)
            return;
        EnsureBitmap(width, height);
        _bitmap!.Lock();
        try
        {
            Marshal.Copy(buffer, 0, _bitmap.BackBuffer, frameNeeded);
            _bitmap.AddDirtyRect(new System.Windows.Int32Rect(0, 0, width, height));
        }
        finally
        {
            _bitmap.Unlock();
        }
        RecordPublication("buffered");
        if (handle != IntPtr.Zero)
            _spoutOutput.SendFrame(handle, width, height);
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
